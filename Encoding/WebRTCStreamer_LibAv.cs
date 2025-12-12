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
using Vortice.Direct3D11;
using RemotePlayServer.Encoding;

/// <summary>
/// WebRTC H.264 streamer using LibAvEncoder (FFmpeg in-process).
/// Provides lower latency than pipe-based encoding by eliminating process boundary.
/// </summary>
public class WebRTCStreamer_LibAv : IDisposable
{
    private RTCPeerConnection? _pc;
    private Task? _statsTask;
    private CancellationTokenSource? _cts;

    private volatile bool _running = false;
    public bool IsRunning => _running;
    public event Action? OnPeerDisconnected;
    public event Action<string>? OnIceCandidate;

    private readonly int _fps;
    private readonly int _minIntervalMs;
    private readonly int _targetKbps;
    private ID3D11Device? _device;

    // NV12 input channel - larger capacity to buffer frames during encoding
    private readonly Channel<(byte[] buf, int w, int h)> _nv12Chan =
        Channel.CreateBounded<(byte[], int, int)>(
            new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private readonly Stopwatch _gateSw = Stopwatch.StartNew();
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private long _enq, _deq, _sent;

    // Encoded AU queue - larger capacity and wait mode to ensure all frames are sent
    private readonly Channel<(uint durMs, byte[] au)> _auChan =
        Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private Task? _sendAuTask;

    // LibAv encoder
    private LibAvEncoder? _enc;
    private int _encW, _encH;

    private volatile bool _canSend = false;

    public WebRTCStreamer_LibAv(
        int fps = 30,
        int targetKbps = 4000,
        ID3D11Device? device = null)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
        _targetKbps = Math.Max(1000, targetKbps);
        _device = device;
        
        Console.WriteLine($"[RTC-LibAv] Created: {_fps}fps, {_targetKbps}kbps");
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
            var init = new RTCIceCandidateInit { candidate = candidate, sdpMLineIndex = 0, sdpMid = "0" };
            _pc.addIceCandidate(init);
            Console.WriteLine($"[RTC-LibAv] Added remote ICE: {candidate.Substring(0, Math.Min(50, candidate.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RTC-LibAv] AddIceCandidate error: {ex.Message}");
        }
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
        Console.WriteLine("[RTC-LibAv] PeerConnection created");

