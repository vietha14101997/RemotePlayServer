#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

/// <summary>
/// Streamer H.264: nhận BGRA từ WGC, pipe qua ffmpeg (libx264) để điều khiển thật FPS/bitrate/CRF,
/// tách Access Unit ổn định (AUD) và gửi vào RTCPeerConnection theo nhịp thực.
/// </summary>
public class WebRTCStreamer_H264 : IDisposable
{
    private RTCPeerConnection? _pc;
    private Task? _statsTask;
    private CancellationTokenSource? _cts;

    private volatile bool _running = false;
    public bool IsRunning => _running;
    public event Action? OnPeerDisconnected;

    // ---- pacing & raw BGRA queue (từ WGC) ----
    private readonly int _fps;
    private readonly int _minIntervalMs;
    private readonly Channel<(byte[] buf, int w, int h, int stride)> _sendChan =
        Channel.CreateBounded<(byte[], int, int, int)>(
            new BoundedChannelOptions(3) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private long _lastEnqMs = 0;
    private readonly Stopwatch _gateSw = Stopwatch.StartNew();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastSendTsMs = -1;

    private long _enq, _deq, _sent;

    // ---- encoded AU queue & paced sender ----
    private readonly Channel<(uint durMs, byte[] au)> _auChan =
        Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(120)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private Task? _sendAuTask;

    // ---- ffmpeg pipe encoder (điều khiển bitrate/fps thực) ----
    private readonly int _targetKbps;
    private readonly int _crf;
    private readonly string _preset;
    private readonly bool _zerolatency;
    private readonly string? _ffmpegExe;

    private FfmpegPipeEncoder? _enc;
    private int _encW, _encH;

    // ---- negotiation flag ----
    private volatile bool _canSend = false;

    public WebRTCStreamer_H264(
        int fps = 30,
        int targetKbps = 6000,
        int crf = 23,
        string preset = "veryfast",
        bool zerolatency = true,
        string? ffmpegExe = null)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);

