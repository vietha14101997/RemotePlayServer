#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

/// <summary>
/// WebRTCStreamer: nhận BGRA frame, encode VP8 và đẩy vào RTCPeerConnection.
/// </summary>
public class WebRTCStreamer : IWebRTCStreamer
{
    private RTCPeerConnection? _pc;
    private Task? _statsTask;

    // Dùng encoder VP8 trực tiếp (SIPSorceryMedia.Encoders).
    private readonly VpxVideoEncoder _encoder = new VpxVideoEncoder();
    private readonly int _fps;

    // Hàng đợi nhỏ, bỏ khung cũ để luôn lấy khung mới nhất (low-latency).
    private readonly Channel<(byte[] buf, int width, int height, int stride, int durationMs)> _sendChan
        = Channel.CreateBounded<(byte[], int, int, int, int)>(
            new BoundedChannelOptions(capacity: 3) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private CancellationTokenSource? _cts;
    private DateTime _lastKeyframe = DateTime.MinValue;
    private readonly TimeSpan _kfInterval = TimeSpan.FromSeconds(5);
    private readonly System.Buffers.ArrayPool<byte> _pool = System.Buffers.ArrayPool<byte>.Shared;

    // Đồng hồ thực để tính delta timestamp RTP theo thời gian thực
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastSendTsMs = -1;
    private readonly System.Diagnostics.Stopwatch _gateSw = System.Diagnostics.Stopwatch.StartNew();
    private long _lastEnqMs = 0;
    private readonly int _minIntervalMs;
    private readonly int _maxW = 1280, _maxH = 720;   // đặt “nấc” mong muốn
    private byte[]? _scaleBuf;

    static (int w, int h) FitEven(int w, int h, int maxW, int maxH)
    {
        double s = Math.Min((double)maxW / w, (double)maxH / h);
        if (s >= 1.0) return (w & ~1, h & ~1); // không phóng to, ép chẵn
        int nw = Math.Max(2, ((int)Math.Round(w * s)) & ~1);
        int nh = Math.Max(2, ((int)Math.Round(h * s)) & ~1);
        return (nw, nh);
    }

    static unsafe void DownscaleBgraBilinear(
        byte[] src, int sw, int sh, int sstride,
        byte[] dst, int dw, int dh, int dstride)
    {
        fixed (byte* ps = src)
        fixed (byte* pd = dst)
        {
            double sx = (double)(sw - 1) / Math.Max(1, dw - 1);
            double sy = (double)(sh - 1) / Math.Max(1, dh - 1);
            for (int y = 0; y < dh; y++)
            {
                double fy = y * sy;
                int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1);
                double wy = fy - y0;
                byte* drow = pd + y * dstride;
                for (int x = 0; x < dw; x++)
                {
                    double fx = x * sx;
                    int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1);
                    double wx = fx - x0;

                    byte* p00 = ps + y0 * sstride + x0 * 4;
                    byte* p10 = ps + y0 * sstride + x1 * 4;
                    byte* p01 = ps + y1 * sstride + x0 * 4;
                    byte* p11 = ps + y1 * sstride + x1 * 4;

                    for (int c = 0; c < 4; c++)
                    { // B,G,R,A
                        double v =
                            (1 - wy) * ((1 - wx) * p00[c] + wx * p10[c]) +
                             wy * ((1 - wx) * p01[c] + wx * p11[c]);
                        drow[x * 4 + c] = (byte)(v + 0.5);
                    }
                }
            }
        }
    }

    public WebRTCStreamer(int fps = 30, uint targetKbps = 5000)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
        _encoder.TargetKbps = targetKbps; // đặt bitrate mục tiêu  ~3.5 Mbps cho 1080p@24–30
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(SenderLoop);
        return Task.CompletedTask;
    }

    private long _enq, _deq, _sent;

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        var cfg = new RTCConfiguration { iceServers = new() };
        _pc = new RTCPeerConnection(cfg);

        _pc.onconnectionstatechange += st => Console.WriteLine($"[RTC] pc.state = {st}");
        _pc.oniceconnectionstatechange += st => Console.WriteLine($"[RTC] ice = {st}");

        var track = new MediaStreamTrack(_encoder.SupportedFormats, MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        _pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats != null && formats.Count > 0)
            {
                Console.WriteLine($"[RTC] negotiated format: {formats[0]}");
                // Có thể set bitrate ở đây nếu cần: _encoder.TargetKbps = 2000;
                _encoder.TargetKbps = 3500;
            }
        };

        _statsTask = Task.Run(async () =>
        {
            while (!_cts!.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, _cts.Token);
                    Console.WriteLine($"[RTC] q=enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)}");
                }
                catch (OperationCanceledException) { break; }
            }
        });

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        _encoder.ForceKeyFrame();
        Console.WriteLine("[RTC] VPX encoder initialised");

        return answer.sdp;
    }

    public Task StartVideoAsync(int width, int height) => Task.CompletedTask;

    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        // durationMs ở item chỉ là "gợi ý" ban đầu, ta sẽ thay bằng delta thực ở lúc gửi
        long now = _gateSw.ElapsedMilliseconds;
        if (now - _lastEnqMs < _minIntervalMs - 1)
            return Task.CompletedTask; // chưa tới nhịp -> bỏ từ gốc, không copy/alloc
        _lastEnqMs = now;
        int durationMs = Math.Max(1, 1000 / _fps);

        int size = stride * height;
        var buf = _pool.Rent(size);
        Buffer.BlockCopy(src, 0, buf, 0, size);

        if (_sendChan.Writer.TryWrite((buf, width, height, stride, durationMs)))
        {
            Interlocked.Increment(ref _enq);
            if ((Interlocked.Read(ref _enq) % 30) == 0)
                Console.WriteLine($"[RTC] enqueue tick (w={width}, h={height}, stride={stride})");
        }
        else
        {
            _pool.Return(buf); // channel đầy -> drop
        }
        return Task.CompletedTask;
    }

    private async Task SenderLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Cưỡng bức keyframe theo chu kỳ để peer mới có thể bắt đầu ngay.
                if (DateTime.UtcNow - _lastKeyframe > _kfInterval)
                {
                    try { _encoder.ForceKeyFrame(); } catch { }
                    _lastKeyframe = DateTime.UtcNow;
                }

                // Lấy 1 khung từ channel rồi drain để giữ khung mới nhất
                var item = await _sendChan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);
                while (_sendChan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    _pool.Return(item.buf);
                    item = newer; // luôn giữ newest
                }

                try
                {
                    // Quyết định kích thước encode trước
                    var (ew, eh) = FitEven(item.width, item.height, _maxW, _maxH);

                    byte[] srcForEnc = item.buf;
                    int encStride = item.stride;

                    if (ew != item.width || eh != item.height)
                    {
                        int need = ew * eh * 4;
                        _scaleBuf ??= new byte[need];
                        if (_scaleBuf.Length < need) _scaleBuf = new byte[need];

                        DownscaleBgraBilinear(item.buf, item.width, item.height, item.stride, _scaleBuf, ew, eh, ew * 4);
                        srcForEnc = _scaleBuf;
                        encStride = ew * 4;
                    }

                    // ✅ Dùng VP8 nhất quán để khớp với codec thực tế
                    var encoded = _encoder.EncodeVideo(ew, eh, srcForEnc, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

                    if (encoded != null && encoded.Length > 0)
                    {
                        // 🔸 TÍNH DELTA THỰC CHO RTP TIMESTAMP
                        long nowMs = _sw.ElapsedMilliseconds;
                        int deltaMs;
                        if (_lastSendTsMs < 0)
                        {
                            // Khung đầu: dùng gần 1000/fps
                            deltaMs = Math.Max(1, 1000 / _fps);
                        }
                        else
                        {
                            // Delta theo thời gian thực giữa 2 lần gửi
                            long d = nowMs - _lastSendTsMs;
                            // Clamp nhẹ để tránh nhảy số quá lớn nếu thread bị treo
                            deltaMs = (int)Math.Clamp(d, 1, 1000);
                        }
                        _lastSendTsMs = nowMs;

                        _pc?.SendVideo((uint)deltaMs, encoded);
                        Interlocked.Increment(ref _sent);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[RTC] encode error: " + ex.Message);
                }
                finally
                {
                    _pool.Return(item.buf);
                }
            }
        }
        catch (OperationCanceledException) { /* normal on stop */ }
    }

    public Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _statsTask?.Wait(200); } catch { }
        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }
    }
}
