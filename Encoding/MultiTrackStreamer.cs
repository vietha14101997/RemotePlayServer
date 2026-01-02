#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Multi-track WebRTC streamer: Creates N video tracks in a single PeerConnection.
/// Each monitor gets its own encoder and video track for optimal Android decoder compatibility.
/// This avoids the 3840x1080 combined frame issue on Android MediaCodec.
/// </summary>
public class MultiTrackStreamer : IDisposable
{
    private readonly int _fps;
    private readonly int _kbps;
    private readonly int _monitorCount;
    private ID3D11Device? _device;
    
    private RTCPeerConnection? _pc;
    private readonly List<MonitorStream> _streams = new();
    private readonly object _lock = new();
    
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _iceConnected;
    
    public bool IsRunning => _running;
    public RTCIceConnectionState IceConnectionState => _pc?.iceConnectionState ?? RTCIceConnectionState.@new;
    
    public event Action? OnPeerDisconnected;
    public event Action<string>? OnIceCandidate;

    /// <summary>
    /// Per-monitor stream state
    /// </summary>
    private class MonitorStream : IDisposable
    {
        public int MonitorIndex { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public AmfNativeWrapper? Encoder { get; set; }
        public MediaStreamTrack? VideoTrack { get; set; }
        public long EnqueueCount;
        public long SentCount;
        public long LastPts100ns;
        
        // Staging texture for zero-copy
        public ID3D11Texture2D? StagingNV12 { get; set; }
        
        public void Dispose()
        {
            try { Encoder?.Dispose(); } catch { }
            try { StagingNV12?.Dispose(); } catch { }
            Encoder = null;
            StagingNV12 = null;
        }
    }

    public MultiTrackStreamer(int monitorCount, int fps, int kbps, ID3D11Device? device = null)
    {
        _monitorCount = monitorCount;
        _fps = fps;
        _kbps = kbps;
        _device = device;
        
        Console.WriteLine($"[MultiTrack] Created: {monitorCount} monitors, {fps}fps, {kbps}kbps per track");
    }

    public void SetDevice(ID3D11Device device)
    {
        _device = device;
    }

    public void AddIceCandidate(string candidate)
    {
        if (_pc == null) return;
        try
        {
            var candStr = (candidate ?? string.Empty).Trim();
            if (candStr.Length == 0) return;

            if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring(2);
            if (candStr.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring("candidate:".Length);
            if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = "candidate:" + candStr;

            var init = new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = "0" };
            _pc.addIceCandidate(init);
            Console.WriteLine($"[MultiTrack] Added remote ICE: {candStr.Substring(0, Math.Min(60, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiTrack] AddIceCandidate error: {ex.Message}");
        }
    }

    /// <summary>
    /// Process client offer and create answer with N video tracks
    /// </summary>
    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp, List<(int w, int h)> monitorSizes)
    {
        if (_running) throw new InvalidOperationException("Already running");
        
        Console.WriteLine($"[MultiTrack] Processing offer for {monitorSizes.Count} monitors");
        
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>()
        };
        
        _pc = new RTCPeerConnection(cfg);
        Console.WriteLine("[MultiTrack] PeerConnection created");

        // Parse H264 from offer
        var (h264Pt, h264Fmtp) = TryGetH264FromOfferSdp(offerSdp);
        if (h264Pt.HasValue)
            Console.WriteLine($"[MultiTrack] Offer H264 pt={h264Pt.Value}");

        // Create N video tracks - one per monitor
        for (int i = 0; i < monitorSizes.Count; i++)
        {
            var (w, h) = monitorSizes[i];
            var stream = new MonitorStream
            {
                MonitorIndex = i,
                Width = w,
                Height = h
            };
            
            // Create H264 format for this track
            var h264 = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                id: h264Pt ?? (96 + i), // Use different payload types if needed
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
            
            // Set track ID/mid to identify which monitor this is
            // Unity will receive tracks with mid=0,1,2,...
            
            _pc.addTrack(track);
            stream.VideoTrack = track;
            _streams.Add(stream);
            
            Console.WriteLine($"[MultiTrack] Added track {i}: {w}x{h}");
        }