        _targetKbps = Math.Max(0, targetKbps);
        _crf = crf;
        _preset = string.IsNullOrWhiteSpace(preset) ? "veryfast" : preset;
        _zerolatency = zerolatency;
        _ffmpegExe = ffmpegExe;
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(SenderLoop);
        _running = true;
        return Task.CompletedTask;
    }

    /// <summary>Khởi tạo PC, thương lượng H.264 (pt=102, 90kHz, packetization-mode=1).</summary>
    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        var cfg = new RTCConfiguration { iceServers = new() };
        _pc = new RTCPeerConnection(cfg);

        _pc.onconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC] pc.state = {st}");
            if (st == RTCPeerConnectionState.disconnected ||
                st == RTCPeerConnectionState.failed ||
                st == RTCPeerConnectionState.closed)
            {
                try { _cts?.Cancel(); } catch { }
                _running = false;
                OnPeerDisconnected?.Invoke();
            }
        };
        _pc.oniceconnectionstatechange += st => Console.WriteLine($"[RTC] ice = {st}");

        // Tạo track H.264
        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: 102,
            name: "H264",
            clockRate: 90000,
            channels: 0,
            fmtp: "packetization-mode=1;profile-level-id=42e01f");
        var caps = new List<SDPAudioVideoMediaFormat> { h264 };
        var track = new MediaStreamTrack(SDPMediaTypesEnum.video, isRemote: false, capabilities: caps, streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        // Khi đối tác thương lượng xong
        _pc.OnVideoFormatsNegotiated += fmts =>
        {
            var ok = fmts?.Any(f => f.Codec == VideoCodecsEnum.H264) == true;
            _canSend = ok;
            var pick = fmts?.FirstOrDefault();
            Console.WriteLine("[RTC] negotiated format: " + (pick != null ? pick.ToString() : "(none)"));
        };

        // stats log
        _statsTask = Task.Run(async () =>
        {
            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, _cts!.Token);
                    Console.WriteLine($"[RTC] q=enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)}");
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // SDP
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        Console.WriteLine("[RTC] H.264 encoder (FFmpeg) initialised");
        return answer.sdp;
    }

    /// <summary>Được gọi bởi WgcCapture mỗi frame BGRA.</summary>
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        if (!_running || _cts?.IsCancellationRequested == true || _pc == null)
            return Task.CompletedTask;

        long now = _gateSw.ElapsedMilliseconds;
        if (now - _lastEnqMs < _minIntervalMs - 1) return Task.CompletedTask;
        _lastEnqMs = now;

        int size = stride * height;
        var buf = ArrayPool<byte>.Shared.Rent(size);
        Buffer.BlockCopy(src, 0, buf, 0, size);

        if (_sendChan.Writer.TryWrite((buf, width, height, stride)))
        {
            Interlocked.Increment(ref _enq);
            if ((Interlocked.Read(ref _enq) % 30) == 0)
                Console.WriteLine($"[RTC] enqueue tick (w={width}, h={height}, stride={stride})");
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return Task.CompletedTask;
    }

    // Khởi tạo/restart encoder khi có frame đầu tiên hoặc khi đổi kích thước.
    private void EnsureEncoder(int w, int h)
    {
        if (_enc == null)
        {
            _encW = w; _encH = h;
            _enc = new FfmpegPipeEncoder(_encW, _encH, _fps, _targetKbps, _crf, _preset, _zerolatency, _ffmpegExe);
            _enc.OnEncodedAccessUnit += (durMs, au) =>
            {
                // Không gửi ngay; xếp vào AU queue để SendAuLoop phát theo nhịp thực.
                try { _auChan.Writer.TryWrite((durMs, au)); } catch { }
            };
            _enc.Start();
            Console.WriteLine($"[RTC] ffmpeg started {_encW}x{_encH} {_fps}fps " +
                (_targetKbps > 0 ? $"{_targetKbps}kbps CBR" : $"CRF={_crf}"));

            if (_sendAuTask == null)
                _sendAuTask = Task.Run(() => SendAuLoop());
        }
        else if (w != _encW || h != _encH)
        {
            _encW = w; _encH = h;
            Console.WriteLine($"[RTC] resize -> restart ffmpeg: {_encW}x{_encH}");
            _enc.ReconfigureOnResize(_encW, _encH);
        }
    }

    // Vòng xử lý BGRA -> ffmpeg (không gửi trực tiếp).
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

                // Drop đến frame mới nhất để giảm độ trễ
                while (_sendChan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    ArrayPool<byte>.Shared.Return(item.buf);
                    item = newer;
                }

                try
                {
                    EnsureEncoder(item.w, item.h);

                    long nowMs = _sw.ElapsedMilliseconds;
                    uint deltaMs = (uint)((_lastSendTsMs < 0) ? Math.Max(1, 1000 / _fps)
                                                              : Math.Clamp(nowMs - _lastSendTsMs, 1, 1000));
                    _lastSendTsMs = nowMs;

                    _enc?.PushBGRAFrame(item.buf.AsSpan(0, item.stride * item.h), item.stride, deltaMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[RTC] encode error: " + ex.Message);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.buf);
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    // Vòng phát AU theo nhịp thực (fps), bỏ backlog để luôn bám hiện tại.
    private async Task SendAuLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();
        long nextDueMs = sw.ElapsedMilliseconds;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await _auChan.Reader.WaitToReadAsync(ct)) break;

                // Lấy AU mới nhất (bỏ backlog)
                if (!_auChan.Reader.TryRead(out var item)) continue;
                while (_auChan.Reader.TryRead(out var newer)) item = newer;

                // Một số packetiser kén AUD => lược bỏ AUD đầu (nếu có)
                var au = StripLeadingAud(item.au);

                // Pace theo delta thực (durMs) thay vì cố định theo fps
                var nowMs = sw.ElapsedMilliseconds;
                if (nowMs < nextDueMs)
                    await Task.Delay((int)(nextDueMs - nowMs), ct);
                nextDueMs += Math.Max(1, item.durMs);

                // RTP step theo 90kHz clock dựa trên durMs của AU
                uint rtpStep = (uint)Math.Max(1, 90000L * item.durMs / 1000L);

                if (_canSend && _pc != null && _running && _cts != null && !_cts.IsCancellationRequested)
                {
                    _pc.SendVideo(rtpStep, au);
                    Interlocked.Increment(ref _sent);
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    // Bỏ NAL AUD đầu tiên nếu xuất hiện ngay đầu AU (giúp 1 số RTP packetiser).
    private static byte[] StripLeadingAud(byte[] au)
    {
        if (au.Length < 4) return au;

        int pos;
        // tìm start code đầu
        if (au.Length >= 4 && au[0] == 0 && au[1] == 0 && au[2] == 0 && au[3] == 1) pos = 4;
        else if (au.Length >= 3 && au[0] == 0 && au[1] == 0 && au[2] == 1) pos = 3;
        else return au;

        int nalType = (pos < au.Length) ? (au[pos] & 0x1F) : -1;
        if (nalType != 9) return au; // không phải AUD

        // tìm start code kế tiếp để cắt bỏ AUD
        for (int i = pos + 1; i + 3 <= au.Length; i++)
        {
            if (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1)
                return au.AsSpan(i).ToArray();
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
                return au.AsSpan(i).ToArray();
        }
        // chỉ có AUD? trả nguyên
        return au;
    }

    public Task StopAsync()
    {
        try { _running = false; } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        try { _sendAuTask = null; } catch { }
        try { _sendChan.Writer.TryComplete(); } catch { }
        try { _enc?.Dispose(); _enc = null; } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _running = false; } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        try { _sendAuTask = null; } catch { }
        try { _statsTask?.Wait(200); } catch { }
        try { _enc?.Dispose(); _enc = null; } catch { }
        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
    }
}
