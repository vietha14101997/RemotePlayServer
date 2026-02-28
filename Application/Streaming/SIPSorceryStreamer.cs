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
public partial class SIPSorceryStreamer : IDisposable
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
    private volatile RTCDataChannel? _audioDc; // DataChannel for low-latency audio (bypasses client NetEQ)
    private volatile RTCDataChannel? _cursorDc; // DataChannel for low-latency cursor position updates
    private long _audioPacketsSent;
    private long _audioPacketsLastInterval; // Snapshot for per-interval rate calculation

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
            // SIPSorcery's internal UDP ReceiveFromAsync tasks throw SocketException 995
            // when PC closes. This is expected and silently filtered by the global
            // UnobservedTaskException handler in Program.cs.
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
}
