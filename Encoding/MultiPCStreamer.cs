#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Multi-PC WebRTC streamer: Creates N separate PeerConnections (one per monitor).
/// This solves the SIPSorcery limitation where SendVideo() broadcasts to all tracks.
/// 
/// Protocol (multiplexed over single WebSocket):
/// - Client: "offer:0:&lt;sdp&gt;" for monitor 0, "offer:1:&lt;sdp&gt;" for monitor 1, etc.
/// - Server: "answer:0:&lt;sdp&gt;", "answer:1:&lt;sdp&gt;", etc.
/// - ICE: "candidate:0:&lt;candidate&gt;", "candidate:1:&lt;candidate&gt;", etc.
/// </summary>
public class MultiPCStreamer : IDisposable
{
    private readonly int _fps;
    private readonly int _kbps;
    private readonly int _monitorCount;
    private ID3D11Device? _device;
    
    private readonly List<MonitorPC> _monitors = new();
    private readonly object _lock = new();
    
    private volatile bool _running;
    private volatile bool _disposed;
    
    public bool IsRunning => _running;
    
    public event Action? OnAllConnected;
    public event Action<int, string>? OnIceCandidate; // monitorIndex, candidate
    public event Action<int>? OnPeerDisconnected; // monitorIndex

    private class MonitorPC : IDisposable
    {
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public RTCPeerConnection? PC { get; set; }
        public AmfNativeWrapper? Encoder { get; set; }
        public ID3D11Texture2D? StagingNV12 { get; set; }
        public bool IceConnected { get; set; }
        public long SentCount;
        public long SkipCount;
        public long LastPts100ns;
        
        public void Dispose()
        {
            try { Encoder?.Dispose(); } catch { }
            try { StagingNV12?.Dispose(); } catch { }
            try { PC?.close(); } catch { }
        }
    }

    public MultiPCStreamer(int monitorCount, int fps, int kbps, ID3D11Device? device = null)
    {
        _monitorCount = monitorCount;
        _fps = fps;
        _kbps = kbps;
        _device = device;
        _sendLocks = new object[monitorCount];
        for (int i = 0; i < monitorCount; i++)
            _sendLocks[i] = new object();
        
        Console.WriteLine($"[MultiPC] Created: {monitorCount} monitors, {fps}fps, {kbps}kbps per stream");
    }

    public void SetDevice(ID3D11Device device) => _device = device;

