using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

public class WebRTCStreamer : IDisposable
{
    private RTCPeerConnection? _pc;
    private readonly VideoEncoderEndPoint _videoEP = new VideoEncoderEndPoint(); // VP8 default
    private readonly int _fps;

    private readonly Channel<(byte[] buf, int w, int h, int stride, int durationMs)> _sendChan
        = Channel.CreateBounded<(byte[], int, int, int, int)>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private CancellationTokenSource? _cts;
    private DateTime _lastKeyframe = DateTime.MinValue;
    private readonly TimeSpan _kfInterval = TimeSpan.FromSeconds(2);

    private readonly System.Buffers.ArrayPool<byte> _pool = System.Buffers.ArrayPool<byte>.Shared;

    public WebRTCStreamer(int fps = 60) { _fps = Math.Max(5, fps); }

    public Task StartAsync() { _cts = new CancellationTokenSource(); _ = Task.Run(SenderLoop); return Task.CompletedTask; }
    private long _enq, _deq, _sent;

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        var cfg = new RTCConfiguration { iceServers = new() };
        _pc = new RTCPeerConnection(cfg);

        // Logs hữu ích
        _pc.onconnectionstatechange += st => Console.WriteLine($"[RTC] pc.state = {st}");
        _pc.oniceconnectionstatechange += st => Console.WriteLine($"[RTC] ice = {st}");

        // expose các format mà encoder hỗ trợ
        var track = new MediaStreamTrack(_videoEP.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        // chờ negotiate xong format trước khi start encoder
        var fmtReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats != null && formats.Count > 0)
            {
                _videoEP.SetVideoSourceFormat(formats[0]);
                Console.WriteLine($"[RTC] negotiated format: {formats[0].ToString()}");
                fmtReady.TrySetResult(true);
            }
        };

        // khi encoder sinh gói → gửi vào peer
        _videoEP.OnVideoSourceEncodedSample += (dur, buf) =>
        {
            Interlocked.Increment(ref _sent);
            _pc.SendVideo(dur, buf);
        };

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2000);
                Console.WriteLine($"[RTC] q=enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)}");
            }
        });

        // SDP O/A
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        // đợi format sẵn sàng rồi mới bật encoder
        _ = Task.Run(async () =>
        {
            try
            {
                await fmtReady.Task;
                await _videoEP.StartVideo();       // nếu API bản bạn có
                _videoEP.ForceKeyFrame();             // I-frame đầu
                Console.WriteLine("[RTC] encoder started");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RTC] encoder start error: {ex.Message}");
            }
        });

        return answer.sdp;
    }

    public Task StartVideoAsync(int width, int height)
    {
        // có thể set FPS nếu bản SIPSorcery của bạn hỗ trợ
        // _videoEP.SetFrameRate(_fps);
        return Task.CompletedTask;
    }

    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        // đẩy frame BGRA thô → encoder
        int durationMs = Math.Max(1, 1000 / _fps);
        int size = stride * height;
        var buf = _pool.Rent(size);
        Buffer.BlockCopy(src, 0, buf, 0, size);
        _sendChan.Writer.TryWrite((buf, width, height, stride, durationMs));
        Interlocked.Increment(ref _enq);
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
                if (DateTime.UtcNow - _lastKeyframe > _kfInterval)
                {
                    try { _videoEP.ForceKeyFrame(); } catch { }
                    _lastKeyframe = DateTime.UtcNow;
                }

                var item = await _sendChan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);
                try
                {
                    _videoEP.ExternalVideoSourceRawSample(
                        (uint)item.durationMs, item.w, item.h, item.buf, VideoPixelFormatsEnum.Bgra);
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
        catch (OperationCanceledException) { }
    }

    public Task StopAsync() { try { _cts?.Cancel(); } catch { } return Task.CompletedTask; }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
    }
}
