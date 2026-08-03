#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Network.Telemetry;
using RemotePlayServer.Server;
using VideoCodec = RemotePlayServer.Core.VideoCodec;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    /// <summary>
    /// Relay-media fallback: bring up the encode/audio pipeline WITHOUT a WebRTC
    /// PeerConnection (no ICE/DTLS). Frames then flow through OnRelayVideoFrame /
    /// OnRelayAudioChunk. Idempotent — safe to call once ICE-restart budget is spent.
    /// The caller (PhaseProtocolHandler) drives capture via StartCaptureThread().
    /// </summary>
    public void StartRelayMediaMode()
    {
        if (RelayMediaMode) return;
        Logger.Info("[SIPSorcery] Entering relay-media mode (encode pipeline without WebRTC)");
        _running = true;
        RelayMediaMode = true;
        InitializeEncoders();
        InitializeAudio();

        // Backpressure v1: the relay path is TCP + a server hop, so it can't sustain the
        // P2P bitrate. Cap conservatively and force an immediate keyframe so the client
        // gets a clean decodable start (no bufferedAmount signal to drive drops here).
        try { ForceSetBitrate(RelayMediaBitrateKbps); } catch { }
        try { RequestKeyframe(force: true); } catch { }

        // The relay path has no DTLS/start_streaming handshake to trigger the normal
        // Phase-3 activation, so activate it here — otherwise PushBgraTexture/PushTexture
        // keep dropping every frame until the (possibly never-completing) WebRTC Phase 3
        // flow runs. ResetSyncState gives a clean time origin + forces an IDR first frame.
        ResetSyncState();
        ActivatePhase3();
    }

    /// <summary>Conservative per-session bitrate cap while media flows over the TCP relay.</summary>
    private const int RelayMediaBitrateKbps = 6000;

    /// <summary>Leave relay-media mode when a WebRTC P2P path recovers (auto-upgrade).</summary>
    public void StopRelayMediaMode()
    {
        if (!RelayMediaMode) return;
        Logger.Info("[SIPSorcery] Leaving relay-media mode — WebRTC path resumed");
        RelayMediaMode = false;
    }

    private void InitializeEncoders()
    {
        // Detect GPU vendor once for all tracks
        var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();
        Logger.Info($"[SIPSorcery] Detected GPU vendor: {gpuVendor}");

        lock (_lock)
        {
            foreach (var track in _tracks)
            {
                lock (track.EncodeLock)
                {
                    EnsureEncoderMatchesResolution(track, track.Width, track.Height, gpuVendor);
                }
            }
        }
    }

    private void EnsureEncoderMatchesResolution(TrackInfo track, int width, int height, GpuVendorDetector.GpuVendor? gpuVendor = null)
    {
        // Skip invalid dims (paused tracks with invalidated dimensions — will be set on next real frame)
        if (width <= 0 || height <= 0) return;

        // Must be called with track.EncodeLock held
        // Compare with actual encoder codec (LastUsedCodec), not negotiated codec.
        // After fallback, LastUsedCodec may differ from _negotiatedCodec (e.g., H264 vs H265).
        var actualCodec = track.Encoder?.CurrentCodec ?? track.LastUsedCodec;
        if (track.Encoder != null && track.Encoder.Width == width && track.Encoder.Height == height && track.LastUsedCodec == actualCodec)
        {
            if (track.Width != width || track.Height != height)
            {
                track.Width = width;
                track.Height = height;
            }
            return;
        }

        string reason = (track.Encoder != null && track.LastUsedCodec != actualCodec) ? "Codec changed" : "Dimensions changed";
        Logger.Info($"[SIPSorcery] Track {track.Index}: {reason} ({track.LastUsedCodec} -> {actualCodec}) or ({track.Encoder?.Width ?? 0}x{track.Encoder?.Height ?? 0} -> {width}x{height}), recreating encoder");

        track.Width = width;
        track.Height = height;

        if (track.Encoder != null)
        {
            track.Encoder.Dispose();
            track.Encoder = null;
        }

        // Reset frame counters on resolution or codec change
        Interlocked.Exchange(ref track.SentFrames, 0);
        Interlocked.Exchange(ref track.EncodedFrames, 0);
        track.IdrViaDcCount = 0; // Force re-send codec config (VPS/SPS/PPS) on next IDR
        track.DcNotReadyCount = 0;

        var device = track.Device ?? _sharedDevice;
        if (device == null)
        {
            Logger.Info($"[SIPSorcery] Track {track.Index}: No D3D11 device, cannot initialize encoder");
            return;
        }

        var vendor = gpuVendor ?? GpuVendorDetector.DetectPrimaryGpuVendor();
        track.Encoder = TryInitializeEncoderWithFallback(track, device, vendor);
        if (track.Encoder != null)
        {
            var encoderCodec = track.Encoder.CurrentCodec;
            // Store the ACTUAL codec the encoder is using, not the negotiated one.
            // This prevents EnsureEncoderMatchesResolution from thinking codec changed
            // and triggering unnecessary encoder recreation loops.
            track.LastUsedCodec = encoderCodec;

            // Notify if encoder fell back to a different codec than negotiated
            if (encoderCodec != _negotiatedCodec && track.Index == 0)
            {
                Logger.Warn($"[SIPSorcery] Codec fallback: negotiated={_negotiatedCodec}, actual={encoderCodec}");
                OnCodecFallback?.Invoke(_negotiatedCodec, encoderCodec,
                    $"Hardware does not support {_negotiatedCodec} encoding, fell back to {encoderCodec}");
            }
        }
    }

    /// <summary>
    /// Try to initialize encoder with automatic fallback on failure
    /// </summary>
    private ITextureEncoder? TryInitializeEncoderWithFallback(TrackInfo track, ID3D11Device device, GpuVendorDetector.GpuVendor gpuVendor)
    {
        // First attempt: Use recommended encoder for GPU
        ITextureEncoder? encoder = CreateEncoderForGpu(gpuVendor);
        if (encoder == null)
        {
            Logger.Info($"[SIPSorcery] Track {track.Index}: No suitable encoder found for {gpuVendor}");
            return null;
        }

        // Set codec mode on native encoders (H265 if negotiated)
        bool useHevc = _negotiatedCodec == VideoCodec.H265;
        if (useHevc)
        {
            if (encoder is NvencNativeWrapper nvenc) nvenc.UseHevc = true;
            else if (encoder is AmfNativeWrapper amf) amf.UseHevc = true;
            else if (encoder is QsvNativeWrapper qsv) qsv.UseHevc = true;
            Logger.Info($"[SIPSorcery] Track {track.Index}: Encoder configured for HEVC (H.265)");
        }

        try
        {
            encoder.OnEncodedData += (nal, keyframe, pts) => OnEncodedData(track, nal, keyframe, pts);

            // BUGFIX: Scene-change auto-IDR. The encoder (AmfNativeWrapper) detects when the
            // current encoded frame is dramatically larger than recent frames (typical of
            // tab switches / new windows / large UI updates). When detected, force the next
            // encode as IDR so the client gets a fresh reference without waiting for the
            // natural GOP cycle. Without this, "New Tab" or app switches on the desktop
            // show stale P-frame deltas for up to 1 second before the natural IDR arrives.
            if (encoder is AmfNativeWrapper amf)
            {
                amf.OnSceneChangeDetected += () =>
                {
                    long now = Environment.TickCount64;
                    // Throttle per-track: at most one force-IDR per 500ms (we already
                    // throttle scene-change detection at the encoder; this is a safety net).
                    if (now - track.LastKeyframeRequestTicks >= 500)
                    {
                        track.ForceNextKeyframe = true;
                        track.LastKeyframeRequestTicks = now;
                        Logger.Info($"[SIPSorcery] Track {track.Index} scene-change from encoder → forcing IDR");
                    }
                };
            }

            // FFmpeg adapter: pin the NEGOTIATED codec explicitly. Its parameterless Initialize
            // defaults to H265 (hevc_qsv), which fails to open on some iGPUs (e.g. Intel Arc:
            // "Function not implemented") and produced a recreate -> H265-fail -> H264-recover
            // churn every time the encoder was recreated (bitrate/FPS/resolution change), breaking
            // the client stream even though H264 QSV works. Native wrappers (NVENC/AMF/QSV native)
            // fall through to their BGRA/NV12 path below unchanged.
            if (encoder is LibAvEncoderAdapter libavPrimary)
            {
                if (libavPrimary.Initialize(track.Width, track.Height, _fps,
                        _bitrateController.TargetBitrateKbps, device, _negotiatedCodec))
                {
                    Logger.Info($"[SIPSorcery] Track {track.Index}: LibAv encoder initialized (codec: {libavPrimary.CurrentCodec})");
                    return encoder;
                }
                Logger.Error($"[SIPSorcery] Track {track.Index}: LibAv primary ({_negotiatedCodec}) init failed, trying codec fallbacks...");
                encoder.Dispose();
                // Negotiated codec just failed on FFmpeg — skip it, try VP9/VP8 software fallbacks.
                return TryInitializeLibAvEncoder(track, device, skipNegotiatedCodec: true);
            }

            bool initSuccess;
            // Use BGRA mode if encoder supports it (eliminates GPU color conversion)
            if (encoder.SupportsBgraInput)
            {
                initSuccess = encoder.InitializeBgra(track.Width, track.Height, _fps, _bitrateController.TargetBitrateKbps, device);
                if (initSuccess)
                {
                    string encoderName = encoder.GetType().Name.Replace("NativeWrapper", "").Replace("Adapter", "");
                    Logger.Info($"[SIPSorcery] Track {track.Index}: {encoderName} encoder initialized (BGRA mode - no color conversion)");
                    return encoder;
                }
                // Fallback to NV12 mode if BGRA failed
                Logger.Error($"[SIPSorcery] Track {track.Index}: BGRA mode failed, trying NV12 mode...");
            }

            initSuccess = encoder.Initialize(track.Width, track.Height, _fps, _bitrateController.TargetBitrateKbps, device);
            if (initSuccess)
            {
                string encoderName = encoder.GetType().Name.Replace("NativeWrapper", "").Replace("Adapter", "");
                Logger.Info($"[SIPSorcery] Track {track.Index}: {encoderName} encoder initialized");
                return encoder;
            }

            Logger.Error($"[SIPSorcery] Track {track.Index}: Primary encoder init failed, trying fallback...");
            // Cache QsvNative failure so subsequent tracks/recreations skip it immediately
            if (encoder is QsvNativeWrapper) _qsvNativeInitFailed = true;
            encoder.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Primary encoder error: {ex.Message}");
            if (encoder is QsvNativeWrapper) _qsvNativeInitFailed = true;
            try { encoder.Dispose(); } catch { }
        }

        // Fallback: Try LibAvEncoderAdapter (FFmpeg-based, more compatible)
        // Skip the negotiated codec in LibAv fallback — if native encoder failed for that codec,
        // FFmpeg with the same hardware will also fail (same QSV/NVENC/AMF runtime limitation).
        // This avoids ~500ms wasted per track on redundant init attempts.
        bool skipNegotiatedInFallback = useHevc; // Native HEVC failed → skip LibAv HEVC too
        if (gpuVendor == GpuVendorDetector.GpuVendor.Intel)
        {
            Logger.Info($"[SIPSorcery] Track {track.Index}: Trying LibAv fallback for Intel (skipHEVC={skipNegotiatedInFallback})...");
            return TryInitializeLibAvEncoder(track, device, skipNegotiatedInFallback);
        }

        // For other GPUs, also try LibAv as final fallback
        Logger.Info($"[SIPSorcery] Track {track.Index}: Trying LibAv software fallback (skipHEVC={skipNegotiatedInFallback})...");
        return TryInitializeLibAvEncoder(track, device, skipNegotiatedInFallback);
    }

    /// <summary>
    /// Initialize LibAv encoder as fallback.
    /// Uses negotiated codec first, then fallback to other compatible codecs.
    /// When skipNegotiatedCodec is true, skips the negotiated codec entirely
    /// (used when native encoder already failed for that codec — same hardware limitation applies to FFmpeg).
    /// </summary>
    private ITextureEncoder? TryInitializeLibAvEncoder(TrackInfo track, ID3D11Device device, bool skipNegotiatedCodec = false)
    {
        // Build codec list with negotiated codec first, then fallbacks
        var codecs = new System.Collections.Generic.List<VideoCodec>();

        if (!skipNegotiatedCodec)
        {
            codecs.Add(_negotiatedCodec);
        }
        else
        {
            Logger.Info($"[SIPSorcery] Track {track.Index}: Skipping {_negotiatedCodec} in LibAv fallback (native encoder already failed)");
        }

        // Add fallbacks (only codecs client might support)
        if (_negotiatedCodec != VideoCodec.H264) codecs.Add(VideoCodec.H264);
        if (_negotiatedCodec != VideoCodec.VP9) codecs.Add(VideoCodec.VP9);
        if (_negotiatedCodec != VideoCodec.VP8) codecs.Add(VideoCodec.VP8);
        // Note: Don't add H265 as fallback - most WebRTC clients don't support it

        Logger.Info($"[SIPSorcery] Track {track.Index}: Codec priority: [{string.Join(", ", codecs)}]");

        foreach (var codec in codecs)
        {
            try
            {
                Logger.Info($"[SIPSorcery] Track {track.Index}: Trying LibAv with {codec}...");
                var encoder = new LibAvEncoderAdapter();
                encoder.OnEncodedData += (nal, keyframe, pts) => OnEncodedData(track, nal, keyframe, pts);

                if (encoder.Initialize(track.Width, track.Height, _fps, _bitrateController.TargetBitrateKbps, device, codec))
                {
                    Logger.Info($"[SIPSorcery] Track {track.Index}: LibAv encoder initialized (codec: {encoder.CurrentCodec})");
                    return encoder;
                }

                Logger.Error($"[SIPSorcery] Track {track.Index}: LibAv {codec} init failed, trying next...");
                encoder.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error($"[SIPSorcery] Track {track.Index}: LibAv {codec} error: {ex.Message}");
            }
        }

        Logger.Error($"[SIPSorcery] Track {track.Index}: ALL ENCODERS FAILED - no video output!");
        return null;
    }

    /// <summary>
    /// Create the appropriate hardware encoder based on GPU vendor with fallback chain
    /// </summary>
    private static ITextureEncoder? CreateEncoderForGpu(GpuVendorDetector.GpuVendor gpuVendor)
    {
        switch (gpuVendor)
        {
            case GpuVendorDetector.GpuVendor.AMD:
                // AMD: Use AMF encoder
                if (AmfNativeWrapper.IsAvailable())
                {
                    Logger.Info("[SIPSorcery] Creating AMF encoder for AMD GPU");
                    return new AmfNativeWrapper();
                }
                // AMD fallback to LibAv AMF
                Logger.Info("[SIPSorcery] AMF native not available, trying LibAv encoder...");
                return CreateLibAvFallbackEncoder("amf");

            case GpuVendorDetector.GpuVendor.NVIDIA:
                // NVIDIA: Use NVENC encoder with BGRA mode (no color conversion needed)
                if (NvencNativeWrapper.IsAvailable())
                {
                    Logger.Info("[SIPSorcery] Creating NVENC encoder for NVIDIA GPU (BGRA mode)");
                    return new NvencNativeWrapper();  // Will use InitializeBgra when initializing
                }
                // Fallback to AMF if available (some systems have both)
                if (AmfNativeWrapper.IsAvailable())
                {
                    Logger.Info("[SIPSorcery] NVENC not available, falling back to AMF");
                    return new AmfNativeWrapper();
                }
                // NVIDIA fallback to LibAv NVENC
                Logger.Info("[SIPSorcery] NVENC native not available, trying LibAv encoder...");
                return CreateLibAvFallbackEncoder("nvenc");

            case GpuVendorDetector.GpuVendor.Intel:
                return CreateIntelEncoder();

            default:
                // Try each encoder in order of preference
                Logger.Info("[SIPSorcery] Unknown GPU, trying available encoders...");
                if (NvencNativeWrapper.IsAvailable())
                    return new NvencNativeWrapper();
                if (AmfNativeWrapper.IsAvailable())
                    return new AmfNativeWrapper();
                if (QsvNativeWrapper.IsAvailable())
                    return new QsvNativeWrapper();
                // Final fallback to software
                return CreateLibAvFallbackEncoder("software");
        }
    }

    // Cache: if QsvNativeWrapper init failed once, skip it for subsequent tracks/recreations.
    // Saves ~700ms per track that would otherwise be wasted on doomed init attempts.
    private static volatile bool _qsvNativeInitFailed = false;

    /// <summary>
    /// Create Intel encoder with driver version-aware fallback chain:
    /// 1. QsvNativeWrapper (Media Foundation) - requires driver >= 27.20.100.x
    /// 2. LibAvEncoderAdapter with h264_qsv (FFmpeg QSV) - works with older drivers
    /// 3. LibAvEncoderAdapter software (x264) - final fallback
    /// </summary>
    private static ITextureEncoder? CreateIntelEncoder()
    {
        var driverInfo = GpuVendorDetector.GetIntelDriverInfo();
        Logger.Info($"[SIPSorcery] Intel GPU: {driverInfo.GpuName}");
        Logger.Info($"[SIPSorcery] Intel driver: {driverInfo.DriverVersionString}, reason: {driverInfo.Reason}");

        // Step 1: Try native MF encoder if driver supports it (skip if already failed)
        if (!_qsvNativeInitFailed &&
            (driverInfo.RecommendedEncoder == "qsv_native" || driverInfo.RecommendedEncoder == "qsv_try_native"))
        {
            if (QsvNativeWrapper.IsAvailable())
            {
                Logger.Info("[SIPSorcery] Trying QSV native encoder (Media Foundation)...");
                var encoder = new QsvNativeWrapper();
                return encoder;
            }
            Logger.Info("[SIPSorcery] QSV native not available (DLL missing or QsvIsAvailable=false)");
        }
        else if (_qsvNativeInitFailed)
        {
            Logger.Info("[SIPSorcery] Skipping QSV native (previously failed, using LibAv directly)");
        }

        // Step 2: Try FFmpeg QSV (h264_qsv) - more compatible with older drivers
        Logger.Info("[SIPSorcery] Trying FFmpeg QSV encoder (h264_qsv)...");
        var ffmpegQsvEncoder = CreateLibAvFallbackEncoder("qsv");
        if (ffmpegQsvEncoder != null)
        {
            return ffmpegQsvEncoder;
        }

        // Step 3: Final fallback to software encoder
        Logger.Error("[SIPSorcery] All Intel hardware encoders failed, using software encoder...");
        return CreateLibAvFallbackEncoder("software");
    }

    /// <summary>
    /// Create LibAvEncoderAdapter as fallback encoder
    /// </summary>
    private static ITextureEncoder? CreateLibAvFallbackEncoder(string type)
    {
        try
        {
            Logger.Info($"[SIPSorcery] Creating LibAv fallback encoder (type={type})...");
            var encoder = new LibAvEncoderAdapter();
            // Note: LibAvEncoderAdapter will auto-detect hardware and fall back internally
            // The 'type' hint is for logging purposes
            return encoder;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] LibAv fallback encoder creation failed: {ex.Message}");
            return null;
        }
    }

    private void CloseConnection()
    {
        _running = false;
        _connected = false;
        _phase3Active = false;
        _phase3PendingActivation = false;

        // Reset shared sync clock for next connection
        Interlocked.Exchange(ref _streamStartMs, -1);
        lock (_audioSyncLock)
        {
            _lastAbsoluteAudioRtp = 0;
        }

        // Stop audio pipeline
        try { _audioCapture?.Stop(); } catch { }
        try { _opusEncoder?.Dispose(); } catch { }
        try { _audioCapture?.Dispose(); } catch { }
        _audioCapture = null;
        _opusEncoder = null;

        lock (_lock)
        {
            foreach (var track in _tracks)
            {
                // IMPORTANT: Do NOT dispose encoders here to allow fast re-use!
                // Just clear the track reference associated with the old PeerConnection.
                track.Track = null;
                
                // Clear any pending frames from previous session
                track.PendingFrame = null;

                // Reset session-specific counters to ensure clean bootstrap on reconnect
                Interlocked.Exchange(ref track.SentFrames, 0);
                Interlocked.Exchange(ref track.EncodedFrames, 0);
                track.IdrViaDcCount = 0;
                track.IsDecodable = false;
                track.IsSessionStarted = false;
                Interlocked.Exchange(ref track.DcNotReadyCount, 0);

                // Generate fresh SSRC + SeqNum to force client's jitter buffer to reset.
                // Reusing the same SSRC with a sequence gap causes the client to hold frames
                // waiting for "missing" packets from the previous session.
                track.Ssrc = (uint)Random.Shared.Next(100000000, 2000000000);
                track.SequenceNumber = (ushort)Random.Shared.Next(0, ushort.MaxValue);
            }

            // Do NOT call _tracks.Clear() - we persist TrackInfo for encoder/device reuse (SSRC is refreshed above)
            _dcWasAboveHigh = false;
            foreach (var t in _tracks)
            {
                t.PFramesDroppedDuringCongestion = false;
                t.LastCongestResyncTicks = 0;
                // Reset per-track congestion state
                t.DcSoftCongestion = false;
                t.DcSoftCongestionEntryTicks = 0;
                t.DcSoftCongestionClearTicks = 0;
                t.CongestionBitrateReduced = false;
                t.TrackBitrateKbps = 0;
                t.PendingBitrateKbps = 0;
            }
            _pendingDevices.Clear();
        }

        try { _audioDc?.close(); } catch { }
        _audioDc = null;

        try { _audioPc?.close(); } catch { }
        _audioPc = null;
        lock (_pendingAudioIceCandidates) { _pendingAudioIceCandidates.Clear(); }
        lock (_pendingAudioLocalCandidates) { _pendingAudioLocalCandidates.Clear(); }
        _audioAnswerDelivered = false;

        try { _cursorDc?.close(); } catch { }
        _cursorDc = null;

        try { _inputDc?.close(); } catch { }
        _inputDc = null;

        // Close per-track H265 video DataChannels (legacy mode on shared PC)
        lock (_h265VideoDcs)
        {
            foreach (var dc in _h265VideoDcs.Values)
                try { dc.close(); } catch { }
            _h265VideoDcs.Clear();
        }
        try { _h265VideoDcLegacy?.close(); } catch { }
        _h265VideoDcLegacy = null;

        // Close per-track video PeerConnections and their DCs (per-track mode)
        foreach (var kvp in _videoPcs)
        {
            try { kvp.Value.close(); } catch { }
        }
        _videoPcs.Clear();
        _videoDcs.Clear();
        lock (_perTrackFallbackDcs) { _perTrackFallbackDcs.Clear(); }
        _perTrackFallbackActive = false;

        try { _mainPc?.close(); } catch { }
        _mainPc = null;
        // SIPSorcery's internal UDP ReceiveFromAsync tasks throw SocketException 995
        // when PC closes. This is expected and silently filtered by the global
        // UnobservedTaskException handler in Program.cs.

        Logger.Info("[SIPSorcery] Connection closed");
    }

    public void Stop()
    {
        if (!_running && _mainPc == null) return;
        Logger.Info("[SIPSorcery] Stopping...");
        CloseConnection();
    }

    /// <summary>
    /// Pause streaming - stop encoding but keep connection alive.
    /// Client can resume without reconnecting.
    /// </summary>
    public void Pause()
    {
        if (_isPaused)
        {
            Logger.Info("[SIPSorcery] Already paused");
            return;
        }
        _isPaused = true;
        _audioCapture?.Pause();
        Logger.Info("[SIPSorcery] Streaming paused (connection maintained)");
    }

    /// <summary>
    /// Resume streaming - restart encoding.
    /// Should request keyframe for immediate visual update.
    /// </summary>
    public void Resume()
    {
        if (!_isPaused)
        {
            Logger.Info("[SIPSorcery] Already running (not paused)");
            return;
        }
        _isPaused = false;
        _audioCapture?.Resume();

        // Reset IdrViaDcCount so codec config (VPS/SPS/PPS) is re-sent on next IDR.
        // Client destroys decoders on pause and needs fresh codec config to reconfigure.
        lock (_lock)
        {
            foreach (var track in _tracks)
                track.IdrViaDcCount = 0;
        }

        Logger.Info("[SIPSorcery] Streaming resumed (codec config will be re-sent)");

        // Request keyframe on all tracks for immediate visual update (bypass throttle)
        RequestKeyframe(-1, force: true);
    }

    /// <summary>
    /// Pause a specific monitor's streaming.
    /// Stops encoding for that monitor but keeps connection alive.
    /// </summary>
    /// <param name="monitorIndex">Index of the monitor to pause (0-based)</param>
    public void PauseMonitor(int monitorIndex)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitorPaused.Length)
        {
            Logger.Info($"[SIPSorcery] PauseMonitor: Invalid index {monitorIndex}");
            return;
        }
        if (_monitorPaused[monitorIndex])
        {
            Logger.Info($"[SIPSorcery] Monitor {monitorIndex} already paused");
            return;
        }
        _monitorPaused[monitorIndex] = true;
        Logger.Info($"[SIPSorcery] Monitor {monitorIndex} paused");
    }

    /// <summary>
    /// Resume a specific monitor's streaming.
    /// Restarts encoding for that monitor and requests keyframe.
    /// </summary>
    /// <param name="monitorIndex">Index of the monitor to resume (0-based)</param>
    public void ResumeMonitor(int monitorIndex)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitorPaused.Length)
        {
            Logger.Info($"[SIPSorcery] ResumeMonitor: Invalid index {monitorIndex}");
            return;
        }
        if (!_monitorPaused[monitorIndex])
        {
            Logger.Info($"[SIPSorcery] Monitor {monitorIndex} already running");
            return;
        }
        _monitorPaused[monitorIndex] = false;
        Logger.Info($"[SIPSorcery] Monitor {monitorIndex} resumed");

        // Request keyframe for immediate visual update (bypass throttle)
        RequestKeyframe(monitorIndex, force: true);
    }

    private async Task LogStatsAsync()
    {
        while (_running && !_disposed)
        {
            await Task.Delay(10000);
            if (!_running) break;

            lock (_lock)
            {
                var stats = string.Join(", ", _tracks.Select(t =>
                {
                    long encCount = Interlocked.Read(ref t.EncodeLatencyCount);
                    long avgUs = encCount > 0 ? Interlocked.Read(ref t.EncodeLatencySum) / encCount : 0;
                    long encoded = Interlocked.Read(ref t.EncodedFrames);
                    long sent = Interlocked.Read(ref t.SentFrames);
                    long skipped = encoded - sent;
                    string skipInfo = _negotiatedCodec == VideoCodec.H265 && skipped > 0 ? $",skip={skipped}" : "";
                    return $"m{t.Index}:{sent}f(enc={encoded}{skipInfo}),lat={avgUs}us";
                }));
                var audioPkts = Interlocked.Read(ref _audioPacketsSent);
                long lastInterval = _audioPacketsLastInterval;
                long intervalPkts = audioPkts - lastInterval;
                _audioPacketsLastInterval = audioPkts;
                float audioRate = intervalPkts / 10.0f; // packets per second over 10s interval
                var audioInfo = _hasAudioTrack ? $", audio:{audioPkts}pkts ({audioRate:F1}/sec)" : "";
                // Audio capture latency diagnostics (callback interval = effective WASAPI buffer latency)
                if (_audioCapture != null)
                {
                    var (avgMs, avgBytes, cbCount) = _audioCapture.ReadAndResetStats();
                    if (cbCount > 0)
                    {
                        double dataDurationMs = (double)avgBytes / (_audioCapture.SampleRate * _audioCapture.Channels * 4) * 1000.0; // float32 = 4 bytes
                        audioInfo += $", capture:{avgMs:F1}ms/cb({dataDurationMs:F1}ms data,{cbCount}cb)";
                    }
                }
                // Log DC buffer depth for H265 latency diagnosis
                var dcInfo = "";
                if (_negotiatedCodec == VideoCodec.H265)
                {
                    var dcBuffers = _tracks.Select(t =>
                    {
                        var dc = GetH265VideoChannel(t.Index);
                        return dc != null ? $"dc{t.Index}={dc.bufferedAmount / 1024}KB" : null;
                    }).Where(s => s != null);
                    dcInfo = $", {string.Join(", ", dcBuffers)}";
                }
                Logger.Info($"[SIPSorcery] Stats: {stats}{audioInfo}{dcInfo}");
            }

            EmitPathSnapshots();
        }
    }

    /// <summary>
    /// Emit one telemetry "snapshot" event per active PC (main + every video PC) with the
    /// REAL selected candidate pair read via HostSelectedPathReader — the media-path source
    /// of truth per telemetry-snapshot-contract-v1. Fire-and-forget; a snapshot is simply
    /// skipped (not sent as garbage) when the selected pair isn't known yet. QoE fields the
    /// Host doesn't measure (rtt/jitter/loss/qp/drops/ttff/freeze) are left null by design.
    /// </summary>
    private void EmitPathSnapshots()
    {
        try
        {
            EmitOnePathSnapshot("main", monitorIndex: 0, _mainPc);

            foreach (var kvp in _videoPcs)
            {
                EmitOnePathSnapshot("video", monitorIndex: kvp.Key, kvp.Value);
            }
        }
        catch (Exception ex)
        {
            // Telemetry must never affect streaming — log and move on.
            Logger.Debug($"[SIPSorcery] EmitPathSnapshots failed (non-fatal): {ex.Message}");
        }
    }

    private void EmitOnePathSnapshot(string pcRole, int monitorIndex, SIPSorcery.Net.RTCPeerConnection? pc)
    {
        var pair = HostSelectedPathReader.TryRead(pc);
        if (pair == null) return; // Not nominated yet this interval — nothing fabricated, just skipped.

        string pcRoleKey = pcRole == "main" ? "main" : $"video-{monitorIndex}";
        string codecStr = _negotiatedCodec switch
        {
            VideoCodec.H264 => "h264",
            VideoCodec.H265 => "h265",
            _ => "unknown"
        };

        int? width = null, height = null;
        lock (_lock)
        {
            if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                width = _tracks[monitorIndex].Width > 0 ? _tracks[monitorIndex].Width : null;
                height = _tracks[monitorIndex].Height > 0 ? _tracks[monitorIndex].Height : null;
            }
        }

        var snapshot = new ConnectionTelemetrySnapshot
        {
            Event = "snapshot",
            SessionId = SessionId,
            PcRole = pcRole,
            MonitorIndex = monitorIndex,
            Generation = CurrentIceGeneration(pcRoleKey),
            Sequence = HostTelemetryReporter.NextSequence(SessionId, pcRole, monitorIndex),
            LocalCandidateType = pair.Value.LocalCandidateType,
            RemoteCandidateType = pair.Value.RemoteCandidateType,
            AddressFamily = pair.Value.AddressFamily,
            Protocol = pair.Value.Protocol,
            RelayProtocol = pair.Value.RelayProtocol,
            PathClass = pair.Value.PathClass,
            SendBitrateKbps = _bitrateController.TargetBitrateKbps > 0 ? _bitrateController.TargetBitrateKbps : null,
            Codec = codecStr,
            Width = width,
            Height = height,
            Fps = _fps > 0 ? _fps : null
        };

        HostTelemetryReporter.ReportFireAndForget(snapshot);
    }

    /// <summary>
    /// Whether the cursor DataChannel is open and ready for sending.
    /// </summary>
    public bool HasCursorChannel => _cursorDc?.readyState == SIPSorcery.Net.RTCDataChannelState.open;

    /// <summary>
    /// Send cursor position via DataChannel (low-latency binary format).
    /// Binary format (little-endian): [type(1)][monitorIndex(1)][u(4)][v(4)][flags(1)][cursorId(8)] = 19 bytes
    /// Allocates fresh buffer each call to avoid race with SCTP send queue.
    /// </summary>
    /// <summary>
    /// Detect actual ICE connection type from nominated candidate pair.
    /// Returns "P2P Direct" (host/srflx) or "TURN Relay" (relay).
    /// </summary>
    /// <summary>
    /// Detect ICE connection type by checking the connected remote endpoint.
    /// If connected to TURN server IP → relay. Otherwise → P2P direct.
    /// </summary>
    /// <summary>
    /// TURN server IPs to check against. Set externally before detection.
    /// </summary>
    public static System.Collections.Generic.HashSet<string> TurnServerIps { get; } = new();

    public string DetectIceConnectionType()
    {
        try
        {
            foreach (var vpc in _videoPcs.Values)
            {
                var ep = vpc.AudioDestinationEndPoint;
                if (ep != null)
                {
                    var ip = ep.Address.ToString();
                    var isTurn = TurnServerIps.Contains(ip);
                    var result = isTurn ? "TURN Relay" : "P2P Direct";
                    Logger.Info($"[SIPSorcery] ICE type: {result} (connected to {ip})");
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Failed to detect ICE type: {ex.Message}");
        }

        return "P2P Direct";
    }

    /// <summary>
    /// Echo ping data back via cursor DataChannel for P2P RTT measurement.
    /// </summary>
    /// <summary>
    /// Send a pre-encoded frame (from host's encoder) via this viewer's DataChannel.
    /// Used in multi-client mode: host encodes once, viewers receive and forward via their own DCs.
    /// </summary>
    public void SendPreEncodedFrame(int trackIndex, byte[] nalBytes, bool isKeyframe, byte[]? paramSets)
    {
        if (!_running || !_connected) return;

        try
        {
            var dc = GetH265VideoChannel(trackIndex);
            if (dc == null || dc.readyState != SIPSorcery.Net.RTCDataChannelState.open) return;

            // For keyframes: send param sets first (VPS/SPS/PPS) as type 0x02
            if (isKeyframe && paramSets != null && paramSets.Length > 0)
            {
                var psMsg = new byte[2 + paramSets.Length];
                psMsg[0] = 0x02; // type: codec config
                psMsg[1] = (byte)trackIndex;
                Buffer.BlockCopy(paramSets, 0, psMsg, 2, paramSets.Length);
                dc.send(psMsg);
            }

            // Send frame data via DC (same chunking as host)
            byte type = isKeyframe ? (byte)0x03 : (byte)0x04;
            int maxChunkSize = 1200;

            if (nalBytes.Length <= maxChunkSize)
            {
                var msg = new byte[6 + nalBytes.Length];
                msg[0] = type;
                msg[1] = (byte)trackIndex;
                msg[2] = 0; // chunkIndex
                msg[3] = 1; // totalChunks
                msg[4] = (byte)(nalBytes.Length & 0xFF);
                msg[5] = (byte)((nalBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(nalBytes, 0, msg, 6, nalBytes.Length);
                dc.send(msg);
            }
            else
            {
                int totalChunks = (nalBytes.Length + maxChunkSize - 1) / maxChunkSize;
                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * maxChunkSize;
                    int len = Math.Min(maxChunkSize, nalBytes.Length - offset);
                    var msg = new byte[6 + len];
                    msg[0] = type;
                    msg[1] = (byte)trackIndex;
                    msg[2] = (byte)i;
                    msg[3] = (byte)totalChunks;
                    msg[4] = (byte)(nalBytes.Length & 0xFF);
                    msg[5] = (byte)((nalBytes.Length >> 8) & 0xFF);
                    Buffer.BlockCopy(nalBytes, offset, msg, 6, len);
                    dc.send(msg);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] SendPreEncodedFrame track {trackIndex}: {ex.Message}");
        }
    }

    public void SendPingEcho(byte[] pingData)
    {
        var dc = _cursorDc;
        if (dc?.readyState != SIPSorcery.Net.RTCDataChannelState.open) return;
        dc.send(pingData); // echo same bytes back (tag 0x09 + timestamp)
    }

    public void SendCursorPosition(int monitorIndex, float u, float v, bool visible, int cursorType, long cursorId)
    {
        var dc = _cursorDc;
        if (dc?.readyState != SIPSorcery.Net.RTCDataChannelState.open) return;

        var buf = new byte[19];
        buf[0] = 1; // message type: cursor_position
        buf[1] = (byte)monitorIndex;
        BitConverter.TryWriteBytes(buf.AsSpan(2, 4), u);
        BitConverter.TryWriteBytes(buf.AsSpan(6, 4), v);
        buf[10] = (byte)((visible ? 1 : 0) | ((cursorType & 0x0F) << 1));
        BitConverter.TryWriteBytes(buf.AsSpan(11, 8), cursorId);

        dc.send(buf);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        // Final cleanup of persistent track resources
        lock (_lock)
        {
            foreach (var track in _tracks)
            {
                track.Dispose();
            }
            _tracks.Clear();
        }

        Logger.Info("[SIPSorcery] Disposed");
    }
}
