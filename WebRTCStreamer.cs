#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

/// <summary>
/// WebRTCStreamer is responsible for piping BGRA frames into a VP8 encoder and forwarding the
/// resulting RTP video packets into an RTCPeerConnection.  In earlier versions of
/// SIPSorcery the VideoEncoderEndPoint class provided an abstraction for encoding
/// raw frames.  However from version 8 onwards the VideoEncoderEndPoint no longer
/// accepts raw samples which results in the encoder never producing encoded
/// frames.  This implementation instead makes use of the VpxVideoEncoder directly
/// to encode BGRA frames to VP8 and sends them to the peer connection.
/// </summary>
public class WebRTCStreamer : IDisposable
{
    private RTCPeerConnection? _pc;

    // Use VpxVideoEncoder directly instead of VideoEncoderEndPoint.  See README for details.
    // The VpxVideoEncoder can encode raw BGRA frames to VP8 when supplied with
    // the width, height and pixel format.  It exposes SupportedFormats which can be
    // passed to the MediaStreamTrack and a ForceKeyFrame method which forces
    // the next encoded frame to be an IDR keyframe.
    private readonly VpxVideoEncoder _encoder = new VpxVideoEncoder();
    private readonly int _fps;

    // Channel used to buffer captured frames prior to encoding.  A small bounded
    // channel with drop-oldest semantics is used so that slow encoders do not
    // cause unlimited buffering and excessive latency.  Each item carries the
    // raw BGRA buffer along with its dimensions and intended duration (frame
    // spacing in ms).  Stride is included for completeness but is not used in
    // the current encoder since BGRA frames have a stride equal to width * 4.
    private readonly Channel<(byte[] buf, int width, int height, int stride, int durationMs)> _sendChan
        = Channel.CreateUnbounded<(byte[], int, int, int, int)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private CancellationTokenSource? _cts;
    private DateTime _lastKeyframe = DateTime.MinValue;
    private readonly TimeSpan _kfInterval = TimeSpan.FromSeconds(2);

    private readonly System.Buffers.ArrayPool<byte> _pool = System.Buffers.ArrayPool<byte>.Shared;

    public WebRTCStreamer(int fps = 60) { _fps = Math.Max(5, fps); }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        // Start the sender loop on a background task.  It will read raw
        // frames from the channel, encode them via the VP8 encoder and
        // forward the encoded samples into the peer connection.
        _ = Task.Run(SenderLoop);
        return Task.CompletedTask;
    }
    private long _enq, _deq, _sent;

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        var cfg = new RTCConfiguration { iceServers = new() };
        _pc = new RTCPeerConnection(cfg);

        // Diagnostic logging for connection state changes.
        _pc.onconnectionstatechange += st => Console.WriteLine($"[RTC] pc.state = {st}");
        _pc.oniceconnectionstatechange += st => Console.WriteLine($"[RTC] ice = {st}");

        // Create a media track using the supported formats from the VPX encoder.
        // Only VP8 is supported by the current encoder and therefore the
        // MediaStreamTrack will advertise VP8 formats only.
        var track = new MediaStreamTrack(_encoder.SupportedFormats, MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        // When the browser negotiates a video format it will be passed back here.
        // There is no need to call SetVideoSourceFormat on the VpxVideoEncoder since
        // it only supports VP8.  However, if additional encoders are added the
        // negotiated format can be used to set the encoder's codec or bit rate.
        _pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats != null && formats.Count > 0)
            {
                Console.WriteLine($"[RTC] negotiated format: {formats[0]}");
                // Optionally adjust encoder target bitrate based on negotiated codec.
                // For example: _encoder.TargetKbps = 2000;
            }
        };

        // Periodically dump queue statistics so that dropped frames can be diagnosed.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2000);
                Console.WriteLine($"[RTC] q=enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)}");
            }
        });

        // Perform the SDP offer/answer exchange.  Set the remote description from
        // the offer received from the browser, create a local answer and set it
        // on the peer connection.  Note that the call to setRemoteDescription is
        // synchronous on the C# side but will still initiate the async ICE
        // gathering process.
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        // Start the encoder immediately.  Unlike the VideoEncoderEndPoint the
        // VpxVideoEncoder does not require an explicit StartVideo call.  A
        // keyframe will automatically be generated when ForceKeyFrame is invoked.
        _encoder.ForceKeyFrame();
        Console.WriteLine("[RTC] VPX encoder initialised");

        return answer.sdp;
    }

    public Task StartVideoAsync(int width, int height)
    {
        // The VPX encoder does not need to know the resolution ahead of time.
        // Each frame passed to EncodeVideo includes its dimensions.  This
        // method remains for compatibility with the existing API and does
        // nothing.
        return Task.CompletedTask;
    }

    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
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
            Console.WriteLine("[RTC] channel full or closed -> drop");
            _pool.Return(buf);
        }
        return Task.CompletedTask;
    }

    private async Task SenderLoop()
    {
        if (_cts == null)
            return;
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Periodically request a key frame.  This ensures the
                // receiving browser can start decoding at any time without
                // needing to wait for the next natural I‑frame.  The logic
                // mirrors the previous implementation but now calls
                // VpxVideoEncoder.ForceKeyFrame().
                if (DateTime.UtcNow - _lastKeyframe > _kfInterval)
                {
                    try { _encoder.ForceKeyFrame(); } catch { }
                    _lastKeyframe = DateTime.UtcNow;
                }

                // Wait for the next raw frame from the capture thread.
                var item = await _sendChan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);

                try
                {
                    // Encode the BGRA sample into VP8.  The EncodeVideo method
                    // automatically performs any colour space conversion as
                    // necessary.  The returned buffer contains an RTP payload
                    // ready to be packetised by the RTCPeerConnection.  The
                    // duration supplied to SendVideo controls the RTP
                    // timestamp increment for the sample.
                    byte[] encoded = _encoder.EncodeVideo(item.width, item.height, item.buf,
                        VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
                    if (encoded != null && encoded.Length > 0)
                    {
                        _pc?.SendVideo((uint)item.durationMs, encoded);
                        Interlocked.Increment(ref _sent);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[RTC] encode error: " + ex.Message);
                }
                finally
                {
                    // Return the rented buffer back to the pool.
                    _pool.Return(item.buf);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the cancellation token is triggered during shutdown.
        }
    }

    public Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }
    }
}