        // Forward local ICE candidates to client
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Console.WriteLine($"[RTC-LibAv] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                Console.WriteLine("[RTC-LibAv] ICE gathering complete");
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        _pc.onconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-LibAv] pc.state = {st}");
            if (st == RTCPeerConnectionState.disconnected ||
                st == RTCPeerConnectionState.failed ||
                st == RTCPeerConnectionState.closed)
            {
                try { _cts?.Cancel(); } catch { }
                _running = false;
                OnPeerDisconnected?.Invoke();
            }
        };

        _pc.oniceconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-LibAv] ice = {st}");
            if (st == RTCIceConnectionState.connected)
            {
                Console.WriteLine("[RTC-LibAv] ICE CONNECTED");
                _canSend = true;
            }
        };

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
            var ok = fmts?.Any(f => f.Codec == VideoCodecsEnum.H264) == true;
            _canSend = ok;
            Console.WriteLine($"[RTC-LibAv] canSend(H264) = {_canSend}");
        };

        _statsTask = Task.Run(async () =>
        {
            try
            {
                while (_cts != null && !_cts.IsCancellationRequested)
                {
                    await Task.Delay(2000, _cts!.Token);
                    Console.WriteLine($"[RTC-LibAv] q=enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)}");
                }
            }
            catch (OperationCanceledException) { }
        });

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        return answer.sdp.Replace("UDP/TLS/RTP/SAVP", "UDP/TLS/RTP/SAVPF");
    }

    public Task PushNV12BytesAsync(byte[] src, int width, int height)
    {
        if (!_running || _cts?.IsCancellationRequested == true || _pc == null)
            return Task.CompletedTask;

        // No rate limiting - let the queue handle backpressure
        // The bounded channel will drop oldest if full (capacity=16)

        int size = width * height * 3 / 2;
        var buf = ArrayPool<byte>.Shared.Rent(size);
        Buffer.BlockCopy(src, 0, buf, 0, size);

        if (_nv12Chan.Writer.TryWrite((buf, width, height)))
        {
            Interlocked.Increment(ref _enq);
            if ((Interlocked.Read(ref _enq) % 30) == 0)
                Console.WriteLine($"[RTC-LibAv] enqueue NV12 {width}x{height}");
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buf);
            // Log when queue is full
            Console.WriteLine("[RTC-LibAv] NV12 queue full, frame dropped");
        }
        return Task.CompletedTask;
    }

    // For compatibility - convert BGRA to NV12 would need GpuColorConverter
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        Console.WriteLine("[RTC-LibAv] WARNING: BGRA input not supported, use NV12");
        return Task.CompletedTask;
    }

    private void EnsureEncoder(int w, int h)
    {
        if (_device == null)
        {
            Console.WriteLine("[RTC-LibAv] ERROR: D3D11 device not set");
            return;
        }

        if (_enc == null)
        {
            _encW = w;
            _encH = h;
            _enc = new LibAvEncoder(_encW, _encH, _fps, _targetKbps * 1000, _device);
            
            _enc.OnEncodedData += (nalData, isKeyFrame, pts) =>
            {
                // Calculate duration based on fps
                uint durMs = (uint)(1000 / _fps);
                try { _auChan.Writer.TryWrite((durMs, nalData)); } catch { }
            };

            if (!_enc.Initialize())
            {
                Console.WriteLine("[RTC-LibAv] ERROR: Encoder init failed");
                _enc.Dispose();
                _enc = null;
                return;
            }

            Console.WriteLine($"[RTC-LibAv] Encoder started {_encW}x{_encH} @ {_fps}fps {_targetKbps}kbps");

            if (_sendAuTask == null)
                _sendAuTask = Task.Run(() => SendAuLoop());
        }
        else if (w != _encW || h != _encH)
        {
            Console.WriteLine($"[RTC-LibAv] Resolution change {_encW}x{_encH} -> {w}x{h} (restart encoder)");
            _enc.Dispose();
            _enc = null;
            EnsureEncoder(w, h);
        }
    }

    private async Task SenderLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var item = await _nv12Chan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);

                // Process ALL frames without dropping for maximum FPS
                // Only drop if queue is critically full (>12 frames backed up)
                int dropped = 0;
                while (_nv12Chan.Reader.Count > 12 && _nv12Chan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    ArrayPool<byte>.Shared.Return(item.buf);
                    item = newer;
                    dropped++;
                }
                if (dropped > 0)
                    Console.WriteLine($"[RTC-LibAv] Dropped {dropped} frames (queue overflow)");

                try
                {
                    EnsureEncoder(item.w, item.h);
                    _enc?.EncodeNV12(item.buf, item.w, item.h);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RTC-LibAv] encode error: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.buf);
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
        long _auReceived = 0;
        long _lastDebugMs = 0;
        long _framesSentInInterval = 0;
        long _bytesSentInInterval = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await _auChan.Reader.WaitToReadAsync(ct)) break;

                // Process ALL available AUs without dropping
                while (_auChan.Reader.TryRead(out var item))
                {
                    _auReceived++;
                    _framesSentInInterval++;
                    _bytesSentInInterval += item.au.Length;

                    var nowDebug = sw.ElapsedMilliseconds;
                    if (nowDebug - _lastDebugMs >= 2000)
                    {
                        double elapsedSec = (nowDebug - _lastDebugMs) / 1000.0;
                        double actualFps = _framesSentInInterval / elapsedSec;
                        double actualKbps = (_bytesSentInInterval * 8 / 1000.0) / elapsedSec;
                        Console.WriteLine($"[RTC-LibAv] OUTPUT: {actualFps:F1} fps, {actualKbps:F0} kbps | AU={_auReceived}");
                        _framesSentInInterval = 0;
                        _bytesSentInInterval = 0;
                        _lastDebugMs = nowDebug;
                    }

                    // No pacing delay - send immediately for lowest latency
                    // WebRTC will handle congestion control internally

                    uint rtpStep = (uint)Math.Max(1, 90000L * item.durMs / 1000L);

                    if (_canSend && _pc != null && _running)
                    {
                        LogKeyFrame(item.au);
                        _pc.SendVideo(rtpStep, item.au);
                        Interlocked.Increment(ref _sent);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void LogKeyFrame(byte[] au)
    {
        // Check for IDR NAL (type 5)
        for (int i = 0; i + 4 < au.Length; i++)
        {
            if ((au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ||
                (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1))
            {
                int offset = (au[i + 2] == 1) ? 3 : 4;
                if (i + offset < au.Length)
                {
                    int nalType = au[i + offset] & 0x1F;
                    if (nalType == 5)
                    {
                        Console.WriteLine("[RTC-LibAv] IDR frame");
                        return;
                    }
                }
            }
        }
    }

    public Task StopAsync()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _enc?.Flush(); } catch { }
        try { _enc?.Dispose(); _enc = null; } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        try { _statsTask?.Wait(100); } catch { }
        try { _enc?.Dispose(); _enc = null; } catch { }
        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
    }
}
