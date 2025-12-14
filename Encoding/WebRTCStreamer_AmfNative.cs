#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// WebRTC streamer using native AmfWrapper.dll for true zero-copy encoding on AMD GPUs.
/// This provides the lowest latency path by encoding D3D11 textures directly.
/// </summary>
public class WebRTCStreamer_AmfNative : IDisposable
{
    private readonly int _fps;
    private readonly int _kbps;
    private ID3D11Device? _device;
    
    private AmfNativeWrapper? _encoder;
    private RTCPeerConnection? _pc;
    private MediaStreamTrack? _videoTrack;
    
    private readonly ConcurrentQueue<(byte[] nv12, int w, int h)> _nv12Queue = new();
    
    private CancellationTokenSource? _cts;
    private Task? _encodeTask;
    
    private bool _running;
    private bool _disposed;
    private long _enqueueCount;
    private long _sentCount;
    private int _width;
    private int _height;

    // AMF output PTS (100ns) -> RTP timestamp step (90kHz).
    private long _lastPts100ns;
    
    // Staging texture for zero-copy path to avoid race condition
    private ID3D11Texture2D? _stagingNV12;
    private ID3D11DeviceContext? _deviceContext;
    
    private readonly object _lock = new();

    public bool IsRunning => _running;
    public bool UseNV12Input => true;
    private volatile bool _iceConnected = false;
    
    public event Action? OnPeerDisconnected;
    public event Action<string>? OnIceCandidate;

    public WebRTCStreamer_AmfNative(int fps, int kbps, ID3D11Device? device = null)
    {
        _fps = fps;
        _kbps = kbps;
        _device = device;
        _lastPts100ns = 0;
        Console.WriteLine($"[RTC-AmfNative] Created: {fps}fps, {kbps}kbps");
    }