        // ICE candidate forwarding
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Console.WriteLine($"[MultiTrack] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        _pc.oniceconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[MultiTrack] ICE connection state changed to: {state}");
            if (state == RTCIceConnectionState.connected)
            {
                _iceConnected = true;
                Console.WriteLine("[MultiTrack] ICE CONNECTED - initializing encoders");
                InitializeEncoders();
            }
            else if (state == RTCIceConnectionState.checking)
            {
                Console.WriteLine("[MultiTrack] ICE is checking candidates...");
            }
            else if (state == RTCIceConnectionState.disconnected)
            {
                _iceConnected = false;
                Console.WriteLine("[MultiTrack] ICE disconnected - attempting reconnect?");
                OnPeerDisconnected?.Invoke();
            }
            else if (state == RTCIceConnectionState.failed)
            {
                _iceConnected = false;
                Console.WriteLine("[MultiTrack] ICE connection FAILED!");
                OnPeerDisconnected?.Invoke();
            }
            else if (state == RTCIceConnectionState.closed)
            {
                _iceConnected = false;
                Console.WriteLine("[MultiTrack] ICE connection closed");
                OnPeerDisconnected?.Invoke();
            }
        };
        
        _pc.onconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[MultiTrack] Peer connection state changed to: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Console.WriteLine("[MultiTrack] Peer connection fully established");
            }
            else if (state == RTCPeerConnectionState.disconnected)
            {
                Console.WriteLine("[MultiTrack] Peer connection disconnected");
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                Console.WriteLine("[MultiTrack] Peer connection FAILED");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                Console.WriteLine("[MultiTrack] Peer connection closed");
            }
        };

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        _running = true;
        
        // Stats logging
        _ = Task.Run(async () =>
        {
            while (_running)
            {
                await Task.Delay(2000);
                var stats = string.Join(", ", _streams.Select(s => $"m{s.MonitorIndex}:{s.SentCount}"));
                Console.WriteLine($"[MultiTrack] stats: {stats}");
            }
        });

        // Fix: Only add F if not already SAVPF (avoid SAVPF -> SAVPFF bug)
        var answerSdp = answer.sdp ?? "";
        if (!answerSdp.Contains("SAVPF"))
            answerSdp = answerSdp.Replace("SAVP", "SAVPF");
        answerSdp = FilterAnswerSdpIceCandidates(answerSdp);
        
        // Log m= lines for debugging
        var mLines = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("m=video", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine($"[MultiTrack] Answer has {mLines.Count} video m-lines");

        return answerSdp;
    }

    private void InitializeEncoders()
    {
        if (_device == null)
        {
            Console.WriteLine("[MultiTrack] No D3D11 device - cannot initialize encoders");
            return;
        }
        
        lock (_lock)
        {
            foreach (var stream in _streams)
            {
                if (stream.Encoder != null) continue;
                
                try
                {
                    var encoder = new AmfNativeWrapper();
                    encoder.OnEncodedData += (nalData, isKeyframe, pts) => 
                        OnEncodedData(stream, nalData, isKeyframe, pts);
                    
                    if (encoder.Initialize(stream.Width, stream.Height, _fps, _kbps, _device))
                    {
                        stream.Encoder = encoder;
                        Console.WriteLine($"[MultiTrack] Encoder {stream.MonitorIndex} initialized: {stream.Width}x{stream.Height}");
                    }
                    else
                    {
                        Console.WriteLine($"[MultiTrack] Encoder {stream.MonitorIndex} init FAILED");
                        encoder.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MultiTrack] Encoder {stream.MonitorIndex} error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Push NV12 texture for a specific monitor
    /// </summary>
    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed || !_iceConnected) return;
        if (monitorIndex < 0 || monitorIndex >= _streams.Count) return;
        
        var stream = _streams[monitorIndex];
        if (stream.Encoder == null) return;
        
        lock (_lock)
        {
            try
            {
                // Create staging texture if needed
                if (stream.StagingNV12 == null && _device != null)
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
                    stream.StagingNV12 = _device.CreateTexture2D(desc);
                }
                
                // Copy and encode
                if (stream.StagingNV12 != null)
                {
                    _device!.ImmediateContext.CopyResource(stream.StagingNV12, nv12Texture);
                    bool forceIdr = Interlocked.Read(ref stream.SentCount) == 0;
                    stream.Encoder.EncodeTexture(stream.StagingNV12, forceKeyframe: forceIdr);
                    Interlocked.Increment(ref stream.EnqueueCount);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MultiTrack] PushTexture m{monitorIndex} error: {ex.Message}");
            }
        }
    }

    private void OnEncodedData(MonitorStream stream, byte[] nalData, bool isKeyframe, long pts)
    {
        if (!_running || _pc == null || !_iceConnected) return;
        if (_pc.connectionState != RTCPeerConnectionState.connected) return;
        if (stream.VideoTrack == null) return;
        
        try
        {
            byte[] au = nalData;
            if (!ContainsAnnexBStartCode(au))
                au = TryConvertAvccToAnnexB(au);
            au = StripLeadingAud(au);

            uint rtpStep = GetRtpStep(stream, pts);
            
            // SIPSorcery limitation: SendVideo() sends to ALL video tracks
            // For true multi-track, need to use separate PeerConnections (Option B)
            // For now, only send from monitor 0 to avoid conflicts
            if (stream.MonitorIndex == 0)
            {
                _pc.SendVideo(rtpStep, au);
            }
            // TODO: Implement proper multi-track with separate PeerConnections
            
            long sent = Interlocked.Increment(ref stream.SentCount);
            if (sent <= 3 || isKeyframe)
            {
                Console.WriteLine($"[MultiTrack] m{stream.MonitorIndex} frame #{sent}: {au.Length}B, key={isKeyframe}");
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref stream.SentCount) % 60 == 0)
                Console.WriteLine($"[MultiTrack] m{stream.MonitorIndex} send error: {ex.Message}");
        }
    }

    private uint GetRtpStep(MonitorStream stream, long pts100ns)
    {
        uint fallback = (uint)Math.Max(1, 90000 / Math.Max(1, _fps));
        long last = Interlocked.Read(ref stream.LastPts100ns);
        
        if (last <= 0)
        {
            Interlocked.Exchange(ref stream.LastPts100ns, pts100ns);
            return fallback;
        }

        long delta = pts100ns - last;
        if (delta <= 0 || delta > 5_000_000)
        {
            Interlocked.Exchange(ref stream.LastPts100ns, pts100ns);
            return fallback;
        }

        long step = 90000L * delta / 10_000_000L;
        Interlocked.Exchange(ref stream.LastPts100ns, pts100ns);
        return (uint)Math.Max(1, step);
    }

    public void Stop()
    {
        if (!_running && _pc == null) return;
        Console.WriteLine("[MultiTrack] Stopping...");
        
        _running = false;
        
        lock (_lock)
        {
            foreach (var stream in _streams)
                stream.Dispose();
            _streams.Clear();
        }
        
        _pc?.close();
        _pc = null;
        
        Console.WriteLine("[MultiTrack] Stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Console.WriteLine("[MultiTrack] Disposed");
    }

    #region SDP Helpers
    
    private static (int? pt, string? fmtp) TryGetH264FromOfferSdp(string offerSdp)
    {
        if (string.IsNullOrWhiteSpace(offerSdp)) return (null, null);

        var lines = offerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
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
    
    #endregion
}