    /// <summary>
    /// Process offer for a specific monitor and return answer
    /// </summary>
    public async Task<string> ProcessOfferAsync(int monitorIndex, string offerSdp, int width, int height)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitorCount)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex));
        
        Console.WriteLine($"[MultiPC] Processing offer for monitor {monitorIndex}: {width}x{height}");
        
        MonitorPC monitor;
        lock (_lock)
        {
            // Find or create monitor entry
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex)!;
            if (monitor == null)
            {
                monitor = new MonitorPC { Index = monitorIndex, Width = width, Height = height };
                _monitors.Add(monitor);
            }
            else
            {
                // Cleanup existing PC if reconnecting
                monitor.PC?.close();
                monitor.Width = width;
                monitor.Height = height;
            }
        }

        // Create PeerConnection
        var cfg = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
        var pc = new RTCPeerConnection(cfg);
        monitor.PC = pc;

        // Parse H264 from offer
        var (h264Pt, h264Fmtp) = TryGetH264FromOffer(offerSdp);
        
        // Create video track
        var h264Format = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            h264Pt ?? 96,
            "H264",
            90000,
            0,
            string.IsNullOrWhiteSpace(h264Fmtp)
                ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f"
                : h264Fmtp);
        
        var track = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            false,
            new List<SDPAudioVideoMediaFormat> { h264Format },
            MediaStreamStatusEnum.SendOnly);
        
        pc.addTrack(track);

        // ICE candidate forwarding
        pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                OnIceCandidate?.Invoke(monitorIndex, cand.candidate);
            }
        };

        pc.oniceconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[MultiPC] Monitor {monitorIndex} ICE connection state changed to: {state}");
            if (state == RTCIceConnectionState.connected)
            {
                monitor.IceConnected = true;
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} ICE CONNECTED - initializing encoder");
                InitializeEncoder(monitor);
                CheckAllConnected();
            }
            else if (state == RTCIceConnectionState.checking)
            {
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} ICE is checking candidates...");
            }
            else if (state == RTCIceConnectionState.disconnected)
            {
                monitor.IceConnected = false;
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} ICE disconnected");
                OnPeerDisconnected?.Invoke(monitorIndex);
            }
            else if (state == RTCIceConnectionState.failed)
            {
                monitor.IceConnected = false;
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} ICE connection FAILED!");
                OnPeerDisconnected?.Invoke(monitorIndex);
            }
            else if (state == RTCIceConnectionState.closed)
            {
                monitor.IceConnected = false;
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} ICE connection closed");
                OnPeerDisconnected?.Invoke(monitorIndex);
            }
        };

        pc.onconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[MultiPC] Monitor {monitorIndex} Peer connection state changed to: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} Peer connection fully established");
            }
            else if (state == RTCPeerConnectionState.disconnected)
            {
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} Peer connection disconnected");
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} Peer connection FAILED");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} Peer connection closed");
            }
        };

        // Set remote offer and create answer
        pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        _running = true;
        
        var answerSdp = (answer.sdp ?? "").Replace("UDP/TLS/RTP/SAVP", "UDP/TLS/RTP/SAVPF");
        
        // CRITICAL: Ensure SDP has proper payload type info (fixes video not playing)
        var chosenPt = h264Pt ?? 96;
        answerSdp = EnsureVideoMLineHasPayload(answerSdp, chosenPt, h264Fmtp);
        answerSdp = FilterIceCandidates(answerSdp);
        
        // Log the final m=video line for debugging
        try
        {
            var mLine = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(mLine))
                Console.WriteLine($"[MultiPC] Monitor {monitorIndex} Answer m-line: {mLine}");
        }
        catch { }
        
        Console.WriteLine($"[MultiPC] Monitor {monitorIndex} answer created");
        return answerSdp;
    }

    /// <summary>
    /// Add ICE candidate for a specific monitor
    /// </summary>
    public void AddIceCandidate(int monitorIndex, string candidate)
    {
        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }
        
        if (monitor?.PC == null) return;
        
        var candStr = candidate.Trim();
        if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
            candStr = candStr.Substring(2);
        if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
            candStr = "candidate:" + candStr;
        
        try
        {
            monitor.PC.addIceCandidate(new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = "0" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiPC] Monitor {monitorIndex} AddICE error: {ex.Message}");
        }
    }

    private void InitializeEncoder(MonitorPC monitor)
    {
        if (_device == null || monitor.Encoder != null) return;
        
        lock (_lock)
        {
            if (monitor.Encoder != null) return;
            
            try
            {
                var encoder = new AmfNativeWrapper();
                encoder.OnEncodedData += (nalData, isKeyframe, pts) => 
                    OnEncodedData(monitor, nalData, isKeyframe, pts);
                
                if (encoder.Initialize(monitor.Width, monitor.Height, _fps, _kbps, _device))
                {
                    monitor.Encoder = encoder;
                    Console.WriteLine($"[MultiPC] Encoder {monitor.Index} ready: {monitor.Width}x{monitor.Height}");
                }
                else
                {
                    encoder.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MultiPC] Encoder {monitor.Index} init error: {ex.Message}");
            }
        }
    }

    private void CheckAllConnected()
    {
        lock (_lock)
        {
            if (_monitors.Count >= _monitorCount && _monitors.All(m => m.IceConnected))
            {
                Console.WriteLine($"[MultiPC] All {_monitorCount} monitors connected!");
                OnAllConnected?.Invoke();
            }
        }
    }

    /// <summary>
    /// Push NV12 texture for a specific monitor - ZERO COPY
    /// </summary>
    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed) return;
        
        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }
        
        if (monitor == null) return;
        
        // Check if encoder is ready
        if (monitor.Encoder == null || !monitor.IceConnected)
        {
            // Log occasionally to help debug
            var skipCount = Interlocked.Increment(ref monitor.SkipCount);
            if (skipCount <= 3 || skipCount % 100 == 0)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} skip #{skipCount}: encoder={monitor.Encoder != null}, ice={monitor.IceConnected}");
            }
            return;
        }
        
        try
        {
            // Use encoder dimensions (from client request), not capture dimensions
            int encWidth = monitor.Width;
            int encHeight = monitor.Height;
            
            // Create staging texture if needed (use encoder dimensions)
            if (monitor.StagingNV12 == null && _device != null)
            {
                monitor.StagingNV12 = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)encWidth,
                    Height = (uint)encHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.NV12,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.None
                });
                Console.WriteLine($"[MultiPC] m{monitorIndex} staging created: {encWidth}x{encHeight}");
            }
            
            if (monitor.StagingNV12 != null)
            {
                _device!.ImmediateContext.CopyResource(monitor.StagingNV12, nv12Texture);
                long sent = Interlocked.Read(ref monitor.SentCount);
                // Force IDR for first frame AND every 30 frames (1 second at 30fps) for debugging
                bool forceIdr = sent == 0 || (sent % 30 == 0);
                monitor.Encoder.EncodeTexture(monitor.StagingNV12, forceKeyframe: forceIdr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiPC] PushTexture m{monitorIndex} error: {ex.Message}");
        }
    }

    // Per-monitor locks for SendVideo - avoid global lock blocking all streams
    private readonly object[] _sendLocks;
    
    // Captured SPS/PPS from AMF encoder (per monitor) - will be extracted from first keyframe
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _capturedSPS = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _capturedPPS = new();
    
    private void OnEncodedData(MonitorPC monitor, byte[] nalData, bool isKeyframe, long pts)
    {
        if (!_running || monitor.PC == null || !monitor.IceConnected) return;
        if (monitor.PC.connectionState != RTCPeerConnectionState.connected) return;
        
        try
        {
            byte[] au = nalData;
            bool wasAvcc = !HasAnnexBStartCode(au);
            if (wasAvcc)
                au = ConvertAvccToAnnexB(au);
            
            au = StripAud(au);
            
            // Extract and capture SPS/PPS from encoder output
            var nalUnits = ParseNalUnits(au);
            bool hasSps = false, hasPps = false;
            
            foreach (var (type, data) in nalUnits)
            {
                if (type == 7 && !_capturedSPS.ContainsKey(monitor.Index))
                {
                    _capturedSPS[monitor.Index] = data;
                    Console.WriteLine($"[MultiPC] m{monitor.Index} Captured SPS: {data.Length} bytes");
                }
                else if (type == 8 && !_capturedPPS.ContainsKey(monitor.Index))
                {
                    _capturedPPS[monitor.Index] = data;
                    Console.WriteLine($"[MultiPC] m{monitor.Index} Captured PPS: {data.Length} bytes");
                }
                
                if (type == 7) hasSps = true;
                if (type == 8) hasPps = true;
            }
            
            // If keyframe is missing SPS/PPS, prepend captured ones
            // Don't use fallback - wait for encoder to output real SPS/PPS
            bool injected = false;
            if (isKeyframe && (!hasSps || !hasPps))
            {
                // Try captured SPS/PPS first - must match current resolution
                if (_capturedSPS.TryGetValue(monitor.Index, out var sps) && 
                    _capturedPPS.TryGetValue(monitor.Index, out var pps) &&
                    sps.Length > 10 && pps.Length > 3) // Valid SPS/PPS
                {
                    var withSpsPps = new byte[sps.Length + pps.Length + au.Length];
                    Buffer.BlockCopy(sps, 0, withSpsPps, 0, sps.Length);
                    Buffer.BlockCopy(pps, 0, withSpsPps, sps.Length, pps.Length);
                    Buffer.BlockCopy(au, 0, withSpsPps, sps.Length + pps.Length, au.Length);
                    au = withSpsPps;
                    injected = true;
                }
                else
                {
                    // No captured SPS/PPS yet - skip frame, wait for encoder to output SPS/PPS
                    long skipCount = Interlocked.Read(ref monitor.SentCount);
                    if (skipCount < 5)
                        Console.WriteLine($"[MultiPC] m{monitor.Index} Waiting for encoder SPS/PPS (resolution may have changed)");
                    return;
                }
            }

            uint rtpStep = CalcRtpStep(monitor, pts);
            
            // Use per-monitor lock to avoid blocking other streams
            lock (_sendLocks[monitor.Index])
            {
                monitor.PC.SendVideo(rtpStep, au);
            }
            
            long sent = Interlocked.Increment(ref monitor.SentCount);
            if (sent <= 5 || isKeyframe || sent % 300 == 0)
            {
                string nalInfo = GetNalUnitInfo(au);
                string spsPpsInfo = isKeyframe ? $" (injected={injected})" : "";
                Console.WriteLine($"[MultiPC] m{monitor.Index} #{sent}: {au.Length}B key={isKeyframe} rtpStep={rtpStep}{spsPpsInfo} {nalInfo}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiPC] m{monitor.Index} send error: {ex.Message}");
        }
    }
    
    // Parse NAL units from Annex-B stream - returns list of (nalType, fullNalWithStartCode)
    private static List<(int type, byte[] data)> ParseNalUnits(byte[] au)
    {
        var result = new List<(int, byte[])>();
        var starts = new List<(int pos, int headerLen)>();
        
        // Find all start codes (00 00 01 or 00 00 00 01)
        for (int i = 0; i < au.Length - 3; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0)
            {
                if (au[i + 2] == 1)
                {
                    starts.Add((i, 3));
                    i += 2; // Skip past start code
                }
                else if (i + 3 < au.Length && au[i + 2] == 0 && au[i + 3] == 1)
                {
                    starts.Add((i, 4));
                    i += 3; // Skip past start code
                }
            }
        }
        
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].pos;
            int headerLen = starts[i].headerLen;
            int end = (i + 1 < starts.Count) ? starts[i + 1].pos : au.Length;
            int nalTypePos = start + headerLen;
            
            if (nalTypePos < au.Length && end > start)
            {
                int nalType = au[nalTypePos] & 0x1F;
                int dataLen = end - start;
                if (dataLen > 0)
                {
                    byte[] nalData = new byte[dataLen];
                    Buffer.BlockCopy(au, start, nalData, 0, dataLen);
                    result.Add((nalType, nalData));
                }
            }
        }
        
        return result;
    }
    
    private static List<int> GetNalTypes(byte[] au)
    {
        var types = new List<int>();
        int pos = 0;
        while (pos < au.Length - 4)
        {
            if (au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 0 && au[pos + 3] == 1)
            {
                pos += 4;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else if (pos + 2 < au.Length && au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 1)
            {
                pos += 3;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else
            {
                pos++;
            }
        }
        return types;
    }
    
    private static string GetNalUnitInfo(byte[] au)
    {
        var types = new List<int>();
        int pos = 0;
        while (pos < au.Length - 4)
        {
            // Find start code
            if (au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 0 && au[pos + 3] == 1)
            {
                pos += 4;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else if (au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 1)
            {
                pos += 3;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else
            {
                pos++;
            }
        }
        return types.Count > 0 ? $"NAL=[{string.Join(",", types)}]" : "NAL=[]";
    }

    private uint CalcRtpStep(MonitorPC monitor, long pts100ns)
    {
        uint fallback = (uint)(90000 / _fps);
        long last = Interlocked.Read(ref monitor.LastPts100ns);
        
        if (last <= 0)
        {
            Interlocked.Exchange(ref monitor.LastPts100ns, pts100ns);
            return fallback;
        }

        long delta = pts100ns - last;
        if (delta <= 0 || delta > 5_000_000)
        {
            Interlocked.Exchange(ref monitor.LastPts100ns, pts100ns);
            return fallback;
        }

        Interlocked.Exchange(ref monitor.LastPts100ns, pts100ns);
        return (uint)Math.Max(1, 90000L * delta / 10_000_000L);
    }

    public void Stop()
    {
        _running = false;
        lock (_lock)
        {
            foreach (var m in _monitors) m.Dispose();
            _monitors.Clear();
        }
        Console.WriteLine("[MultiPC] Stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    #region Helpers
    
    private static (int?, string?) TryGetH264FromOffer(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return (null, null);
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            if (!line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("a=rtpmap:".Length);
            var sp = rest.IndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(rest.Substring(0, sp), out var pt)) continue;
            if (rest.Substring(sp + 1).IndexOf("H264/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Find fmtp
                var fmtpPrefix = $"a=fmtp:{pt} ";
                var fmtp = lines.FirstOrDefault(l => l.StartsWith(fmtpPrefix, StringComparison.OrdinalIgnoreCase));
                return (pt, fmtp?.Substring(fmtpPrefix.Length).Trim());
            }
        }
        return (null, null);
    }

    private static string FilterIceCandidates(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return sdp;
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var kept = lines.Where(l => 
            !l.TrimStart().StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
            !l.TrimStart().StartsWith("a=end-of-candidates", StringComparison.OrdinalIgnoreCase));
        return string.Join("\r\n", kept);
    }

    private static bool HasAnnexBStartCode(byte[] d) =>
        (d.Length >= 4 && d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 1) ||
        (d.Length >= 3 && d[0] == 0 && d[1] == 0 && d[2] == 1);

    private static byte[] ConvertAvccToAnnexB(byte[] avcc)
    {
        if (avcc.Length < 8) return avcc;
        try
        {
            var outBuf = new byte[avcc.Length + 32];
            int outPos = 0, pos = 0;
            while (pos + 4 <= avcc.Length)
            {
                int nalLen = (avcc[pos] << 24) | (avcc[pos + 1] << 16) | (avcc[pos + 2] << 8) | avcc[pos + 3];
                pos += 4;
                if (nalLen <= 0 || pos + nalLen > avcc.Length) return avcc;
                if (outPos + 4 + nalLen > outBuf.Length)
                    Array.Resize(ref outBuf, Math.Max(outBuf.Length * 2, outPos + 4 + nalLen));
                outBuf[outPos++] = 0; outBuf[outPos++] = 0; outBuf[outPos++] = 0; outBuf[outPos++] = 1;
                Buffer.BlockCopy(avcc, pos, outBuf, outPos, nalLen);
                outPos += nalLen; pos += nalLen;
            }
            if (outPos <= 0) return avcc;
            var res = new byte[outPos];
            Buffer.BlockCopy(outBuf, 0, res, 0, outPos);
            return res;
        }
        catch { return avcc; }
    }

    private static byte[] StripAud(byte[] au)
    {
        if (au.Length < 4) return au;
        int pos = (au.Length >= 4 && au[0] == 0 && au[1] == 0 && au[2] == 0 && au[3] == 1) ? 4 :
                  (au.Length >= 3 && au[0] == 0 && au[1] == 0 && au[2] == 1) ? 3 : 0;
        if (pos == 0 || pos >= au.Length) return au;
        if ((au[pos] & 0x1F) != 9) return au;
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

    /// <summary>
    /// Ensures SDP answer has proper m=video line with payload type.
    /// SIPSorcery can generate incomplete SDP that breaks video playback.
    /// </summary>
    private static string EnsureVideoMLineHasPayload(string answerSdp, int pt, string? fmtp)
    {
        if (string.IsNullOrWhiteSpace(answerSdp)) return answerSdp;
        
        var lines = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line == null) continue;
            if (!line.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase)) continue;
            
            // m=video <port> <proto> <fmt> ...
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            // Check if payload type is missing or incomplete
            if (parts.Length <= 3)
            {
                // Add payload type to m-line
                lines[i] = line.TrimEnd() + " " + pt;
                
                // Check for rtpmap and fmtp
                bool hasRtpmap = false;
                bool hasFmtp = false;
                
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var l = lines[j];
                    if (l.StartsWith("m=", StringComparison.OrdinalIgnoreCase)) break;
                    if (l.StartsWith($"a=rtpmap:{pt}", StringComparison.OrdinalIgnoreCase)) hasRtpmap = true;
                    if (l.StartsWith($"a=fmtp:{pt}", StringComparison.OrdinalIgnoreCase)) hasFmtp = true;
                }
                
                // Insert rtpmap and fmtp if missing
                int insertAt = i + 1;
                if (!hasRtpmap)
                {
                    lines.Insert(insertAt++, $"a=rtpmap:{pt} H264/90000");
                }
                if (!hasFmtp)
                {
                    var fmtpVal = string.IsNullOrWhiteSpace(fmtp) 
                        ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f" 
                        : fmtp;
                    lines.Insert(insertAt, $"a=fmtp:{pt} {fmtpVal}");
                }
            }
            break;
        }
        
        return string.Join("\r\n", lines);
    }
    
    #endregion
}