    private static bool ContainsAnnexBStartCode(byte[] data)
    {
        // For our pipeline we only treat it as AnnexB if it starts with a start code.
        // Scanning the whole buffer can give false positives when the bitstream contains 00 00 00 01 by chance.
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) return true;
        if (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) return true;
        return false;
    }

    private static byte[] TryConvertAvccToAnnexB(byte[] avcc)
    {
        // AVCC format is 4-byte big-endian length prefixes followed by NAL payload.
        // If parsing fails, return original.
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
                    int newLen = Math.Max(outBuf.Length * 2, needed);
                    var newBuf = new byte[newLen];
                    Buffer.BlockCopy(outBuf, 0, newBuf, 0, outPos);
                    outBuf = newBuf;
                }

                // start code
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

    // Strip a leading AUD NAL (type 9) if present right at the start of an AnnexB AU.
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

        // Find the next start code.
        for (int i = pos + 1; i + 3 < au.Length; i++)
        {
            if ((au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ||
                (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1))
            {
                int cut = i;
                var trimmed = new byte[au.Length - cut];
                Buffer.BlockCopy(au, cut, trimmed, 0, trimmed.Length);
                return trimmed;
            }
        }

        // Only AUD found.
        return au;
    }

    private uint GetRtpStepFromPts(long pts100ns)
    {
        // Default step from fps.
        uint fallback = (uint)Math.Max(1, 90000 / Math.Max(1, _fps));

        long last = Interlocked.Read(ref _lastPts100ns);
        if (last <= 0)
        {
            Interlocked.Exchange(ref _lastPts100ns, pts100ns);
            return fallback;
        }

        long delta = pts100ns - last;
        if (delta <= 0 || delta > 5_000_000) // >0.5s is suspicious for a 30fps stream
        {
            Interlocked.Exchange(ref _lastPts100ns, pts100ns);
            return fallback;
        }

        // 10,000,000 100ns ticks per second.
        long step = 90000L * delta / 10_000_000L;
        Interlocked.Exchange(ref _lastPts100ns, pts100ns);
        return (uint)Math.Max(1, step);
    }

    public void SetDevice(ID3D11Device device)
    {
        _device = device;
    }

    public async Task StartAsync()
    {
        Console.WriteLine("[RTC-AmfNative] StartAsync called");
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Stop();
        await Task.CompletedTask;
    }

    public void AddIceCandidate(string candidate)
    {
        if (_pc == null) return;
        try
        {
            var candStr = (candidate ?? string.Empty).Trim();
            if (candStr.Length == 0) return;

            // Normalize formats we may receive via signaling.
            // - Some clients send "a=candidate:..." (SDP attribute form)
            // - Some paths can accidentally double-prefix "candidate:candidate:..."
            if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring(2);
            if (candStr.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring("candidate:".Length);
            if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = "candidate:" + candStr;

            var init = new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = "0" };
            Console.WriteLine($"[RTC-AmfNative] 📥 RECEIVED CANDIDATE: '{candStr.Substring(0, Math.Min(120, candStr.Length))}...' (len={candStr.Length})");
            Console.WriteLine($"[RTC-AmfNative] 📥 CANDIDATE HEX: {BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(candStr).Take(50).ToArray())}");
            _pc.addIceCandidate(init);
            Console.WriteLine($"[RTC-AmfNative] Added remote ICE: {candStr.Substring(0, Math.Min(70, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RTC-AmfNative] AddIceCandidate error: {ex.Message}");
        }
        
        // Log current ICE state after adding candidate
        if (_pc != null)
        {
            Console.WriteLine($"[RTC-AmfNative] After add ICE: iceState={_pc.iceConnectionState}, pcState={_pc.connectionState}");
        }
    }

    public RTCIceConnectionState IceConnectionState => _pc?.iceConnectionState ?? RTCIceConnectionState.@new;

    private sealed record H264Offer(int Pt, string? Fmtp, byte? ProfileIdc, byte? Constraints, byte? LevelIdc);

    private static bool TryParseProfileLevelId(string? fmtp, out byte profileIdc, out byte constraints, out byte levelIdc)
    {
        profileIdc = 0;
        constraints = 0;
        levelIdc = 0;

        if (string.IsNullOrWhiteSpace(fmtp)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(fmtp, "profile-level-id=([0-9A-Fa-f]{6})");
        if (!m.Success) return false;
        var hex = m.Groups[1].Value;
        profileIdc = Convert.ToByte(hex.Substring(0, 2), 16);
        constraints = Convert.ToByte(hex.Substring(2, 2), 16);
        levelIdc = Convert.ToByte(hex.Substring(4, 2), 16);
        return true;
    }

    private static (int? pt, string? fmtp) TryGetH264FromOfferSdp(string offerSdp)
    {
        if (string.IsNullOrWhiteSpace(offerSdp)) return (null, null);

        var lines = offerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var h264Pts = new HashSet<int>();

        foreach (var line in lines)
        {
            // a=rtpmap:<pt> H264/90000
            if (!line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("a=rtpmap:".Length);
            var sp = rest.IndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(rest.Substring(0, sp), out var candPt)) continue;
            var codec = rest.Substring(sp + 1);
            if (codec.IndexOf("H264/", StringComparison.OrdinalIgnoreCase) < 0) continue;
            h264Pts.Add(candPt);
        }

        if (h264Pts.Count == 0) return (null, null);

        var offers = new List<H264Offer>();
        foreach (var candPt in h264Pts)
        {
            string? fmtp = null;
            var needle = "a=fmtp:" + candPt + " ";
            foreach (var line in lines)
            {
                if (!line.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) continue;
                fmtp = line.Substring(needle.Length).Trim();
                break;
            }

            byte? profile = null;
            byte? constraints = null;
            byte? level = null;
            if (TryParseProfileLevelId(fmtp, out var p, out var c, out var l))
            {
                profile = p;
                constraints = c;
                level = l;
            }

            offers.Add(new H264Offer(candPt, fmtp, profile, constraints, level));
        }

        // Prefer higher level first (bigger resolution support), then profile, then LOWEST constraints (least restrictive).
        var best = offers
            .OrderByDescending(o => o.LevelIdc ?? (byte)0)
            .ThenByDescending(o => o.ProfileIdc ?? (byte)0)
            .ThenBy(o => o.Constraints ?? (byte)0)
            .First();

        try
        {
            var desc = string.Join(", ", offers
                .OrderByDescending(o => o.LevelIdc ?? (byte)0)
                .Select(o =>
                {
                    string pli = (o.ProfileIdc.HasValue && o.Constraints.HasValue && o.LevelIdc.HasValue)
                        ? $"{o.ProfileIdc.Value:X2}{o.Constraints.Value:X2}{o.LevelIdc.Value:X2}"
                        : "(no profile-level-id)";
                    return $"pt={o.Pt} pli={pli}";
                }));
            Console.WriteLine($"[RTC-AmfNative] Offer H264 payloads: {desc}");
        }
        catch { }

        return (best.Pt, best.Fmtp);
    }

    private static string EnsureVideoMLineHasPayload(string answerSdp, int pt, string? fmtp)
    {
        if (string.IsNullOrWhiteSpace(answerSdp)) return answerSdp;

        // Normalize newlines.
        var lines = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line == null) continue;
            if (!line.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase)) continue;

            // m=video <port> <proto> <fmt> ...
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 3)
            {
                lines[i] = line.TrimEnd() + " " + pt;

                bool hasRtpmap = false;
                bool hasFmtp = false;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var l = lines[j];
                    if (l.StartsWith("m=", StringComparison.OrdinalIgnoreCase)) break;
                    if (l.StartsWith($"a=rtpmap:{pt}", StringComparison.OrdinalIgnoreCase)) hasRtpmap = true;
                    if (l.StartsWith($"a=fmtp:{pt}", StringComparison.OrdinalIgnoreCase)) hasFmtp = true;
                }

                // Insert minimally required rtpmap/fmtp if missing.
                int insertAt = i + 1;
                if (!hasRtpmap)
                {
                    lines.Insert(insertAt++, $"a=rtpmap:{pt} H264/90000");
                }
                if (!string.IsNullOrWhiteSpace(fmtp) && !hasFmtp)
                {
                    lines.Insert(insertAt++, $"a=fmtp:{pt} {fmtp}");
                }
            }

            break; // only one video section expected
        }

        return string.Join("\r\n", lines);
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

        int dropped = 0;
        foreach (var raw in lines)
        {
            var l = raw ?? string.Empty;
            var t = l.TrimStart();

            if (t.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                if (!CandidateLineIsHostPrivateV4(t))
                {
                    dropped++;
                    continue;
                }
            }
            else if (t.StartsWith("a=end-of-candidates", StringComparison.OrdinalIgnoreCase))
            {
                // We'll rely on trickle ICE signalling instead.
                dropped++;
                continue;
            }

            kept.Add(l);
        }

        if (dropped > 0)
            Console.WriteLine($"[RTC-AmfNative] SDP: dropped {dropped} ICE candidate lines (keeping host private IPv4 only)");

        return string.Join("\r\n", kept);
    }

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        if (_running) throw new InvalidOperationException("Already running");
        
        Console.WriteLine("[RTC-AmfNative] Processing offer");
        
        var cfg = new RTCConfiguration
        {
            // Host-only by default for LAN stability.
            iceServers = new List<RTCIceServer>()
        };
        
        _pc = new RTCPeerConnection(cfg);
        Console.WriteLine("[RTC-AmfNative] PeerConnection created");

        // Create H264 video track (must match a payload type from the offer for Unity)
        var (h264Pt, h264Fmtp) = TryGetH264FromOfferSdp(offerSdp);
        if (h264Pt.HasValue)
            Console.WriteLine($"[RTC-AmfNative] Offer H264 payload type detected: pt={h264Pt.Value}");
        else
            Console.WriteLine("[RTC-AmfNative] Offer H264 payload type not detected (will use fallback)");
        if (!string.IsNullOrWhiteSpace(h264Fmtp))
            Console.WriteLine($"[RTC-AmfNative] Offer H264 fmtp: {h264Fmtp}");

        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: h264Pt ?? 127,
            name: "H264",
            clockRate: 90000,
            channels: 0,
            fmtp: string.IsNullOrWhiteSpace(h264Fmtp)
                ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f"
                : h264Fmtp);
        
        _videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { h264 },
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(_videoTrack);
        
        Console.WriteLine($"[RTC-AmfNative] canSend(H264) = {_pc.VideoLocalTrack != null}");

        // Forward local ICE candidates to client
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Console.WriteLine($"[RTC-AmfNative] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                Console.WriteLine("[RTC-AmfNative] ICE gathering complete");
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        _pc.oniceconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[RTC-AmfNative] ice = {state}");
            if (state == RTCIceConnectionState.connected)
            {
                _iceConnected = true;
                Console.WriteLine("[RTC-AmfNative] ICE CONNECTED");
            }
            else if (state == RTCIceConnectionState.disconnected || state == RTCIceConnectionState.failed)
            {
                _iceConnected = false;
                OnPeerDisconnected?.Invoke();
            }
        };
        
        _pc.onconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[RTC-AmfNative] pc.state = {state}");
        };

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        _running = true;
        _cts = new CancellationTokenSource();
        _encodeTask = Task.Run(() => EncodeLoop(_cts.Token));
        
        // Stats logging task for debugging
        _ = Task.Run(async () =>
        {
            while (_running && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000);
                Console.WriteLine($"[RTC-AmfNative] stats: enq={_enqueueCount} sent={_sentCount}");
            }
        });

        // Unity (and our web test) expect SAVPF. Some SIPSorcery answers default to SAVP.
        var answerSdp = (answer.sdp ?? "").Replace("UDP/TLS/RTP/SAVP", "UDP/TLS/RTP/SAVPF");

        // If SIPSorcery generated an invalid m=video line with no payload types, fix it.
        // This case breaks Unity's SetRemoteDescription (it can hang).
        var chosenPt = h264Pt ?? 127;
        answerSdp = EnsureVideoMLineHasPayload(answerSdp, chosenPt, h264Fmtp);

        // IMPORTANT: Filter candidates embedded in the SDP.
        // Even though we trickle candidates via onicecandidate, SIPSorcery can include host candidates in the SDP.
        // If IPv6 candidates leak into the SDP, Chrome can select an IPv6 pair and then receive no media when we later filter/trickle only IPv4.
        answerSdp = FilterAnswerSdpIceCandidates(answerSdp);

        // Log the final m=video line for debugging.
        try
        {
            var mLine = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(mLine))
                Console.WriteLine($"[RTC-AmfNative] Answer m-line: {mLine}");
        }
        catch { }

        return answerSdp;
    }

    public async Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        Console.WriteLine("[RTC-AmfNative] BGRA input not supported - use NV12 or texture");
        await Task.CompletedTask;
    }

    public async Task PushNV12BytesAsync(byte[] src, int width, int height)
    {
        if (!_running || _disposed) return;
        
        _width = width;
        _height = height;
        
        byte[] copy = new byte[src.Length];
        Buffer.BlockCopy(src, 0, copy, 0, src.Length);
        
        _nv12Queue.Enqueue((copy, width, height));
        Interlocked.Increment(ref _enqueueCount);
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// TRUE ZERO-COPY: Encode directly from NV12 GPU texture
    /// Uses staging texture copy to avoid race condition with capture pipeline
    /// </summary>
    public void PushTexture(ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed) return;
        
        lock (_lock)
        {
            if (_encoder == null)
            {
                InitializeEncoder(width, height);
            }
            
            if (_encoder != null && _device != null)
            {
                // Create staging texture on first use or if size changed
                if (_stagingNV12 == null || _width != width || _height != height)
                {
                    _stagingNV12?.Dispose();
                    _deviceContext = _device.ImmediateContext;
                    
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
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    };
                    _stagingNV12 = _device.CreateTexture2D(desc);
                    _width = width;
                    _height = height;
                    Console.WriteLine($"[RTC-AmfNative] Created staging NV12 texture {width}x{height}");
                }
                
                // Copy source texture to staging (prevents race condition)
                _deviceContext?.CopyResource(_stagingNV12, nv12Texture);
                
                // Encode from staging texture
                bool forceIdr = (Interlocked.Read(ref _sentCount) == 0);
                _encoder.EncodeTexture(_stagingNV12, forceKeyframe: forceIdr);
            }
        }
    }

    public void Stop()
    {
        if (!_running && _pc == null) return;
        Console.WriteLine("[RTC-AmfNative] Stopping...");
        
        _running = false;
        _cts?.Cancel();
        
        try { _encodeTask?.Wait(1000); } catch { }
        
        _pc?.close();
        _pc = null;
        
        _encoder?.Dispose();
        _encoder = null;
        
        _stagingNV12?.Dispose();
        _stagingNV12 = null;
        
        Console.WriteLine("[RTC-AmfNative] Stopped");
    }

    private void InitializeEncoder(int width, int height)
    {
        if (_encoder != null) return;
        if (_device == null)
        {
            Console.WriteLine("[RTC-AmfNative] No D3D11 device - cannot use zero-copy encoder");
            return;
        }
        
        _encoder = new AmfNativeWrapper();
        _encoder.OnEncodedData += OnEncodedData;
        
        if (!_encoder.Initialize(width, height, _fps, _kbps, _device))
        {
            Console.WriteLine("[RTC-AmfNative] Encoder initialization failed!");
            _encoder.Dispose();
            _encoder = null;
        }
        else
        {
            Console.WriteLine($"[RTC-AmfNative] Encoder started {width}x{height} @ {_fps}fps (zero-copy)");
        }
    }

    private void OnEncodedData(byte[] nalData, bool isKeyframe, long pts)
    {
        // Wait for both ICE connected AND PeerConnection connected before sending frames
        if (!_running || _pc == null || !_iceConnected || _pc.connectionState != RTCPeerConnectionState.connected) return;
        
        try
        {
            // Normalize AMF output into an AnnexB AU compatible with the packetiser.
            byte[] au = nalData;
            bool convertedFromAvcc = false;
            if (!ContainsAnnexBStartCode(au))
            {
                au = TryConvertAvccToAnnexB(au);
                convertedFromAvcc = !ReferenceEquals(au, nalData);
            }
            au = StripLeadingAud(au);

            uint rtpStep = GetRtpStepFromPts(pts);
            _pc.SendVideo(rtpStep, au);
            long sent = Interlocked.Increment(ref _sentCount);
            
            // Debug: log first 5 frames and keyframes
            if (sent <= 5 || isKeyframe)
            {
                Console.WriteLine($"[RTC-AmfNative] Frame #{sent}: {au.Length} bytes, keyframe={isKeyframe}, rtpStep={rtpStep}, avcc2annexb={convertedFromAvcc}");
            }
        }
        catch (Exception ex)
        {
            // Log only occasionally to avoid spam
            if (Interlocked.Read(ref _sentCount) % 60 == 0)
            {
                Console.WriteLine($"[RTC-AmfNative] SendVideo error: {ex.Message}");
            }
        }
    }

    private void EncodeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _running)
        {
            while (_nv12Queue.TryDequeue(out var item))
            {
                lock (_lock)
                {
                    if (_encoder == null)
                    {
                        InitializeEncoder(item.w, item.h);
                    }
                    
                    if (_encoder != null)
                    {
                        // Force IDR until we've successfully sent at least one frame.
                        // We may encode frames before ICE is connected; those get dropped in OnEncodedData,
                        // so forcing only the very first *encoded* frame can still result in a black screen.
                        bool forceIdr = (Interlocked.Read(ref _sentCount) == 0);
                        _encoder.EncodeNV12Bytes(item.nv12, forceKeyframe: forceIdr);
                    }
                }
            }
            
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Stop();
        Console.WriteLine("[RTC-AmfNative] Disposed");
    }
}

