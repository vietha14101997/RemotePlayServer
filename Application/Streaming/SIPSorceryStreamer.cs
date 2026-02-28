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
using RemotePlayServer.Infrastructure.Capture;
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
    private volatile bool _phase3Active; // false during early capture → frames dropped to prevent WiFi congestion

    // Per-monitor pause state (allows pausing individual monitors)
    private volatile bool[] _monitorPaused = Array.Empty<bool>();

    // Audio pipeline
    private DesktopAudioCapture? _audioCapture;
    private OpusAudioEncoder? _opusEncoder;
    private bool _hasAudioTrack;
    private RTCDataChannel? _audioDc; // DataChannel for low-latency audio (bypasses client NetEQ)
    private long _audioPacketsSent;
    private long _audioPacketsLastInterval; // Snapshot for per-interval rate calculation

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

    // Shared reference clock for A/V sync: set on first frame (audio or video)
    private long _streamStartMs = -1;

    // Audio sync: last absolute audio RTP timestamp (protected by _audioSyncLock)
    private readonly object _audioSyncLock = new();
    private uint _lastAbsoluteAudioRtp;
    private bool _audioClockInitialized;

    // Adaptive bitrate controller
    private readonly AdaptiveBitrateController _bitrateController = new();

    // Deferred send mode: buffer frames in OnEncodedData, flush via FlushAllPendingFrames()
    // Prevents consistent jitter buffer asymmetry when SRTP lock serializes multi-track sends
    public bool DeferredSendEnabled { get; set; }
    private long _flushFrameCounter;

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
        public int KeyframeBurstRemaining; // Send N consecutive keyframes for WiFi resilience
        public long LastKeyframeRequestTicks; // Throttle: last time a keyframe was requested for this track
        public int KeyframeStaggerCountdown; // Frames to wait before forcing keyframe (stagger between tracks)

        // Shared-clock sync: capture-time-based RTP (replaces encoder-PTS-based)
        public uint LastAbsoluteRtp;
        public bool CaptureClockInitialized;
        // Store capture timestamp for use in OnEncodedData callback
        public long PendingCaptureTimestampMs;

        // Per-track lock for encoding: allows parallel NVENC sessions across tracks
        // (shared _lock was serializing all encoding, causing track delay accumulation)
        public readonly object EncodeLock = new();

        // Diagnostic: per-track encode latency
        public long LastEncodeStartTicks;
        public long EncodeLatencySum;
        public long EncodeLatencyCount;

        // Deferred send: buffer encoded frame for coordinated multi-track sending
        public volatile PendingFrameData? PendingFrame;
        public class PendingFrameData
        {
            public readonly byte[] Au;
            public readonly uint RtpStep;
            public PendingFrameData(byte[] au, uint rtpStep) { Au = au; RtpStep = rtpStep; }
        }

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

    /// <summary>
    /// Pre-warm DTLS/BouncyCastle crypto at server startup.
    /// First RTCPeerConnection triggers lazy cert generation + RNG init (~2-5s).
    /// Without this, the first client DTLS handshake times out.
    /// </summary>
    public static void PreWarmDtls()
    {
        try
        {
            Logger.Info("[SIPSorcery] Pre-warming DTLS crypto...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var warmupPc = new RTCPeerConnection(null);
            warmupPc.close();
            sw.Stop();
            Logger.Info($"[SIPSorcery] DTLS pre-warm done in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] DTLS pre-warm failed: {ex.Message}");
        }
    }

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
    public Task<string> ProcessOfferAsync(string offerSdp, List<(int w, int h)> dimensions)
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

        // Receive client-created DataChannel for low-latency audio.
        // Client creates DC "audio" (libwebrtc manages SCTP), server just receives and sends Opus via it.
        // Opus frames sent as binary messages: [type(1)][timestamp(8)][opus_data]
        _pc.ondatachannel += (dc) =>
        {
            Logger.Info($"[SIPSorcery] DataChannel received: label={dc.label}, id={dc.id}");
            if (dc.label == "audio")
            {
                _audioDc = dc;
                _audioDc.onopen += () => Logger.Info("[SIPSorcery] Audio DataChannel opened");
                _audioDc.onclose += () => { Logger.Info("[SIPSorcery] Audio DataChannel closed"); _audioDc = null; };
                Logger.Info("[SIPSorcery] Audio DataChannel wired for sending");
            }
        };
        _hasAudioTrack = true;
        Logger.Info("[SIPSorcery] Waiting for client audio DataChannel");

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
                InitializeAudio();
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
        Logger.Info($"[SIPSorcery] After setRemoteDescription: signalingState={_pc.signalingState}");

        var answer = _pc.createAnswer(null);
        Logger.Info($"[SIPSorcery] After createAnswer: signalingState={_pc.signalingState}, answer.type={answer.type}");

        // DO NOT call setLocalDescription(answer) — SIPSorcery 8.x signalingState is broken
        // (shows "closed" after setRemoteDescription), so setLocalDescription misinterprets
        // the answer as an offer, setting state to "have_local_offer" and corrupting DTLS config.
        // createAnswer() already configures DTLS internals correctly.

        _running = true;

        // Start stats logging
        _ = Task.Run(LogStatsAsync);

        var answerSdp = answer.sdp ?? "";

        // RFC 5763: Answerer MUST use "active" or "passive", NOT "actpass".
        // When signalingState works correctly (Android), SIPSorcery generates "active" natively.
        // When signalingState is broken (Unity Editor), it falls back to "actpass" which
        // libwebrtc rejects. Fix: replace with "active" for RFC compliance.
        if (answerSdp.Contains("a=setup:actpass"))
        {
            answerSdp = answerSdp.Replace("a=setup:actpass", "a=setup:active");
            Logger.Info("[SIPSorcery] SDP: fixed actpass -> active in answer (RFC 5763)");
        }

        if (!answerSdp.Contains("SAVPF"))
            answerSdp = answerSdp.Replace("SAVP", "SAVPF");
        answerSdp = FilterAnswerSdpIceCandidates(answerSdp);

        // Log DTLS-critical SDP attributes for debugging
        foreach (var line in answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] SDP: {line}");
        }

        Logger.Info($"[SIPSorcery] Answer ready, {answerSdp.Length} bytes");
        return Task.FromResult(answerSdp);
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
    /// Initialize audio capture and encoding pipeline.
    /// Non-fatal: if audio init fails, video streaming continues.
    /// </summary>
    private void InitializeAudio()
    {
        // Only init if we added an audio track during SDP negotiation
        if (_pc == null || !_hasAudioTrack)
        {
            if (!_hasAudioTrack)
                Logger.Info("[SIPSorcery] Audio pipeline skipped (no audio track negotiated)");
            return;
        }

        try
        {
            _audioCapture = new DesktopAudioCapture();
            _opusEncoder = new OpusAudioEncoder();

            // Wire: capture -> encoder -> RTP send
            _audioCapture.OnAudioData += (pcm, length, sampleRate, channels, timestampMs) =>
            {
                if (_isPaused || !_connected || !_running) return;
                _opusEncoder.EncodePcm(pcm, length, sampleRate, channels, timestampMs);
            };

            _opusEncoder.OnEncodedAudio += (opusData, opusLength, rtpDuration, timestampMs) =>
            {
                if (!_connected || !_running || _pc == null) return;
                // Don't send audio during early capture — contributes to WiFi congestion
                if (!_phase3Active) return;
                try
                {
                    // DataChannel path: send Opus frame as binary message
                    // Format: [type(1)][timestamp(8)][opus_data]
                    // Client decodes with Concentus + OnAudioFilterRead (~20ms latency)
                    if (_audioDc?.readyState == RTCDataChannelState.open)
                    {
                        var msg = new byte[1 + 8 + opusLength];
                        msg[0] = 0x01; // Audio frame type
                        BitConverter.TryWriteBytes(msg.AsSpan(1, 8), timestampMs);
                        Buffer.BlockCopy(opusData, 0, msg, 9, opusLength);
                        _audioDc.send(msg);
                        Interlocked.Increment(ref _audioPacketsSent);
                    }
                    else
                    {
                        // RTP fallback: used when DataChannel not yet open
                        var packet = new byte[opusLength];
                        Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);

                        uint audioStep;
                        lock (_audioSyncLock)
                        {
                            if (!_audioClockInitialized && timestampMs > 0)
                                audioStep = CalculateAudioRtpStep(timestampMs);
                            else
                                audioStep = rtpDuration;
                        }

                        _pc.SendAudio(audioStep, packet);
                        Interlocked.Increment(ref _audioPacketsSent);
                    }
                }
                catch (Exception ex)
                {
                    if (Environment.TickCount64 % 5000 < 20)
                        Logger.Error($"[SIPSorcery] Audio send error: {ex.Message}");
                }
            };

            _audioCapture.Start();

            Logger.Info("[SIPSorcery] Audio pipeline started (WASAPI loopback -> Opus -> DataChannel)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Audio init failed (non-fatal, video continues): {ex.Message}");

            try { _opusEncoder?.Dispose(); } catch { }
            try { _audioCapture?.Dispose(); } catch { }
            _opusEncoder = null;
            _audioCapture = null;
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
    public void PushBgraTexture(int monitorIndex, ID3D11Texture2D bgraTexture, int width, int height, long captureTimestampMs = 0)
    {
        if (!_running || _disposed || !_connected || _isPaused) return;
        // During early capture (before Phase 3), drop frames to prevent WiFi congestion.
        // Capture hardware stays warm but no encoding/sending — saves bandwidth for ICE/DTLS.
        if (!_phase3Active) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        // Check per-monitor pause
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null) return;

        // Per-track lock: allows parallel NVENC encoding across tracks.
        // Previously used shared _lock which serialized all encoding,
        // causing the second track to accumulate transport delay on the client.
        lock (track.EncodeLock)
        {
            try
            {
                // Store capture timestamp for use in OnEncodedData callback
                track.PendingCaptureTimestampMs = captureTimestampMs;

                // Stagger countdown: decrement and trigger keyframe when it reaches 0
                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                // Force keyframe for first 5 frames, explicit request, or burst
                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
                track.ForceNextKeyframe = false;
                if (track.KeyframeBurstRemaining > 0) track.KeyframeBurstRemaining--;

                // Encode BGRA directly - no staging texture or copy needed
                track.LastEncodeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
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

    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height, long captureTimestampMs = 0)
    {
        if (!_running || _disposed || !_connected || _isPaused) return;
        // During early capture (before Phase 3), drop frames to prevent WiFi congestion
        if (!_phase3Active) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        // Check per-monitor pause
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null) return;

        // Per-track lock: allows parallel encoding across tracks
        lock (track.EncodeLock)
        {
            try
            {
                var device = track.Device ?? _sharedDevice;
                if (device == null) return;

                // Store capture timestamp for use in OnEncodedData callback
                track.PendingCaptureTimestampMs = captureTimestampMs;

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

                // Stagger countdown: decrement and trigger keyframe when it reaches 0
                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                // Force keyframe for first 5 frames, explicit request, or burst
                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
                track.ForceNextKeyframe = false;
                if (track.KeyframeBurstRemaining > 0) track.KeyframeBurstRemaining--;

                track.LastEncodeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
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

        // Track encode latency for diagnostics
        long encodeStartTicks = track.LastEncodeStartTicks;
        if (encodeStartTicks > 0)
        {
            long encodeEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long latencyUs = (encodeEndTicks - encodeStartTicks) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            Interlocked.Add(ref track.EncodeLatencySum, latencyUs);
            Interlocked.Increment(ref track.EncodeLatencyCount);
        }

        try
        {
            // Convert to Annex B if needed and strip AUD
            byte[] au = nalData;
            if (!ContainsAnnexBStartCode(au))
                au = TryConvertAvccToAnnexB(au);
            au = StripLeadingAud(au);

            // Use capture timestamp if available, fall back to encoder PTS
            long captureMs = track.PendingCaptureTimestampMs;
            uint rtpStep = captureMs > 0
                ? CalculateRtpStepFromCaptureTime(track, captureMs)
                : CalculateRtpStep(track, pts100ns);

            // Log per-track diagnostics on keyframe sends
            if (isKeyframe)
            {
                long encCount = Interlocked.Read(ref track.EncodeLatencyCount);
                long avgEncUs = encCount > 0 ? Interlocked.Read(ref track.EncodeLatencySum) / encCount : 0;
                Logger.Info($"[SIPSorcery] Track {track.Index} KEYFRAME: rtpStep={rtpStep}, captureMs={captureMs}, " +
                    $"avgEncLatency={avgEncUs}us, sentFrames={Interlocked.Read(ref track.SentFrames)}");
            }

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

            if (DeferredSendEnabled)
            {
                // Deferred mode: buffer frame for coordinated multi-track sending.
                // FlushAllPendingFrames() sends all tracks in alternating order
                // after the post-encode barrier, preventing jitter asymmetry.
                track.PendingFrame = new TrackInfo.PendingFrameData(au, rtpStep);
            }
            else
            {
                // Immediate send (single-monitor or no barrier sync)
                SendFrameImmediate(track, au, rtpStep, frameNum);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                Logger.Error($"[SIPSorcery] Track {track.Index} send error: {ex.Message}");
        }
    }

    private void SendFrameImmediate(TrackInfo track, byte[] au, uint rtpStep, long frameNum)
    {
        if (_pc!.VideoStreamList != null && track.Index < _pc.VideoStreamList.Count)
        {
            var videoStream = _pc.VideoStreamList[track.Index];
            videoStream.SendVideo(rtpStep, au);
            Interlocked.Increment(ref track.SentFrames);
        }
        else if (track.Index == 0)
        {
            _pc!.SendVideo(rtpStep, au);
            Interlocked.Increment(ref track.SentFrames);
        }
        else
        {
            if (frameNum < 5)
                Logger.Info($"[SIPSorcery] Track {track.Index} SKIP: VideoStreamList null or index out of range");
        }
    }

    /// <summary>
    /// Send all buffered frames in alternating track order.
    /// Called by the post-encode barrier after all tracks finish encoding.
    /// Alternating order prevents one track's RTP packets from consistently
    /// arriving before the other's, which causes client jitter buffer asymmetry.
    /// </summary>
    public void FlushAllPendingFrames()
    {
        if (!_running || _pc == null || !_connected) return;

        // Snapshot tracks under lock to prevent race with CloseConnection._tracks.Clear()
        TrackInfo[] snapshot;
        lock (_lock) { snapshot = _tracks.ToArray(); }

        long frame = Interlocked.Increment(ref _flushFrameCounter);
        bool reverse = (frame % 2) == 0;
        int count = snapshot.Length;

        for (int iter = 0; iter < count; iter++)
        {
            int i = reverse ? (count - 1 - iter) : iter;
            var track = snapshot[i];
            var pending = track.PendingFrame;
            if (pending == null) continue;
            track.PendingFrame = null;

            try
            {
                long frameNum = Interlocked.Read(ref track.SentFrames);
                SendFrameImmediate(track, pending.Au, pending.RtpStep, frameNum);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                    Logger.Error($"[SIPSorcery] Track {i} flush error: {ex.Message}");
            }
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

    /// <summary>
    /// Calculate RTP step from capture wallclock time instead of encoder PTS.
    /// All tracks sharing the same _streamStartMs produce identical absolute RTP
    /// for simultaneously-captured frames (barrier-synced), fixing multi-track desync.
    /// Audio also uses the same _streamStartMs, fixing A/V desync.
    /// </summary>
    private uint CalculateRtpStepFromCaptureTime(TrackInfo track, long captureTimestampMs)
    {
        const int ClockRate = 90000;
        uint fallback = (uint)Math.Max(1, ClockRate / Math.Max(1, _fps));

        // Initialize shared stream start time (first frame from any track sets this)
        if (Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        long startMs = Interlocked.Read(ref _streamStartMs);
        long elapsedMs = captureTimestampMs - startMs;
        if (elapsedMs < 0) elapsedMs = 0;

        // Convert elapsed wallclock to absolute RTP timestamp (90kHz)
        uint absoluteRtp = (uint)((long)ClockRate * elapsedMs / 1000L);

        lock (track)
        {
            if (!track.CaptureClockInitialized)
            {
                track.CaptureClockInitialized = true;
                track.LastAbsoluteRtp = absoluteRtp;
                // First frame: return the absolute timestamp as the initial step
                // This seeds the RTP sequence at the correct wallclock position
                return absoluteRtp > 0 ? absoluteRtp : fallback;
            }

            // Step = difference from last sent absolute RTP
            uint step;
            if (absoluteRtp > track.LastAbsoluteRtp)
            {
                step = absoluteRtp - track.LastAbsoluteRtp;
            }
            else
            {
                // Same or older timestamp (duplicate frame, or clock wraparound)
                step = fallback;
            }

            track.LastAbsoluteRtp = absoluteRtp;

            // Clamp to reasonable range: 1 tick to 500ms worth of ticks
            step = Math.Clamp(step, 1, (uint)(ClockRate / 2));
            return step;
        }
    }

    /// <summary>
    /// Calculate the INITIAL audio RTP timestamp offset to anchor audio relative to video.
    /// Called ONLY for the first audio frame, under _audioSyncLock.
    /// All subsequent frames use fixed 480 increment.
    /// This ensures audio and video RTP timestamps share the same _streamStartMs origin,
    /// allowing the client to correctly align them via RTCP Sender Reports.
    /// </summary>
    /// <remarks>Caller must hold _audioSyncLock.</remarks>
    private uint CalculateAudioRtpStep(long audioTimestampMs)
    {
        const int AudioClockRate = 48000;
        const uint DefaultStep = 480; // 10ms at 48kHz

        // Initialize shared stream start time (first frame from any track sets this)
        if (Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, audioTimestampMs, -1);

        long startMs = Interlocked.Read(ref _streamStartMs);
        long elapsedMs = audioTimestampMs - startMs;
        if (elapsedMs < 0) elapsedMs = 0;

        // Convert elapsed wallclock to absolute audio RTP timestamp (48kHz)
        uint absoluteRtp = (uint)((long)AudioClockRate * elapsedMs / 1000L);

        _audioClockInitialized = true;
        _lastAbsoluteAudioRtp = absoluteRtp;
        return absoluteRtp > 0 ? absoluteRtp : DefaultStep;
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

    /// <summary>
    /// Request keyframe for a track (or all tracks if monitorIndex == -1).
    /// When force=false, rate-limited to 1 request per second per track to prevent
    /// keyframe storms that cause WiFi congestion.
    /// When requesting ALL tracks (monitorIndex == -1), keyframes are staggered:
    /// Track 0 gets immediate keyframe, Track N gets it after N*10 frames (~167ms @ 60fps).
    /// This prevents simultaneous keyframe bursts that saturate WiFi and drop Track 1's packets.
    /// Internal callers (Resume, ResumeMonitor) should use force=true.
    /// </summary>
    public void RequestKeyframe(int monitorIndex = -1, bool force = false)
    {
        const long MinIntervalMs = 1000;
        const int StaggerFramesPerTrack = 10; // ~167ms @ 60fps between track keyframes
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (monitorIndex == -1)
            {
                // Stagger: Track 0 immediate, Track 1 after 10 frames, Track 2 after 20, etc.
                for (int i = 0; i < _tracks.Count; i++)
                {
                    var t = _tracks[i];
                    if (force || now - t.LastKeyframeRequestTicks >= MinIntervalMs)
                    {
                        if (i == 0)
                        {
                            t.ForceNextKeyframe = true;
                        }
                        else
                        {
                            t.KeyframeStaggerCountdown = i * StaggerFramesPerTrack;
                        }
                        t.LastKeyframeRequestTicks = now;
                    }
                }
            }
            else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                var t = _tracks[monitorIndex];
                if (force || now - t.LastKeyframeRequestTicks >= MinIntervalMs)
                {
                    t.ForceNextKeyframe = true;
                    t.LastKeyframeRequestTicks = now;
                }
            }
        }
    }

    public void RequestKeyframeBurst(int monitorIndex = -1, int count = 3)
    {
        lock (_lock)
        {
            if (monitorIndex == -1)
            {
                foreach (var t in _tracks) t.KeyframeBurstRemaining = count;
            }
            else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                _tracks[monitorIndex].KeyframeBurstRemaining = count;
            }
        }
        Logger.Info($"[SIPSorcery] Keyframe burst: monitor={monitorIndex}, count={count}");
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

    public void SetWiFiMode(bool isWiFi) => _bitrateController.IsWiFiMode = isWiFi;

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
                var stats = string.Join(", ", _tracks.Select(t =>
                {
                    long encCount = Interlocked.Read(ref t.EncodeLatencyCount);
                    long avgUs = encCount > 0 ? Interlocked.Read(ref t.EncodeLatencySum) / encCount : 0;
                    return $"m{t.Index}:{t.SentFrames}f,enc={avgUs}us";
                }));
                var audioPkts = Interlocked.Read(ref _audioPacketsSent);
                long lastInterval = _audioPacketsLastInterval;
                long intervalPkts = audioPkts - lastInterval;
                _audioPacketsLastInterval = audioPkts;
                float audioRate = intervalPkts / 10.0f; // packets per second over 10s interval
                var audioInfo = _hasAudioTrack ? $", audio:{audioPkts}pkts ({audioRate:F1}/sec)" : "";
                Logger.Info($"[SIPSorcery] Stats: {stats}{audioInfo}");
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

        // Reset shared sync clock for next connection
        Interlocked.Exchange(ref _streamStartMs, -1);
        lock (_audioSyncLock)
        {
            _audioClockInitialized = false;
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
                track.Dispose();
            _tracks.Clear();
            _pendingDevices.Clear();
        }

        try { _audioDc?.close(); } catch { }
        _audioDc = null;

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
    /// <summary>
    /// Reset sync clocks so next frames start from a fresh time origin.
    /// Call when Phase 3 starts to clear stale state from early capture.
    /// This prevents the client's jitter buffer from inflating due to
    /// RTP timestamp gaps between early-capture and real streaming.
    /// </summary>
    public void ResetSyncState()
    {
        Interlocked.Exchange(ref _streamStartMs, -1);
        lock (_audioSyncLock)
        {
            _audioClockInitialized = false;
            _lastAbsoluteAudioRtp = 0;
        }
        lock (_lock)
        {
            foreach (var track in _tracks)
            {
                track.CaptureClockInitialized = false;
                track.LastAbsoluteRtp = 0;
                track.TimestampInitialized = false;
                track.LastPts100ns = -1;
            }
        }
        // Reset deferred send counter for clean alternation on reconnect
        Interlocked.Exchange(ref _flushFrameCounter, 0);
        // Enable frame sending — early capture frames were dropped to prevent WiFi congestion
        _phase3Active = true;
        Logger.Info("[SIPSorcery] Sync state reset + Phase 3 active (frames will now be sent)");
    }

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
        Logger.Info("[SIPSorcery] Streaming resumed");

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Logger.Error("[SIPSorcery] Disposed");
    }
}
