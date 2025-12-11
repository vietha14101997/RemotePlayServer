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
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// WebRTC streamer using Media Foundation H.264 encoder.
/// Supports zero-copy GPU encoding for minimal latency.
/// </summary>
public class WebRTCStreamer_MF : IDisposable
{
    private RTCPeerConnection? _pc;
    private Task? _statsTask;
    private CancellationTokenSource? _cts;

    private volatile bool _running = false;
    public bool IsRunning => _running;
    public event Action? OnPeerDisconnected;

    // Frame input queue
    private readonly int _fps;
    private readonly int _minIntervalMs;
    private readonly Channel<FrameData> _frameChan =
        Channel.CreateBounded<FrameData>(new BoundedChannelOptions(3)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private long _lastEnqMs = 0;
    private readonly Stopwatch _gateSw = Stopwatch.StartNew();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastSendTsMs = -1;

    private long _enq, _deq, _sent;

    // Encoded AU queue
    private readonly Channel<(uint durMs, byte[] au)> _auChan =
        Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private Task? _sendAuTask;

    // Encoder
    private readonly int _targetKbps;
    private MediaFoundationH264Encoder? _encoder;
    private D3D11VideoProcessor? _videoProcessor;
    private ID3D11Device? _d3dDevice;
    private int _encW, _encH;
    private volatile bool _canSend = false;

    // Frame data structure
    private record struct FrameData(byte[] Buffer, int Width, int Height, int Stride, bool IsPooled);

    public WebRTCStreamer_MF(int fps = 30, int targetKbps = 6000)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
        _targetKbps = Math.Max(500, targetKbps);
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(SenderLoop);
        _running = true;
        return Task.CompletedTask;
    }

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };
        _pc = new RTCPeerConnection(cfg);
        Console.WriteLine("[RTC-MF] PeerConnection created");

        _pc.onconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-MF] pc.state = {st}");
            if (st == RTCPeerConnectionState.disconnected ||
                st == RTCPeerConnectionState.failed ||
                st == RTCPeerConnectionState.closed)
            {
                try { _cts?.Cancel(); } catch { }
                _running = false;
                OnPeerDisconnected?.Invoke();
            }
        };

        _pc.onicegatheringstatechange += st => Console.WriteLine($"[RTC-MF] ice gathering = {st}");

        _pc.onicecandidate += cand =>
        {
            if (cand != null)
                Console.WriteLine($"[RTC-MF] ice candidate: {cand.type} {cand.address}:{cand.port}");
        };

        _pc.oniceconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-MF] ice connection = {st}");
            if (st == RTCIceConnectionState.connected)
            {
                Console.WriteLine("[RTC-MF] ICE CONNECTED - enabling send");
                _canSend = true;
            }
        };

        // Create H.264 track
        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: 0,
            name: "H264",
            clockRate: 90000,
            channels: 0,
            fmtp: "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f");

        var track = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { h264 },
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        _pc.OnVideoFormatsNegotiated += fmts =>
        {
            Console.WriteLine("NEGOTIATED VIDEO FORMATS (MF):");
            if (fmts != null)
                foreach (var f in fmts) Console.WriteLine("  " + f);

            var ok = fmts?.Any(f => f.Codec == VideoCodecsEnum.H264) == true;
            _canSend = ok;
            Console.WriteLine("canSend(H264) = " + _canSend);
        };

        // Stats logging
        _statsTask = Task.Run(async () =>
        {
            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, _cts!.Token);
                    Console.WriteLine($"[RTC-MF] q=enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)}");
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // SDP negotiation
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        Console.WriteLine("---- REMOTE OFFER SDP (MF) ----\n" + offerSdp.Substring(0, Math.Min(500, offerSdp.Length)) + "...");
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        var sdp = answer.sdp.Replace("UDP/TLS/RTP/SAVP", "UDP/TLS/RTP/SAVPF");
        return sdp;
    }

    /// <summary>
    /// Push BGRA frame from capture (CPU memory)
    /// </summary>
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

        var frame = new FrameData(buf, width, height, stride, true);
        if (_frameChan.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _enq);
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Task.CompletedTask;
    }

    private void EnsureEncoder(int w, int h)
    {
        if (_encoder != null && w == _encW && h == _encH) return;

        // Cleanup old encoder
        _encoder?.Dispose();
        _videoProcessor?.Dispose();
        _d3dDevice?.Dispose();

        _encW = w;
        _encH = h;

        // Create D3D11 device for encoder
        var levels = new FeatureLevel[] { FeatureLevel.Level_11_0 };
        ID3D11DeviceContext ctx;
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels,
            out _d3dDevice!,
            out ctx
        );
        ctx?.Dispose();

        Console.WriteLine($"[RTC-MF] Created D3D11 device for encoder");

        // Create Media Foundation encoder
        _encoder = new MediaFoundationH264Encoder(_d3dDevice, w, h, _fps, _targetKbps);
        _encoder.OnEncodedSample += (data, durMs) =>
        {
            try { _auChan.Writer.TryWrite((durMs, data)); }
            catch { }
        };
        _encoder.Start();

        // Create video processor for color conversion
        _videoProcessor = new D3D11VideoProcessor(_d3dDevice, w, h);

        // Start AU sender task
        if (_sendAuTask == null)
            _sendAuTask = Task.Run(SendAuLoop);

        Console.WriteLine($"[RTC-MF] Encoder created: {w}x{h}@{_fps}fps {_targetKbps}kbps HW={_encoder.IsHardwareEncoder}");
    }

    private async Task SenderLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var item = await _frameChan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);

                // Drop to latest frame
                while (_frameChan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    if (item.IsPooled) ArrayPool<byte>.Shared.Return(item.Buffer);
                    item = newer;
                }

                try
                {
                    EnsureEncoder(item.Width, item.Height);
                    _encoder?.EncodeBgraFrame(item.Buffer.AsSpan(0, item.Stride * item.Height), item.Stride);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RTC-MF] Encode error: {ex.Message}");
                }
                finally
                {
                    if (item.IsPooled) ArrayPool<byte>.Shared.Return(item.Buffer);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendAuLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();
        long nextDueMs = sw.ElapsedMilliseconds;
        long auReceived = 0;
        long lastDebugMs = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await _auChan.Reader.WaitToReadAsync(ct)) break;

                // Get latest AU
                if (!_auChan.Reader.TryRead(out var item)) continue;
                while (_auChan.Reader.TryRead(out var newer)) item = newer;
                auReceived++;

                // Debug log
                var nowDebug = sw.ElapsedMilliseconds;
                if (nowDebug - lastDebugMs > 2000)
                {
                    Console.WriteLine($"[SendAuLoop-MF] AU received={auReceived}, _canSend={_canSend}");
                    lastDebugMs = nowDebug;
                }

                // Frame pacing
                var nowMs = sw.ElapsedMilliseconds;
                if (nowMs < nextDueMs)
                    await Task.Delay((int)(nextDueMs - nowMs), ct);
                nextDueMs += Math.Max(1, item.durMs);

                uint rtpStep = (uint)Math.Max(1, 90000L * item.durMs / 1000L);

                if (_canSend && _pc != null && _running && _cts != null && !_cts.IsCancellationRequested)
                {
                    // Ensure proper NAL format for WebRTC
                    var processedAu = EnsureAnnexB(item.au);
                    LogNalSummary(processedAu);
                    _pc.SendVideo(rtpStep, processedAu);
                    Interlocked.Increment(ref _sent);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Ensure AU has proper Annex-B format with start codes
    /// </summary>
    private static byte[] EnsureAnnexB(byte[] data)
    {
        // MF encoder may output data without start codes in some cases
        // Check if it already has start codes
        if (data.Length >= 4)
        {
            bool hasStartCode = (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) ||
                               (data[0] == 0 && data[1] == 0 && data[2] == 1);
            if (hasStartCode) return data;
        }

        // Add start code if missing
        var result = new byte[4 + data.Length];
        result[0] = 0; result[1] = 0; result[2] = 0; result[3] = 1;
        Buffer.BlockCopy(data, 0, result, 4, data.Length);
        return result;
    }

    private static void LogNalSummary(byte[] au)
    {
        int i = 0;
        bool sps = false, pps = false, idr = false;
        while (i + 4 <= au.Length)
        {
            int sc = (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ? 3 :
                     (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) ? 4 : 0;
            if (sc == 0) break;
            i += sc;
            if (i >= au.Length) break;
            int nal = au[i] & 0x1F;
            if (nal == 7) sps = true;
            else if (nal == 8) pps = true;
            else if (nal == 5) idr = true;
            int j = i + 1;
            for (; j + 3 < au.Length; j++)
            {
                if ((au[j] == 0 && au[j + 1] == 0 && au[j + 2] == 1) ||
                    (j + 4 <= au.Length && au[j] == 0 && au[j + 1] == 0 && au[j + 2] == 0 && au[j + 3] == 1))
                    break;
            }
            i = j;
        }
        if (idr) Console.WriteLine($"[H264-MF] AU has IDR (SPS={sps}, PPS={pps})");
    }

    public Task StopAsync()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        _sendAuTask = null;
        try { _frameChan.Writer.TryComplete(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        _sendAuTask = null;
        try { _statsTask?.Wait(200); } catch { }

        _encoder?.Dispose();
        _videoProcessor?.Dispose();
        _d3dDevice?.Dispose();

        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
    }
}
