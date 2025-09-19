#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;              // <- encoder H.264 (FFmpeg)
using System.Collections.Generic;

/// <summary>
/// WebRTCStreamer_H264: nhận BGRA frame, encode H.264 (libx264) và đẩy vào RTCPeerConnection.
/// </summary>
public class WebRTCStreamer_H264 : IWebRTCStreamer
{
    private RTCPeerConnection? _pc;
    private Task? _statsTask;

    // FFmpeg H.264 encoder
    private readonly FFmpegVideoEncoder _encoder = new FFmpegVideoEncoder();
    private readonly int _fps;

    private readonly Channel<(byte[] buf, int width, int height, int stride)> _sendChan =
        Channel.CreateBounded<(byte[], int, int, int)>(
            new BoundedChannelOptions(capacity: 3) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastSendTsMs = -1;
    private long _enq, _deq, _sent;

    // Chọn trần kích thước encode để vừa đẹp vừa nhẹ (bạn có thể đẩy lên 1920x1080 nếu máy khoẻ)
    private readonly int _maxW = 1280, _maxH = 720;
    private byte[]? _scaleBuf;

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

    // Giới hạn tốc độ vào để giữ latency thấp
    private readonly int _minIntervalMs;
    private long _lastEnqMs = 0;
    private readonly Stopwatch _gateSw = Stopwatch.StartNew();

    public WebRTCStreamer_H264(int fps = 30, int targetKbps = 6000, int crf = 23, string preset = "veryfast", bool zerolatency = true)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(SenderLoop);
        return Task.CompletedTask;
    }

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        var cfg = new RTCConfiguration { iceServers = new() };
        _pc = new RTCPeerConnection(cfg);
        _pc.onconnectionstatechange += st => Console.WriteLine($"[RTC] pc.state = {st}");
        _pc.oniceconnectionstatechange += st => Console.WriteLine($"[RTC] ice = {st}");