/// <summary>
/// Wrapper for WebRTCStreamer_AmfNative that implements IWebRTCStreamer
/// </summary>
public class WebRTCStreamerAmfNativeWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_AmfNative _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public bool UseNV12Input => _streamer.UseNV12Input;
    public bool UseTextureInput => true; // Native AMF supports TRUE zero-copy!
    
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public event Action<string>? OnIceCandidate
    {
        add => _streamer.OnIceCandidate += value;
        remove => _streamer.OnIceCandidate -= value;
    }

    public WebRTCStreamerAmfNativeWrapper(int fps, int kbps, ID3D11Device? device = null)
    {
        _streamer = new WebRTCStreamer_AmfNative(fps, kbps, device);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync() => _streamer.StopAsync();
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => _streamer.PushBgraBytesAsync(src, width, height, stride);
    public Task PushNV12BytesAsync(byte[] src, int width, int height)
        => _streamer.PushNV12BytesAsync(src, width, height);
    public void PushTexture(ID3D11Texture2D nv12Texture, int width, int height)
        => _streamer.PushTexture(nv12Texture, width, height);
    public void SetDevice(ID3D11Device device) => _streamer.SetDevice(device);
    public void AddIceCandidate(string candidate) => _streamer.AddIceCandidate(candidate);
    public void Dispose() => _streamer.Dispose();
}
