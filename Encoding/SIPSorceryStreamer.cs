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
using RemotePlayServer.Utils;

namespace RemotePlayServer.Encoding;

/// <summary>
/// SIPSorcery-based WebRTC streamer with interface matching LibDataChannelStreamer.
/// Uses SIPSorcery's VideoStreamList for multi-track support (v6.0.8+).
/// </summary>
public class SIPSorceryStreamer : IDisposable
{
    private readonly int _monitorCount;
    private readonly int _fps;
    private readonly int _bitrateKbps;
    private ID3D11Device? _sharedDevice;

    private RTCPeerConnection? _pc;
    private readonly List<TrackInfo> _tracks = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, ID3D11Device> _pendingDevices = new();

    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _connected;

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

    // Events matching LibDataChannelStreamer interface
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
        _sharedDevice = device;

        Console.WriteLine($"[SIPSorcery] Created: {monitorCount} monitors, {fps}fps, {kbps}kbps");
    }

    public void SetDevice(ID3D11Device device)
    {
        _sharedDevice = device;
    }

    public void SetDeviceForMonitor(int monitorIndex, ID3D11Device device)
    {
        lock (_lock)
        {
            if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                _tracks[monitorIndex].Device = device;
                Console.WriteLine($"[SIPSorcery] Set device for existing track {monitorIndex}");
            }
            else
            {
                _pendingDevices[monitorIndex] = device;
                Console.WriteLine($"[SIPSorcery] Stored pending device for monitor {monitorIndex}");
            }
        }
    }

    /// <summary>
    /// Process single SDP offer (with N m= sections), create N tracks, return single answer.
    /// Interface matches LibDataChannelStreamer.ProcessOfferAsync().
    /// </summary>
    public async Task<string> ProcessOfferAsync(string offerSdp, List<(int w, int h)> dimensions)
    {
        if (_running)
        {
            Console.WriteLine("[SIPSorcery] Closing existing connection for reconnect...");
            CloseConnection();
        }

        Console.WriteLine($"[SIPSorcery] Processing offer for {dimensions.Count} monitors");

        // Parse H264 payload type from offer
        var (h264Pt, h264Fmtp) = TryGetH264FromOfferSdp(offerSdp);
        Console.WriteLine($"[SIPSorcery] Offer H264 pt={h264Pt ?? 96}, fmtp={h264Fmtp ?? "default"}");

        // Create PeerConnection (no STUN for LAN)
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>()
        };
        _pc = new RTCPeerConnection(cfg);
        Console.WriteLine("[SIPSorcery] PeerConnection created");

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

            // Check for pending per-monitor device
            ID3D11Device? deviceForTrack = _sharedDevice;
            if (_pendingDevices.TryGetValue(i, out var pendingDevice))
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

            Console.WriteLine($"[SIPSorcery] Added track {i}: {w}x{h}");
        }

        // ICE candidate forwarding
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Console.WriteLine($"[SIPSorcery] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        // Connection state changes
        _pc.oniceconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[SIPSorcery] ICE state: {state}");
            if (state == RTCIceConnectionState.connected)
            {
                _connected = true;
                Console.WriteLine("[SIPSorcery] ICE CONNECTED - initializing encoders");
                InitializeEncoders();
                OnAllTracksReady?.Invoke();
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

        _pc.onconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[SIPSorcery] Peer state: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Console.WriteLine("[SIPSorcery] DTLS CONNECTED - media can flow now");
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                Console.WriteLine("[SIPSorcery] DTLS FAILED - check certificate/fingerprint");
                _connected = false;
                OnConnectionFailed?.Invoke();
            }
        };

        _pc.onsignalingstatechange += () =>
        {
            Console.WriteLine($"[SIPSorcery] Signaling state: {_pc.signalingState}");
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

        // Log DTLS-critical SDP attributes for debugging
        foreach (var line in answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Console.WriteLine($"[SIPSorcery] SDP: {line}");
        }

        Console.WriteLine($"[SIPSorcery] Answer ready, {answerSdp.Length} bytes");
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
            Console.WriteLine($"[SIPSorcery] Added remote ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SIPSorcery] AddIceCandidate error: {ex.Message}");
        }
    }

    private void InitializeEncoders()
    {
        // Detect GPU vendor once for all tracks
        var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();
        Console.WriteLine($"[SIPSorcery] Detected GPU vendor: {gpuVendor}");

        lock (_lock)
        {
            foreach (var track in _tracks)
            {
                if (track.Encoder != null) continue;

                var device = track.Device ?? _sharedDevice;
                if (device == null)
                {
                    Console.WriteLine($"[SIPSorcery] Track {track.Index}: No D3D11 device");
                    continue;
                }

                try
                {
                    ITextureEncoder? encoder = CreateEncoderForGpu(gpuVendor);
                    if (encoder == null)
                    {
                        Console.WriteLine($"[SIPSorcery] Track {track.Index}: No suitable encoder found for {gpuVendor}");
                        continue;
                    }

                    encoder.OnEncodedData += (nal, keyframe, pts) =>
                        OnEncodedData(track, nal, keyframe, pts);

                    if (encoder.Initialize(track.Width, track.Height, _fps, _bitrateKbps, device))
                    {
                        track.Encoder = encoder;
                        string encoderName = encoder.GetType().Name.Replace("NativeWrapper", "");
                        Console.WriteLine($"[SIPSorcery] Track {track.Index}: {encoderName} encoder initialized");
                    }
                    else
                    {
                        Console.WriteLine($"[SIPSorcery] Track {track.Index}: Encoder init failed");
                        encoder.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SIPSorcery] Track {track.Index}: Encoder error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Create the appropriate hardware encoder based on GPU vendor
    /// </summary>
    private static ITextureEncoder? CreateEncoderForGpu(GpuVendorDetector.GpuVendor gpuVendor)
    {
        switch (gpuVendor)
        {
            case GpuVendorDetector.GpuVendor.AMD:
                // AMD: Use AMF encoder
                if (AmfNativeWrapper.IsAvailable())
                {
                    Console.WriteLine("[SIPSorcery] Creating AMF encoder for AMD GPU");
                    return new AmfNativeWrapper();
                }
                break;

            case GpuVendorDetector.GpuVendor.NVIDIA:
                // NVIDIA: Use NVENC encoder
                if (NvencNativeWrapper.IsAvailable())
                {
                    Console.WriteLine("[SIPSorcery] Creating NVENC encoder for NVIDIA GPU");
                    return new NvencNativeWrapper();
                }
                // Fallback to AMF if available (some systems have both)
                if (AmfNativeWrapper.IsAvailable())
                {
                    Console.WriteLine("[SIPSorcery] NVENC not available, falling back to AMF");
                    return new AmfNativeWrapper();
                }
                break;

            case GpuVendorDetector.GpuVendor.Intel:
                // Intel: Use QSV encoder
                if (QsvNativeWrapper.IsAvailable())
                {
                    Console.WriteLine("[SIPSorcery] Creating QSV encoder for Intel GPU");
                    return new QsvNativeWrapper();
                }
                break;

            default:
                // Try each encoder in order of preference
                Console.WriteLine("[SIPSorcery] Unknown GPU, trying available encoders...");
                if (NvencNativeWrapper.IsAvailable())
                    return new NvencNativeWrapper();
                if (AmfNativeWrapper.IsAvailable())
                    return new AmfNativeWrapper();
                if (QsvNativeWrapper.IsAvailable())
                    return new QsvNativeWrapper();
                break;
        }

        Console.WriteLine("[SIPSorcery] No hardware encoder available!");
        return null;
    }

    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed || !_connected) return;
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
                    Console.WriteLine($"[SIPSorcery] Track {monitorIndex} encode error: {ex.Message}");
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

            // DEBUG: Log NAL types for first few frames
            long frameNum = Interlocked.Read(ref track.SentFrames);
            if (frameNum < 5 || isKeyframe)
            {
                var nalTypes = GetNalTypes(au);
                Console.WriteLine($"[SIPSorcery] Track {track.Index} frame #{frameNum}: {au.Length}B, NAL types=[{string.Join(",", nalTypes)}], key={isKeyframe}");
            }

            // DEBUG: Log VideoStreamList info on first frame of each track
            if (frameNum == 0)
            {
                var streamCount = _pc.VideoStreamList?.Count ?? 0;
                Console.WriteLine($"[SIPSorcery] Track {track.Index} first frame: VideoStreamList.Count={streamCount}, rtpStep={rtpStep}");

                // Log SSRC info for each video stream
                if (_pc.VideoStreamList != null)
                {
                    for (int i = 0; i < Math.Min(3, _pc.VideoStreamList.Count); i++)
                    {
                        var vs = _pc.VideoStreamList[i];
                        Console.WriteLine($"[SIPSorcery] VideoStream[{i}]: SSRC={vs.LocalTrack?.Ssrc ?? 0}");
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
                    Console.WriteLine($"[SIPSorcery] Track {track.Index} SKIP: VideoStreamList null or index out of range");
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                Console.WriteLine($"[SIPSorcery] Track {track.Index} send error: {ex.Message}");
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

    public void ProcessFpsFeedback(int monitorIndex, float effectiveFps, int droppedFrames)
    {
        Console.WriteLine($"[SIPSorcery] FPS feedback m{monitorIndex}: {effectiveFps:F1}fps, dropped={droppedFrames}");
    }

    public int GetCurrentTargetFps(int monitorIndex) => _fps;

    private async Task LogStatsAsync()
    {
        while (_running && !_disposed)
        {
            await Task.Delay(3000);
            if (!_running) break;

            lock (_lock)
            {
                var stats = string.Join(", ", _tracks.Select(t => $"m{t.Index}:{t.SentFrames}"));
                Console.WriteLine($"[SIPSorcery] Stats: {stats}");
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

        Console.WriteLine("[SIPSorcery] Connection closed");
    }

    public void Stop()
    {
        if (!_running && _pc == null) return;
        Console.WriteLine("[SIPSorcery] Stopping...");
        CloseConnection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Console.WriteLine("[SIPSorcery] Disposed");
    }
}