        // Thoả thuận định dạng: H.264
        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: 102,                            // 96–127 (dynamic)
            name: "H264",
            clockRate: 90000,                   // bắt buộc cho H.264
            channels: 0,
            fmtp: "packetization-mode=1;profile-level-id=42e01f"); // fmtp phổ biến

        var caps = new List<SDPAudioVideoMediaFormat> { h264 };

        // Tạo local video track từ capabilities trên:
        var track = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: caps,
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        _pc.OnVideoFormatsNegotiated += fmts =>
        {
            if (fmts != null && fmts.Count > 0)
            {
                Console.WriteLine($"[RTC] negotiated format: {fmts[0]}");
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
        Console.WriteLine("[RTC] H.264 encoder (FFmpeg) initialised");
        return answer.sdp;
    }

    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        long now = _gateSw.ElapsedMilliseconds;
        if (now - _lastEnqMs < _minIntervalMs - 1) return Task.CompletedTask;
        _lastEnqMs = now;

        int size = stride * height;
        var buf = new byte[size];
        Buffer.BlockCopy(src, 0, buf, 0, size);

        if (_sendChan.Writer.TryWrite((buf, width, height, stride)))
        {
            Interlocked.Increment(ref _enq);
            if ((Interlocked.Read(ref _enq) % 30) == 0)
                Console.WriteLine($"[RTC] enqueue tick (w={width}, h={height}, stride={stride})");
        }
        return Task.CompletedTask;
    }

    static (int w, int h) FitEven(int w, int h, int maxW, int maxH)
    {
        double s = Math.Min((double)maxW / w, (double)maxH / h);
        if (s >= 1.0) return (w & ~1, h & ~1);
        int nw = Math.Max(2, ((int)Math.Round(w * s)) & ~1);
        int nh = Math.Max(2, ((int)Math.Round(h * s)) & ~1);
        return (nw, nh);
    }

    private async Task SenderLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var item = await _sendChan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);
                while (_sendChan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    item = newer; // luôn giữ khung mới nhất
                }

                try
                {
                    var (ew, eh) = FitEven(item.width, item.height, _maxW, _maxH);

                    byte[] srcBgra = item.buf;
                    int encStrideBgra = item.stride;
                    if (ew != item.width || eh != item.height)
                    {
                        int need = ew * eh * 4;
                        _scaleBuf ??= new byte[need];
                        if (_scaleBuf.Length < need) _scaleBuf = new byte[need];

                        DownscaleBgraBilinear(item.buf, item.width, item.height, item.stride, _scaleBuf, ew, eh, ew * 4);
                        srcBgra = _scaleBuf;
                        encStrideBgra = ew * 4;
                    }

                    // BGRA -> I420
                    int ySize, uSize, vSize;
                    int i420Size = ew * eh + (ew / 2) * (eh / 2) * 2;
                    byte[] i420 = System.Buffers.ArrayPool<byte>.Shared.Rent(i420Size);
                    try
                    {
                        BgraToI420(srcBgra, ew, eh, encStrideBgra, i420, out ySize, out uSize, out vSize);

                        var encoded = _encoder.EncodeVideo(
                            ew, eh, i420, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264);

                        if (encoded != null && encoded.Length > 0 && _pc != null)
                        {
                            long nowMs = _sw.ElapsedMilliseconds;
                            int deltaMs = _lastSendTsMs < 0 ? Math.Max(1, 1000 / _fps)
                                                            : (int)Math.Clamp(nowMs - _lastSendTsMs, 1, 1000);
                            _lastSendTsMs = nowMs;

                            _pc.SendVideo((uint)deltaMs, encoded);
                            Interlocked.Increment(ref _sent);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[RTC] encode error: " + ex.Message);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(i420);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[RTC] encode error: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* normal on stop */ }
    }

    public Task StopAsync() { try { _cts?.Cancel(); } catch { } return Task.CompletedTask; }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _statsTask?.Wait(200); } catch { }
        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }
    }

    static void BgraToI420(byte[] src, int sw, int sh, int sstride, byte[] dst, out int ySize, out int uSize, out int vSize)
    {
        // I420 layout: [YYYY...][UU...][VV...]
        int yPlane = sw * sh;
        int cW = sw / 2;
        int cH = sh / 2;
        int uvPlane = cW * cH;

        ySize = yPlane; uSize = uvPlane; vSize = uvPlane;

        // Lấy tham chiếu
        var Y = dst.AsSpan(0, yPlane);
        var U = dst.AsSpan(yPlane, uvPlane);
        var V = dst.AsSpan(yPlane + uvPlane, uvPlane);

        // Tạo Y + downsample 2x2 cho U,V (BT.601 approx)
        int uIdx = 0, vIdx = 0;
        for (int y = 0; y < sh; y += 2)
        {
            int yRow0 = y * sstride;
            int yRow1 = Math.Min(sh - 1, y + 1) * sstride;

            for (int x = 0; x < sw; x += 2)
            {
                // 2x2 block sample (B,G,R,A order)
                int x0 = x * 4, x1 = Math.Min(sw - 1, x + 1) * 4;

                // p00
                byte B00 = src[yRow0 + x0 + 0], G00 = src[yRow0 + x0 + 1], R00 = src[yRow0 + x0 + 2];
                // p10
                byte B10 = src[yRow0 + x1 + 0], G10 = src[yRow0 + x1 + 1], R10 = src[yRow0 + x1 + 2];
                // p01
                byte B01 = src[yRow1 + x0 + 0], G01 = src[yRow1 + x0 + 1], R01 = src[yRow1 + x0 + 2];
                // p11
                byte B11 = src[yRow1 + x1 + 0], G11 = src[yRow1 + x1 + 1], R11 = src[yRow1 + x1 + 2];

                // viết Y cho 2x2
                int yIdx0 = y * sw + x;
                int yIdx1 = (y + 1) * sw + x;
                Y[yIdx0 + 0] = (byte)((66 * R00 + 129 * G00 + 25 * B00 + 128) >> 8); // ~0.257R+0.504G+0.098B + 16, bỏ +16 cho gọn
                Y[yIdx0 + 1] = (byte)((66 * R10 + 129 * G10 + 25 * B10 + 128) >> 8);
                if (y + 1 < sh)
                {
                    Y[yIdx1 + 0] = (byte)((66 * R01 + 129 * G01 + 25 * B01 + 128) >> 8);
                    Y[yIdx1 + 1] = (byte)((66 * R11 + 129 * G11 + 25 * B11 + 128) >> 8);
                }

                // tính U,V trung bình 4 điểm (BT.601 approx)
                int Rsum = R00 + R10 + R01 + R11;
                int Gsum = G00 + G10 + G01 + G11;
                int Bsum = B00 + B10 + B01 + B11;

                int Uv = ((-38 * Rsum - 74 * Gsum + 112 * Bsum + 512) >> 10) + 128;
                int Vv = ((112 * Rsum - 94 * Gsum - 18 * Bsum + 512) >> 10) + 128;

                U[uIdx++] = (byte)Math.Clamp(Uv, 0, 255);
                V[vIdx++] = (byte)Math.Clamp(Vv, 0, 255);
            }
        }
    }
}
