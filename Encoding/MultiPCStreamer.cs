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
        public long EncodeCount;
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
        
        Console.WriteLine($"[MultiPC] Created: {monitorCount}mon {fps}fps {kbps}kbps");
    }

    public void SetDevice(ID3D11Device device) => _device = device;

    /// <summary>
    /// Process offer for a specific monitor and return answer
    /// </summary>
    public async Task<string> ProcessOfferAsync(int monitorIndex, string offerSdp, int width, int height)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitorCount)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex));
        
        Console.WriteLine($"[MultiPC] m{monitorIndex} offer: {width}x{height}");
        
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
                // Check if resolution changed
                bool resolutionChanged = monitor.Width != width || monitor.Height != height;
                
                // Cleanup existing PC
                monitor.PC?.close();
                
                // If resolution changed, cleanup encoder and staging texture
                if (resolutionChanged)
                {
                    Console.WriteLine($"[MultiPC] m{monitorIndex} res changed: {monitor.Width}x{monitor.Height} -> {width}x{height}");
                    
                    // Dispose old encoder
                    try { monitor.Encoder?.Dispose(); } catch { }
                    monitor.Encoder = null;
                    
                    // Dispose old staging texture
                    try { monitor.StagingNV12?.Dispose(); } catch { }
                    monitor.StagingNV12 = null;
                    
                    // Clear captured SPS/PPS for this monitor (no longer valid)
                    _capturedSPS.TryRemove(monitorIndex, out _);
                    _capturedPPS.TryRemove(monitorIndex, out _);
                    
                    // Reset counters
                    Interlocked.Exchange(ref monitor.SentCount, 0);
                    Interlocked.Exchange(ref monitor.SkipCount, 0);
                    Interlocked.Exchange(ref monitor.LastPts100ns, 0);
                }
                
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
            if (state == RTCIceConnectionState.connected)
            {
                monitor.IceConnected = true;
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE connected");
                CheckAllConnected();
            }
            else if (state == RTCIceConnectionState.disconnected || 
                     state == RTCIceConnectionState.failed || 
                     state == RTCIceConnectionState.closed)
            {
                monitor.IceConnected = false;
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE {state}");
                OnPeerDisconnected?.Invoke(monitorIndex);
            }
        };

        pc.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.connected)
                Console.WriteLine($"[MultiPC] m{monitorIndex} PC connected");
            else if (state != RTCPeerConnectionState.connecting && state != RTCPeerConnectionState.@new)
                Console.WriteLine($"[MultiPC] m{monitorIndex} PC {state}");
        };

        // Set remote offer and create answer
        pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        _running = true;
        
        // Initialize encoder immediately (don't wait for ICE connected)
        // This ensures both encoders are ready at the same time
        InitializeEncoder(monitor);
        
        var answerSdp = (answer.sdp ?? "").Replace("UDP/TLS/RTP/SAVP", "UDP/TLS/RTP/SAVPF");
        
        // CRITICAL: Ensure SDP has proper payload type info (fixes video not playing)
        var chosenPt = h264Pt ?? 96;
        answerSdp = EnsureVideoMLineHasPayload(answerSdp, chosenPt, h264Fmtp);
        answerSdp = FilterIceCandidates(answerSdp);
        
        Console.WriteLine($"[MultiPC] m{monitorIndex} answer created");
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
        catch { }
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
                    Console.WriteLine($"[MultiPC] m{monitor.Index} encoder ready");
                }
                else
                {
                    encoder.Dispose();
                    Console.WriteLine($"[MultiPC] m{monitor.Index} encoder init failed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MultiPC] m{monitor.Index} encoder error: {ex.Message}");
            }
        }
    }

    private void CheckAllConnected()
    {
        lock (_lock)
        {
            if (_monitors.Count >= _monitorCount && _monitors.All(m => m.IceConnected))
                OnAllConnected?.Invoke();
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
            Interlocked.Increment(ref monitor.SkipCount);
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
            }
            
            if (monitor.StagingNV12 != null)
            {
                _device!.ImmediateContext.CopyResource(monitor.StagingNV12, nv12Texture);
                long sent = Interlocked.Read(ref monitor.SentCount);
                long skipped = Interlocked.Read(ref monitor.SkipCount);
                
                // Force IDR for:
                // - First 3 frames to ensure SPS/PPS capture
                // - Every 90 frames (3 seconds at 30fps) - reduced from 30 to fix stuttering
                // - When too many frames skipped waiting for SPS/PPS
                bool forceIdr = sent < 3 || (sent % 90 == 0) || 
                               (skipped > 0 && skipped <= 10); // Force keyframe when waiting for SPS/PPS
                
                monitor.Encoder.EncodeTexture(monitor.StagingNV12, forceKeyframe: forceIdr);
            }
        }
        catch { }
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
        
        // Debug: log first few callbacks per monitor
        long encCount = Interlocked.Increment(ref monitor.EncodeCount);
        if (encCount <= 5)
        {
            var debugNals = ParseNalUnits(nalData);
            var nalTypes = string.Join(",", debugNals.Select(n => n.type));
            Console.WriteLine($"[MultiPC] m{monitor.Index} enc#{encCount}: {nalData.Length}B key={isKeyframe} NALs=[{nalTypes}]");
        }
        
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
                    _capturedSPS[monitor.Index] = data;
                else if (type == 8 && !_capturedPPS.ContainsKey(monitor.Index))
                    _capturedPPS[monitor.Index] = data;
                
                if (type == 7) hasSps = true;
                if (type == 8) hasPps = true;
            }
            
            // If keyframe is missing SPS/PPS, prepend captured ones
            if (isKeyframe && (!hasSps || !hasPps))
            {
                // Try captured SPS/PPS first - must match current resolution
                if (_capturedSPS.TryGetValue(monitor.Index, out var sps) && 
                    _capturedPPS.TryGetValue(monitor.Index, out var pps) &&
                    sps.Length > 10 && pps.Length > 3)
                {
                    var withSpsPps = new byte[sps.Length + pps.Length + au.Length];
                    Buffer.BlockCopy(sps, 0, withSpsPps, 0, sps.Length);
                    Buffer.BlockCopy(pps, 0, withSpsPps, sps.Length, pps.Length);
                    Buffer.BlockCopy(au, 0, withSpsPps, sps.Length + pps.Length, au.Length);
                    au = withSpsPps;
                }
                else
                {
                    // No captured SPS/PPS yet - wait for encoder to output them
                    Interlocked.Increment(ref monitor.SkipCount);
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
            if (sent == 1)
                Console.WriteLine($"[MultiPC] m{monitor.Index} streaming started");
        }
        catch { }
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    #region Fallback SPS/PPS Generation
    
    /// <summary>
    /// Generate fallback SPS/PPS for common resolutions when encoder doesn't output them inline.
    /// SPS/PPS are resolution-specific, so we generate them dynamically.
    /// Profile: Constrained Baseline (42 00), Level: auto-calculated
    /// </summary>
    private static (byte[]? sps, byte[]? pps) GenerateFallbackSpsPps(int width, int height)
    {
        try
        {
            // Calculate level based on resolution (macroblocks per second)
            int mbWidth = (width + 15) / 16;
            int mbHeight = (height + 15) / 16;
            int totalMbs = mbWidth * mbHeight;
            
            // Level calculation based on total macroblocks
            byte level;
            if (totalMbs <= 99) level = 10;           // 176x144 (99 MBs) - Level 1.0
            else if (totalMbs <= 396) level = 13;     // 352x288 (396 MBs) - Level 1.3
            else if (totalMbs <= 792) level = 21;     // 352x576 (792 MBs) - Level 2.1
            else if (totalMbs <= 1620) level = 30;    // 720x480 (1350 MBs) - Level 3.0
            else if (totalMbs <= 3600) level = 31;    // 1280x720 (3600 MBs) - Level 3.1
            else if (totalMbs <= 8192) level = 40;    // 1920x1080 (8160 MBs) - Level 4.0
            else if (totalMbs <= 8704) level = 41;    // 2048x1024 (8192 MBs) - Level 4.1
            else level = 42;                          // 2048x1080+ - Level 4.2
            
            // Generate SPS NAL unit (type 7)
            // Using Constrained Baseline profile (profile_idc=66, constraint_set1=1)
            var spsData = GenerateSpsNal(width, height, level);
            
            // Generate PPS NAL unit (type 8)
            var ppsData = GeneratePpsNal();
            
            // Add Annex-B start codes
            var sps = new byte[4 + spsData.Length];
            sps[0] = 0; sps[1] = 0; sps[2] = 0; sps[3] = 1;
            Buffer.BlockCopy(spsData, 0, sps, 4, spsData.Length);
            
            var pps = new byte[4 + ppsData.Length];
            pps[0] = 0; pps[1] = 0; pps[2] = 0; pps[3] = 1;
            Buffer.BlockCopy(ppsData, 0, pps, 4, ppsData.Length);
            
            return (sps, pps);
        }
        catch
        {
            return (null, null);
        }
    }
    
    /// <summary>
    /// Generate SPS NAL unit data (without start code)
    /// </summary>
    private static byte[] GenerateSpsNal(int width, int height, byte level)
    {
        // Width and height in macroblocks (minus 1 for pic_width/height_in_mbs_minus1)
        int mbWidth = (width + 15) / 16;
        int mbHeight = (height + 15) / 16;
        
        // Calculate cropping if resolution is not multiple of 16
        int cropRight = mbWidth * 16 - width;
        int cropBottom = mbHeight * 16 - height;
        bool needsCrop = cropRight > 0 || cropBottom > 0;
        
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        
        // NAL unit header: forbidden_zero_bit(1) + nal_ref_idc(2) + nal_unit_type(5)
        // 0x67 = 0 11 00111 = SPS with high priority
        bw.Write((byte)0x67);
        
        // profile_idc = 66 (Baseline) - but we use 100 (High) for better compatibility
        bw.Write((byte)100); // High profile
        
        // constraint_set0_flag(1) + constraint_set1_flag(1) + constraint_set2_flag(1) + 
        // constraint_set3_flag(1) + constraint_set4_flag(1) + constraint_set5_flag(1) + reserved(2)
        bw.Write((byte)0x00); // No constraints
        
        // level_idc
        bw.Write(level);
        
        // seq_parameter_set_id = 0 (ue(v) = 1 bit = 1)
        // log2_max_frame_num_minus4 = 0 (ue(v) = 1 bit = 1)  
        // pic_order_cnt_type = 2 (ue(v) = 011 = 3 bits)
        // max_num_ref_frames = 1 (ue(v) = 010 = 3 bits)
        // gaps_in_frame_num_value_allowed_flag = 0 (1 bit)
        // This is simplified - real SPS uses exp-golomb coding
        
        // For High profile, we need to write more fields
        // Simplified High profile SPS with hardcoded values
        byte[] spsPayload;
        if (needsCrop)
        {
            // SPS with cropping for non-16-aligned resolutions
            spsPayload = BuildSpsWithCropping(mbWidth, mbHeight, cropRight / 2, cropBottom / 2);
        }
        else
        {
            // SPS without cropping
            spsPayload = BuildSpsNoCropping(mbWidth, mbHeight);
        }
        
        var result = new byte[4 + spsPayload.Length];
        result[0] = 0x67; // NAL type SPS
        result[1] = 100;  // High profile
        result[2] = 0x00; // Constraints
        result[3] = level;
        Buffer.BlockCopy(spsPayload, 0, result, 4, spsPayload.Length);
        
        return result;
    }
    
    private static byte[] BuildSpsNoCropping(int mbWidth, int mbHeight)
    {
        // Pre-built SPS payload for common resolutions (High profile, no cropping)
        // seq_parameter_set_id=0, log2_max_frame_num=4, pic_order_cnt_type=2, num_ref_frames=1
        // This is a simplified version - encoding exp-golomb properly
        
        var bits = new System.Collections.Generic.List<bool>();
        
        // For High profile: chroma_format_idc = 1 (4:2:0)
        WriteExpGolomb(bits, 1); // chroma_format_idc
        WriteExpGolomb(bits, 0); // bit_depth_luma_minus8
        WriteExpGolomb(bits, 0); // bit_depth_chroma_minus8
        bits.Add(false); // qpprime_y_zero_transform_bypass_flag
        bits.Add(false); // seq_scaling_matrix_present_flag
        
        // log2_max_frame_num_minus4 = 0
        WriteExpGolomb(bits, 0);
        
        // pic_order_cnt_type = 2 (no POC info needed)
        WriteExpGolomb(bits, 2);
        
        // max_num_ref_frames = 1
        WriteExpGolomb(bits, 1);
        
        // gaps_in_frame_num_value_allowed_flag = 0
        bits.Add(false);
        
        // pic_width_in_mbs_minus1
        WriteExpGolomb(bits, mbWidth - 1);
        
        // pic_height_in_map_units_minus1
        WriteExpGolomb(bits, mbHeight - 1);
        
        // frame_mbs_only_flag = 1 (progressive)
        bits.Add(true);
        
        // direct_8x8_inference_flag = 1
        bits.Add(true);
        
        // frame_cropping_flag = 0
        bits.Add(false);
        
        // vui_parameters_present_flag = 0
        bits.Add(false);
        
        return BitsToBytes(bits);
    }
    
    private static byte[] BuildSpsWithCropping(int mbWidth, int mbHeight, int cropRight, int cropBottom)
    {
        var bits = new System.Collections.Generic.List<bool>();
        
        // For High profile
        WriteExpGolomb(bits, 1); // chroma_format_idc = 1 (4:2:0)
        WriteExpGolomb(bits, 0); // bit_depth_luma_minus8
        WriteExpGolomb(bits, 0); // bit_depth_chroma_minus8
        bits.Add(false); // qpprime_y_zero_transform_bypass_flag
        bits.Add(false); // seq_scaling_matrix_present_flag
        
        WriteExpGolomb(bits, 0); // log2_max_frame_num_minus4
        WriteExpGolomb(bits, 2); // pic_order_cnt_type
        WriteExpGolomb(bits, 1); // max_num_ref_frames
        bits.Add(false); // gaps_in_frame_num_value_allowed_flag
        
        WriteExpGolomb(bits, mbWidth - 1); // pic_width_in_mbs_minus1
        WriteExpGolomb(bits, mbHeight - 1); // pic_height_in_map_units_minus1
        
        bits.Add(true); // frame_mbs_only_flag
        bits.Add(true); // direct_8x8_inference_flag
        
        // frame_cropping_flag = 1
        bits.Add(true);
        WriteExpGolomb(bits, 0); // frame_crop_left_offset
        WriteExpGolomb(bits, cropRight); // frame_crop_right_offset
        WriteExpGolomb(bits, 0); // frame_crop_top_offset
        WriteExpGolomb(bits, cropBottom); // frame_crop_bottom_offset
        
        bits.Add(false); // vui_parameters_present_flag
        
        return BitsToBytes(bits);
    }
    
    /// <summary>
    /// Generate PPS NAL unit data (without start code)
    /// </summary>
    private static byte[] GeneratePpsNal()
    {
        // Simple PPS for High profile
        // NAL type = 8 (PPS), nal_ref_idc = 3
        // 0x68 = 0 11 01000
        
        var bits = new System.Collections.Generic.List<bool>();
        
        WriteExpGolomb(bits, 0); // pic_parameter_set_id
        WriteExpGolomb(bits, 0); // seq_parameter_set_id
        bits.Add(false); // entropy_coding_mode_flag (CAVLC)
        bits.Add(false); // bottom_field_pic_order_in_frame_present_flag
        WriteExpGolomb(bits, 0); // num_slice_groups_minus1
        WriteExpGolomb(bits, 0); // num_ref_idx_l0_default_active_minus1
        WriteExpGolomb(bits, 0); // num_ref_idx_l1_default_active_minus1
        bits.Add(false); // weighted_pred_flag
        bits.Add(false); bits.Add(false); // weighted_bipred_idc (2 bits = 0)
        WriteSignedExpGolomb(bits, 0); // pic_init_qp_minus26
        WriteSignedExpGolomb(bits, 0); // pic_init_qs_minus26
        WriteSignedExpGolomb(bits, 0); // chroma_qp_index_offset
        bits.Add(false); // deblocking_filter_control_present_flag
        bits.Add(false); // constrained_intra_pred_flag
        bits.Add(false); // redundant_pic_cnt_present_flag
        
        var ppsPayload = BitsToBytes(bits);
        var result = new byte[1 + ppsPayload.Length];
        result[0] = 0x68; // NAL type PPS
        Buffer.BlockCopy(ppsPayload, 0, result, 1, ppsPayload.Length);
        
        return result;
    }
    
    private static void WriteExpGolomb(System.Collections.Generic.List<bool> bits, int value)
    {
        // Exp-Golomb coding: codeNum = value, code = (leadingZeros, 1, suffix)
        int codeNum = value;
        int leadingZeros = 0;
        int temp = codeNum + 1;
        while (temp > 1)
        {
            temp >>= 1;
            leadingZeros++;
        }
        
        for (int i = 0; i < leadingZeros; i++)
            bits.Add(false);
        
        for (int i = leadingZeros; i >= 0; i--)
            bits.Add(((codeNum + 1) >> i & 1) == 1);
    }
    
    private static void WriteSignedExpGolomb(System.Collections.Generic.List<bool> bits, int value)
    {
        // Signed exp-golomb: map to unsigned
        int mapped = value <= 0 ? -2 * value : 2 * value - 1;
        WriteExpGolomb(bits, mapped);
    }
    
    private static byte[] BitsToBytes(System.Collections.Generic.List<bool> bits)
    {
        // Add RBSP trailing bits (1 followed by zeros to byte align)
        bits.Add(true);
        while (bits.Count % 8 != 0)
            bits.Add(false);
        
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = 0;
            for (int j = 0; j < 8; j++)
            {
                if (bits[i * 8 + j])
                    b |= (byte)(1 << (7 - j));
            }
            bytes[i] = b;
        }
        return bytes;
    }
    
    #endregion

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
