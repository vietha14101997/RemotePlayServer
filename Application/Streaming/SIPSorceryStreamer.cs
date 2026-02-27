#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D11;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

/// <summary>
/// SIPSorcery-based WebRTC streamer for multi-monitor desktop streaming.
/// Uses SIPSorcery's VideoStreamList for multi-track support (v6.0.8+).
/// </summary>
public class SIPSorceryStreamer : IDisposable
{
    private readonly int _monitorCount;
    private int _fps;
    private int _bitrateKbps;
    private readonly VideoCodec _negotiatedCodec;
    private ID3D11Device? _sharedDevice;

    private RTCPeerConnection? _pc;
    private readonly List<TrackInfo> _tracks = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, ID3D11Device> _pendingDevices = new();

    // Permanent device mappings - persists across reconnections
    private readonly Dictionary<int, ID3D11Device> _deviceMappings = new();

    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _connected;
    private volatile bool _isPaused;

    // Per-monitor pause state (allows pausing individual monitors)
    private volatile bool[] _monitorPaused = Array.Empty<bool>();

    /// <summary>
    /// Indicates if streaming is paused (capture/encode stopped but connection maintained).
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Check if a specific monitor is paused.
    /// </summary>
    public bool IsMonitorPaused(int monitorIndex)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitorPaused.Length) return false;
        return _monitorPaused[monitorIndex];
    }

    // Adaptive bitrate controller
    private readonly AdaptiveBitrateController _bitrateController = new();

    /// <summary>
    /// Per-track state including encoder and staging texture
    /// </summary>
    private class TrackInfo : IDisposable
    {
        public int Index { get; set; }
        public MediaStreamTrack? Track { get; set; }
        public string Mid { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }

        // Encoder per track (supports AMF, NVENC, QSV)
        public ITextureEncoder? Encoder { get; set; }
        public ID3D11Texture2D? StagingNV12 { get; set; }
        public ID3D11Device? Device { get; set; }

        // Stats
        public long EncodedFrames;
        public long SentFrames;
        public long LastPts100ns = -1;
        public uint RtpTimestamp;
        public bool TimestampInitialized;
        public volatile bool ForceNextKeyframe;

        public void Dispose()
        {
            try { Encoder?.Dispose(); } catch { }
            try { StagingNV12?.Dispose(); } catch { }
            Encoder = null;
            StagingNV12 = null;
        }
    }

    // Events for connection state notifications
    public event Action? OnAllTracksReady;
    public event Action<string>? OnIceCandidate;
    public event Action? OnConnectionFailed;

    public bool IsConnected => _connected;
    public int MonitorCount => _monitorCount;

    public SIPSorceryStreamer(int monitorCount, int fps, int kbps, ID3D11Device? device = null, VideoCodec codec = VideoCodec.H264)
    {
        _monitorCount = monitorCount;
        _fps = fps;
        _bitrateKbps = kbps;
        _negotiatedCodec = codec;
        _sharedDevice = device;

        Logger.Info($"[SIPSorcery] Created: {monitorCount} monitors, {fps}fps, {kbps}kbps, codec={codec}");
    }

    public void SetDevice(ID3D11Device device)
    {
        _sharedDevice = device;
    }

    public void SetDeviceForMonitor(int monitorIndex, ID3D11Device device)
    {
        lock (_lock)
        {
            // Always store in permanent mappings (survives reconnection)
            _deviceMappings[monitorIndex] = device;

            if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                _tracks[monitorIndex].Device = device;
                Logger.Info($"[SIPSorcery] Set device for existing track {monitorIndex}");
            }
            else
            {
                _pendingDevices[monitorIndex] = device;
                Logger.Info($"[SIPSorcery] Stored pending device for monitor {monitorIndex}");
            }
        }
    }

    /// <summary>
    /// Process single SDP offer (with N m= sections), create N tracks, return single answer.
    /// </summary>
    public async Task<string> ProcessOfferAsync(string offerSdp, List<(int w, int h)> dimensions)
    {
        if (_running)
        {
            Logger.Info("[SIPSorcery] Closing existing connection for reconnect...");
            CloseConnection();
        }

        Logger.Info($"[SIPSorcery] Processing offer for {dimensions.Count} monitors");

        // Parse H264 payload type from offer
        var (h264Pt, h264Fmtp) = TryGetH264FromOfferSdp(offerSdp);
        Logger.Info($"[SIPSorcery] Offer H264 pt={h264Pt ?? 96}, fmtp={h264Fmtp ?? "default"}");

        // Log client fingerprint from offer for DTLS debugging
        foreach (var line in offerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] Client SDP: {line}");
        }

        // Create PeerConnection with STUN for better ICE reliability
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };
        _pc = new RTCPeerConnection(cfg);
        Logger.Info("[SIPSorcery] PeerConnection created (with STUN)");

        // Create N video tracks - one per monitor
        for (int i = 0; i < dimensions.Count; i++)
        {
            var (w, h) = dimensions[i];

            // H264 format with constrained baseline profile
            var h264 = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                id: h264Pt ?? (96 + i),
                name: "H264",
                clockRate: 90000,
                channels: 0,
                fmtp: string.IsNullOrWhiteSpace(h264Fmtp)
                    ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f"
                    : h264Fmtp);

            var track = new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                isRemote: false,
                capabilities: new List<SDPAudioVideoMediaFormat> { h264 },
                streamStatus: MediaStreamStatusEnum.SendOnly);

            _pc.addTrack(track);

            // Get device for track - priority: permanent mappings > pending > shared
            ID3D11Device? deviceForTrack = _sharedDevice;
            if (_deviceMappings.TryGetValue(i, out var mappedDevice))
            {
                // Use permanent mapping (survives reconnection)
                deviceForTrack = mappedDevice;
            }
            else if (_pendingDevices.TryGetValue(i, out var pendingDevice))
            {
                deviceForTrack = pendingDevice;
                _pendingDevices.Remove(i);
            }

            _tracks.Add(new TrackInfo
            {
                Index = i,
                Track = track,
                Mid = i.ToString(),
                Width = w,
                Height = h,
                Device = deviceForTrack
            });

            Logger.Info($"[SIPSorcery] Added track {i}: {w}x{h} (device={deviceForTrack?.GetHashCode():X8})");
        }

        // Initialize per-monitor pause state (all monitors active initially)
        _monitorPaused = new bool[dimensions.Count];

        // ICE candidate forwarding
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Logger.Info($"[SIPSorcery] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        // Connection state changes
        // NOTE: Do NOT initialize encoders here - it blocks DTLS handshake!
        // Encoder init moved to onconnectionstatechange (after DTLS complete)
        _pc.oniceconnectionstatechange += (state) =>
        {
            Logger.Info($"[SIPSorcery] ICE state: {state}");
            if (state == RTCIceConnectionState.connected)
            {
                Logger.Info("[SIPSorcery] ICE CONNECTED - waiting for DTLS...");
                // Don't initialize encoders here - let DTLS complete first
            }
            else if (state == RTCIceConnectionState.failed)
            {
                _connected = false;
                OnConnectionFailed?.Invoke();
            }
            else if (state == RTCIceConnectionState.disconnected || state == RTCIceConnectionState.closed)
            {
                _connected = false;
            }
        };

        // DTLS/SRTP connection state - initialize encoders AFTER DTLS completes
        _pc.onconnectionstatechange += (state) =>
        {
            Logger.Info($"[SIPSorcery] Peer state: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Logger.Info("[SIPSorcery] DTLS CONNECTED - initializing encoders now");
                _connected = true;
                InitializeEncoders();
                OnAllTracksReady?.Invoke();
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                Logger.Error("[SIPSorcery] DTLS FAILED - check certificate/fingerprint");
                _connected = false;
                OnConnectionFailed?.Invoke();
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                // Connection was closed unexpectedly (DTLS timeout, network issue, etc.)
                // Client should send a reconnect offer to recover
                Logger.Info("[SIPSorcery] Peer state CLOSED unexpectedly - awaiting client reconnect offer");
                _connected = false;
            }
            else if (state == RTCPeerConnectionState.disconnected)
            {
                // Temporary disconnection - may recover automatically
                // Don't fire OnConnectionFailed yet, give ICE time to recover
                Logger.Info("[SIPSorcery] Peer state DISCONNECTED - may recover, waiting...");
                _connected = false;
            }
        };

        _pc.onsignalingstatechange += () =>
        {
            Logger.Info($"[SIPSorcery] Signaling state: {_pc.signalingState}");
        };

        // Set remote offer and create answer
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        _running = true;

        // Start stats logging
        _ = Task.Run(LogStatsAsync);

        // Fix SDP for browser compatibility (SAVPF)
        var answerSdp = answer.sdp ?? "";
        if (!answerSdp.Contains("SAVPF"))
            answerSdp = answerSdp.Replace("SAVP", "SAVPF");
        answerSdp = FilterAnswerSdpIceCandidates(answerSdp);

        // DTLS setup role: Keep SIPSorcery's default "setup:active"
        // SIPSorcery internally acts as DTLS client when IceRole is active.
        // Changing SDP text without changing internal behavior causes mismatch.
        // Let SIPSorcery initiate DTLS handshake as active party.
        // NOTE: RFC 5763 recommends answerer use "active" for parallel handshake.
        if (answerSdp.Contains("a=setup:active"))
        {
            Logger.Info("[SIPSorcery] DTLS setup: keeping active (SIPSorcery will initiate handshake)");
        }

        // Log DTLS-critical SDP attributes for debugging
        foreach (var line in answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] SDP: {line}");
        }

        Logger.Info($"[SIPSorcery] Answer ready, {answerSdp.Length} bytes");
        return answerSdp;
    }

    public void AddIceCandidate(string candidate, string? mid = null)
    {
        if (_pc == null || string.IsNullOrEmpty(candidate)) return;

        try
        {
            var candStr = candidate.Trim();
            if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring(2);
            if (candStr.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring("candidate:".Length);
            if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = "candidate:" + candStr;

            var init = new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = mid ?? "0" };
            _pc.addIceCandidate(init);
            Logger.Info($"[SIPSorcery] Added remote ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] AddIceCandidate error: {ex.Message}");
        }
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
                if (track.Encoder != null) continue;

                var device = track.Device ?? _sharedDevice;
                if (device == null)
                {
                    Logger.Info($"[SIPSorcery] Track {track.Index}: No D3D11 device");
                    continue;
                }

                // Try to initialize encoder with fallback chain
                track.Encoder = TryInitializeEncoderWithFallback(track, device, gpuVendor);
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

        try
        {
            encoder.OnEncodedData += (nal, keyframe, pts) => OnEncodedData(track, nal, keyframe, pts);

            bool initSuccess;
            // Use BGRA mode if encoder supports it (eliminates GPU color conversion)
            if (encoder.SupportsBgraInput)
            {
                initSuccess = encoder.InitializeBgra(track.Width, track.Height, _fps, _bitrateKbps, device);
                if (initSuccess)
                {
                    string encoderName = encoder.GetType().Name.Replace("NativeWrapper", "").Replace("Adapter", "");
                    Logger.Info($"[SIPSorcery] Track {track.Index}: {encoderName} encoder initialized (BGRA mode - no color conversion)");
                    return encoder;
                }
                // Fallback to NV12 mode if BGRA failed
                Logger.Error($"[SIPSorcery] Track {track.Index}: BGRA mode failed, trying NV12 mode...");
            }

            initSuccess = encoder.Initialize(track.Width, track.Height, _fps, _bitrateKbps, device);
            if (initSuccess)
            {
                string encoderName = encoder.GetType().Name.Replace("NativeWrapper", "").Replace("Adapter", "");
                Logger.Info($"[SIPSorcery] Track {track.Index}: {encoderName} encoder initialized");
                return encoder;
            }

            Logger.Error($"[SIPSorcery] Track {track.Index}: Primary encoder init failed, trying fallback...");
            encoder.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Primary encoder error: {ex.Message}");
            try { encoder.Dispose(); } catch { }
        }

        // Fallback: Try LibAvEncoderAdapter (FFmpeg-based, more compatible)
        if (gpuVendor == GpuVendorDetector.GpuVendor.Intel)
        {
            Logger.Info($"[SIPSorcery] Track {track.Index}: Trying LibAv fallback for Intel...");
            return TryInitializeLibAvEncoder(track, device);
        }

        // For other GPUs, also try LibAv as final fallback
        Logger.Info($"[SIPSorcery] Track {track.Index}: Trying LibAv software fallback...");
        return TryInitializeLibAvEncoder(track, device);
    }

    /// <summary>
    /// Initialize LibAv encoder as fallback
    /// Uses negotiated codec first, then fallback to other compatible codecs
    /// </summary>
    private ITextureEncoder? TryInitializeLibAvEncoder(TrackInfo track, ID3D11Device device)
    {
        // Build codec list with negotiated codec first, then fallbacks
        var codecs = new List<VideoCodec> { _negotiatedCodec };

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

                if (encoder.Initialize(track.Width, track.Height, _fps, _bitrateKbps, device, codec))
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

        // Step 1: Try native MF encoder if driver supports it
        if (driverInfo.RecommendedEncoder == "qsv_native" || driverInfo.RecommendedEncoder == "qsv_try_native")
        {
            if (QsvNativeWrapper.IsAvailable())
            {
                Logger.Info("[SIPSorcery] Trying QSV native encoder (Media Foundation)...");
                var encoder = new QsvNativeWrapper();
                // Note: Actual initialization happens later in InitializeEncoders()
                // If it fails there, we should have a retry mechanism
                return encoder;
            }
            Logger.Info("[SIPSorcery] QSV native not available (DLL missing or QsvIsAvailable=false)");
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

    /// <summary>
    /// Check if a track requires BGRA input (no color conversion needed)
    /// </summary>
    public bool RequiresBgraInput(int monitorIndex)
    {
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return false;
        var track = _tracks[monitorIndex];
        return track.Encoder?.UsingBgraMode ?? false;
    }

    /// <summary>
    /// Check if any track requires BGRA input
    /// </summary>
    public bool AnyTrackRequiresBgraInput()
    {
        lock (_lock)
        {
            return _tracks.Any(t => t.Encoder?.UsingBgraMode ?? false);
        }
    }

    /// <summary>
    /// Push BGRA texture directly (for NVENC BGRA mode - no color conversion)
    /// </summary>
    public void PushBgraTexture(int monitorIndex, ID3D11Texture2D bgraTexture, int width, int height)
    {
        if (!_running || _disposed || !_connected || _isPaused) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        // Check per-monitor pause
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null) return;

        lock (_lock)
        {
            try
            {
                // Force keyframe for first 5 frames
                // Also force if explicitly requested
                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe;
                track.ForceNextKeyframe = false;

                // Encode BGRA directly - no staging texture or copy needed
                track.Encoder.EncodeBgraTexture(bgraTexture, forceKeyframe: forceIdr);
                Interlocked.Increment(ref track.EncodedFrames);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.EncodedFrames) % 60 == 0)
                    Logger.Error($"[SIPSorcery] Track {monitorIndex} BGRA encode error: {ex.Message}");
            }
        }
    }

    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed || !_connected || _isPaused) return;
        // Check per-monitor pause
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null) return;

        lock (_lock)
        {
            try
            {
                var device = track.Device ?? _sharedDevice;
                if (device == null) return;

                if (track.StagingNV12 == null)
                {
                    var desc = new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.NV12,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    track.StagingNV12 = device.CreateTexture2D(desc);
                }

                device.ImmediateContext.CopyResource(track.StagingNV12, nv12Texture);

                // Force keyframe for first 5 frames (AMF has output delay/buffering)
                // Also force if explicitly requested
                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe;
                track.ForceNextKeyframe = false;

                track.Encoder.EncodeTexture(track.StagingNV12, forceKeyframe: forceIdr);
                Interlocked.Increment(ref track.EncodedFrames);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.EncodedFrames) % 60 == 0)
                    Logger.Error($"[SIPSorcery] Track {monitorIndex} encode error: {ex.Message}");
            }
        }
    }

    private void OnEncodedData(TrackInfo track, byte[] nalData, bool isKeyframe, long pts100ns)
    {
        if (!_running || _pc == null || !_connected) return;
        if (_pc.connectionState != RTCPeerConnectionState.connected) return;

        try
        {
            // Convert to Annex B if needed and strip AUD
            byte[] au = nalData;
            if (!ContainsAnnexBStartCode(au))
                au = TryConvertAvccToAnnexB(au);
            au = StripLeadingAud(au);

            uint rtpStep = CalculateRtpStep(track, pts100ns);

            // DEBUG: Log NAL types for first few frames only (avoid spam)
            long frameNum = Interlocked.Read(ref track.SentFrames);
            if (frameNum < 5)
            {
                var nalTypes = GetNalTypes(au);
                Logger.Debug($"[SIPSorcery] Track {track.Index} frame #{frameNum}: {au.Length}B, NAL types=[{string.Join(",", nalTypes)}], key={isKeyframe}");
            }

            // DEBUG: Log VideoStreamList info on first frame of each track
            if (frameNum == 0)
            {
                var streamCount = _pc.VideoStreamList?.Count ?? 0;
                Logger.Info($"[SIPSorcery] Track {track.Index} first frame: VideoStreamList.Count={streamCount}, rtpStep={rtpStep}");

                // Log SSRC info for each video stream
                if (_pc.VideoStreamList != null)
                {
                    for (int i = 0; i < Math.Min(3, _pc.VideoStreamList.Count); i++)
                    {
                        var vs = _pc.VideoStreamList[i];
                        Logger.Info($"[SIPSorcery] VideoStream[{i}]: SSRC={vs.LocalTrack?.Ssrc ?? 0}");
                    }
                }
            }

            // WORKAROUND: Use default SendVideo for first track (track 0)
            // VideoStreamList indexing may not match Unity WebRTC transceiver order
            if (track.Index == 0)
            {
                // Always use _pc.SendVideo() for track 0 - goes to first video stream
                _pc.SendVideo(rtpStep, au);
                Interlocked.Increment(ref track.SentFrames);
            }
            else if (_pc.VideoStreamList != null && track.Index < _pc.VideoStreamList.Count)
            {
                // Send to specific video stream by index for tracks 1, 2, ...
                var videoStream = _pc.VideoStreamList[track.Index];
                videoStream.SendVideo(rtpStep, au);
                Interlocked.Increment(ref track.SentFrames);
            }
            else
            {
                // Fallback: skip tracks we can't send to
                if (frameNum < 5)
                    Logger.Info($"[SIPSorcery] Track {track.Index} SKIP: VideoStreamList null or index out of range");
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                Logger.Error($"[SIPSorcery] Track {track.Index} send error: {ex.Message}");
        }
    }

    private uint CalculateRtpStep(TrackInfo track, long pts100ns)
    {
        const int ClockRate = 90000;
        uint fallback = (uint)Math.Max(1, ClockRate / Math.Max(1, _fps));

        lock (track)
        {
            if (!track.TimestampInitialized)
            {
                track.LastPts100ns = pts100ns;
                track.RtpTimestamp = (uint)(Environment.TickCount & 0xFFFF);
                track.TimestampInitialized = true;
                return track.RtpTimestamp;
            }

            long delta = pts100ns - track.LastPts100ns;
            if (delta <= 0 || delta > 5_000_000)
            {
                track.LastPts100ns = pts100ns;
                return fallback;
            }

            track.LastPts100ns = pts100ns;
            uint step = (uint)Math.Max(1, ClockRate * delta / 10_000_000L);
            return step;
        }
    }

    private List<int> GetNalTypes(byte[] au)
    {
        var types = new List<int>();
        int i = 0;
        while (i + 4 <= au.Length)
        {
            int sc = (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ? 3 :
                     (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) ? 4 : 0;
            if (sc == 0) break;
            i += sc;
            if (i >= au.Length) break;
            types.Add(au[i] & 0x1F);
            // Find next start code
            int j = i + 1;
            for (; j + 3 < au.Length; j++)
            {
                if ((au[j] == 0 && au[j + 1] == 0 && au[j + 2] == 1) ||
                    (j + 4 <= au.Length && au[j] == 0 && au[j + 1] == 0 && au[j + 2] == 0 && au[j + 3] == 1))
                    break;
            }
            i = j;
        }
        return types;
    }

    public void RequestKeyframe(int monitorIndex = -1)
    {
        lock (_lock)
        {
            if (monitorIndex == -1)
            {
                foreach (var t in _tracks) t.ForceNextKeyframe = true;
            }
            else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                _tracks[monitorIndex].ForceNextKeyframe = true;
            }
        }
    }

    public void ProcessFpsFeedback(int monitorIndex, float effectiveFps, int droppedFrames, long clientTotalFrames)
    {
        // Get server's sent frame count for this monitor
        long serverSentFrames = 0;
        lock (_lock)
        {
            if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                serverSentFrames = Interlocked.Read(ref _tracks[monitorIndex].SentFrames);
            }
        }

        // Calculate loss percentage
        float lossPercent = serverSentFrames > 0
            ? (1f - (float)clientTotalFrames / serverSentFrames) * 100f
            : 0f;

        Logger.Info($"[Pipeline] Mon{monitorIndex}: Server sent {serverSentFrames}, Client received {clientTotalFrames} (loss={lossPercent:F1}%)");
        Logger.Info($"[SIPSorcery] FPS feedback m{monitorIndex}: {effectiveFps:F1}fps, dropped={droppedFrames}");
    }

    public int GetCurrentTargetFps(int monitorIndex) => _fps;

    /// <summary>
    /// Process quality feedback from client and adjust bitrate if needed.
    /// </summary>
    /// <param name="feedback">Quality feedback from client.</param>
    /// <returns>BitrateAdjustedMessage if bitrate was changed, null otherwise.</returns>
    public BitrateAdjustedMessage? ProcessQualityFeedback(QualityFeedbackMessage feedback)
    {
        // Initialize controller on first feedback if not already done
        if (_bitrateController.TargetBitrateKbps == 0)
        {
            _bitrateController.Initialize(_bitrateKbps, _bitrateKbps * 2);
        }

        // Process feedback through adaptive bitrate controller
        var decision = _bitrateController.ProcessFeedback(feedback);

        if (decision.Changed)
        {
            // Apply new bitrate to all track encoders
            lock (_lock)
            {
                int successCount = 0;
                foreach (var track in _tracks)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetBitrate(decision.NewBitrate))
                        {
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} bitrate → {decision.NewBitrate}kbps");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetBitrate failed");
                        }
                    }
                }

                if (successCount > 0)
                {
                    Logger.Info($"[SIPSorcery] Bitrate adjusted: {decision.NewBitrate}kbps ({decision.Reason})");
                }
            }

            // Return message to notify client
            return new BitrateAdjustedMessage
            {
                MonitorIndex = -1, // All monitors
                BitrateKbps = decision.NewBitrate,
                Reason = decision.Reason
            };
        }

        return null;
    }

    /// <summary>
    /// Get current adaptive bitrate statistics.
    /// </summary>
    public string GetBitrateStats() => _bitrateController.GetStats();

    /// <summary>
    /// Reset adaptive bitrate controller to initial state.
    /// </summary>
    public void ResetBitrateController() => _bitrateController.Reset();

    /// <summary>
    /// Dynamically update streaming configuration during Phase 3.
    /// </summary>
    /// <param name="fps">New target FPS (optional, null = no change). Note: FPS change may not take effect until reconnect.</param>
    /// <param name="totalBitrateKbps">New TOTAL bitrate in kbps for ALL monitors (optional, null = no change).</param>
    /// <returns>Tuple of (success, appliedFps, appliedTotalBitrate, message)</returns>
    public (bool Success, int Fps, int BitrateKbps, string Message) UpdateConfig(int? fps, int? totalBitrateKbps)
    {
        var messages = new List<string>();
        int appliedFps = _fps;
        int appliedBitrate = _bitrateKbps;
        bool anySuccess = false;

        // FPS change - apply to all encoders
        if (fps.HasValue && fps.Value != _fps && fps.Value > 0)
        {
            Logger.Info($"[SIPSorcery] FPS update: {_fps} → {fps.Value}");

            lock (_lock)
            {
                int successCount = 0;
                foreach (var track in _tracks)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetFps(fps.Value))
                        {
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} FPS → {fps.Value}");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetFps failed (encoder may not support runtime change)");
                        }
                    }
                }

                if (successCount > 0)
                {
                    _fps = fps.Value;
                    appliedFps = fps.Value;
                    messages.Add($"FPS: {fps.Value} ({successCount}/{_tracks.Count} encoders updated)");
                    anySuccess = true;
                }
                else if (_tracks.Count > 0)
                {
                    // Even if encoder doesn't support FPS change, update the internal state
                    // so capture rate can be adjusted
                    _fps = fps.Value;
                    appliedFps = fps.Value;
                    messages.Add($"FPS: {fps.Value} (encoder FPS change not supported, capture rate will be adjusted)");
                    anySuccess = true;
                }
            }
        }

        // Bitrate change - apply to all encoders
        if (totalBitrateKbps.HasValue && totalBitrateKbps.Value > 0)
        {
            int perMonitorBitrate = totalBitrateKbps.Value / Math.Max(1, _monitorCount);
            Logger.Info($"[SIPSorcery] Bitrate update: total={totalBitrateKbps.Value}kbps, per-monitor={perMonitorBitrate}kbps");

            lock (_lock)
            {
                int successCount = 0;
                foreach (var track in _tracks)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetBitrate(perMonitorBitrate))
                        {
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} bitrate → {perMonitorBitrate}kbps");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetBitrate failed (encoder may not support runtime change)");
                        }
                    }
                }

                if (successCount > 0)
                {
                    appliedBitrate = totalBitrateKbps.Value;
                    messages.Add($"Bitrate: {totalBitrateKbps.Value}kbps ({successCount}/{_tracks.Count} encoders updated)");
                    anySuccess = true;
                }
                else if (_tracks.Count > 0)
                {
                    messages.Add("Bitrate change not supported by current encoder(s)");
                }
            }
        }

        string message = messages.Count > 0 ? string.Join(", ", messages) : "No changes applied";
        Logger.Info($"[SIPSorcery] UpdateConfig result: {message}");

        return (anySuccess, appliedFps, appliedBitrate, message);
    }

    /// <summary>
    /// Get current streaming config.
    /// </summary>
    public (int Fps, int TotalBitrateKbps, int MonitorCount) GetCurrentConfig() =>
        (_fps, _bitrateKbps, _monitorCount);

    private async Task LogStatsAsync()
    {
        while (_running && !_disposed)
        {
            await Task.Delay(10000);
            if (!_running) break;

            lock (_lock)
            {
                var stats = string.Join(", ", _tracks.Select(t => $"m{t.Index}:{t.SentFrames}"));
                Logger.Info($"[SIPSorcery] Stats: {stats}");
            }
        }
    }

    #region SDP Helpers

    private static (int? pt, string? fmtp) TryGetH264FromOfferSdp(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return (null, null);

        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var h264Pts = new HashSet<int>();

        foreach (var line in lines)
        {
            if (!line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("a=rtpmap:".Length);
            var sp = rest.IndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(rest.Substring(0, sp), out var candPt)) continue;
            var codec = rest.Substring(sp + 1);
            if (codec.IndexOf("H264/", StringComparison.OrdinalIgnoreCase) >= 0)
                h264Pts.Add(candPt);
        }

        if (h264Pts.Count == 0) return (null, null);

        int chosenPt = h264Pts.First();
        string? fmtp = null;
        var needle = "a=fmtp:" + chosenPt + " ";
        foreach (var line in lines)
        {
            if (line.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            {
                fmtp = line.Substring(needle.Length).Trim();
                break;
            }
        }

        return (chosenPt, fmtp);
    }

    private static bool ContainsAnnexBStartCode(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) return true;
        if (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) return true;
        return false;
    }

    private static byte[] TryConvertAvccToAnnexB(byte[] avcc)
    {
        if (avcc.Length < 8) return avcc;

        try
        {
            byte[] outBuf = new byte[avcc.Length + 32];
            int outPos = 0;
            int pos = 0;

            while (pos + 4 <= avcc.Length)
            {
                int nalLen = (avcc[pos] << 24) | (avcc[pos + 1] << 16) | (avcc[pos + 2] << 8) | avcc[pos + 3];
                pos += 4;
                if (nalLen <= 0 || pos + nalLen > avcc.Length) return avcc;

                int needed = outPos + 4 + nalLen;
                if (needed > outBuf.Length)
                {
                    var newBuf = new byte[Math.Max(outBuf.Length * 2, needed)];
                    Buffer.BlockCopy(outBuf, 0, newBuf, 0, outPos);
                    outBuf = newBuf;
                }

                outBuf[outPos++] = 0;
                outBuf[outPos++] = 0;
                outBuf[outPos++] = 0;
                outBuf[outPos++] = 1;

                Buffer.BlockCopy(avcc, pos, outBuf, outPos, nalLen);
                outPos += nalLen;
                pos += nalLen;
            }

            if (outPos <= 0) return avcc;
            var res = new byte[outPos];
            Buffer.BlockCopy(outBuf, 0, res, 0, outPos);
            return res;
        }
        catch
        {
            return avcc;
        }
    }

    private static byte[] StripLeadingAud(byte[] au)
    {
        if (au.Length < 4) return au;

        int pos;
        if (au.Length >= 4 && au[0] == 0 && au[1] == 0 && au[2] == 0 && au[3] == 1) { pos = 4; }
        else if (au.Length >= 3 && au[0] == 0 && au[1] == 0 && au[2] == 1) { pos = 3; }
        else return au;

        if (pos >= au.Length) return au;
        int nalType = au[pos] & 0x1F;
        if (nalType != 9) return au;

        for (int i = pos + 1; i + 3 < au.Length; i++)
        {
            if ((au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ||
                (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1))
            {
                var trimmed = new byte[au.Length - i];
                Buffer.BlockCopy(au, i, trimmed, 0, trimmed.Length);
                return trimmed;
            }
        }

        return au;
    }

    private static bool IsPrivateV4(System.Net.IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    private static bool CandidateLineIsHostPrivateV4(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var s = line.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!s.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!s.Contains(" typ host ", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return false;
        var addr = parts[4];
        if (addr.Contains(':')) return false;
        if (!System.Net.IPAddress.TryParse(addr, out var ip)) return false;
        return IsPrivateV4(ip);
    }

    private static string FilterAnswerSdpIceCandidates(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return sdp;
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var kept = new List<string>(lines.Length);

        foreach (var raw in lines)
        {
            var l = raw ?? string.Empty;
            var t = l.TrimStart();

            if (t.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                if (!CandidateLineIsHostPrivateV4(t))
                    continue;
            }
            else if (t.StartsWith("a=end-of-candidates", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(l);
        }

        return string.Join("\r\n", kept);
    }

    #endregion

    private void CloseConnection()
    {
        _running = false;
        _connected = false;

        lock (_lock)
        {
            foreach (var track in _tracks)
                track.Dispose();
            _tracks.Clear();
            _pendingDevices.Clear();
        }

        _pc?.close();
        _pc = null;

        Logger.Info("[SIPSorcery] Connection closed");
    }

    public void Stop()
    {
        if (!_running && _pc == null) return;
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
        Logger.Info("[SIPSorcery] Streaming resumed");

        // Request keyframe on all tracks for immediate visual update
        RequestKeyframe(-1);
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

        // Request keyframe for immediate visual update
        RequestKeyframe(monitorIndex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Logger.Error("[SIPSorcery] Disposed");
    }
}
