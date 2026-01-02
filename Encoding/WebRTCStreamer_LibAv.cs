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
/// WebRTC H.264/H.265 streamer using LibAvEncoder (FFmpeg in-process).
/// Provides lower latency than pipe-based encoding by eliminating process boundary.
/// Supports both H.264 (AVC) and H.265 (HEVC) codecs with automatic fallback.
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
    private readonly VideoCodec _preferredCodec;

    /// <summary>
    /// The currently active video codec (determined after encoder initialization)
    /// </summary>
    public VideoCodec CurrentCodec => _enc?.CurrentCodec ?? _preferredCodec;

    // NV12 input channel - Small capacity to force dropping old frames
    // Reduced from 16 to 2 for Ultra Low Latency
    private readonly Channel<(byte[] buf, int w, int h)> _nv12Chan =
        Channel.CreateBounded<(byte[], int, int)>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private readonly Stopwatch _gateSw = Stopwatch.StartNew();
    // private readonly Stopwatch _sw = Stopwatch.StartNew(); // Not used, removing

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
        ID3D11Device? device = null,
        VideoCodec preferredCodec = VideoCodec.H265)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
        _targetKbps = Math.Max(1000, targetKbps);
        _device = device;
        _preferredCodec = preferredCodec;

        Console.WriteLine($"[RTC-LibAv] Created: {_fps}fps, {_targetKbps}kbps, preferred codec: {_preferredCodec}");
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
                // LAN only - no STUN to ensure local host candidates are used prioritized
                // new RTCIceServer { urls = "stun:stun.l.google.com:19302" }, 
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
                Console.WriteLine($"[RTC-LibAv] ICE CONNECTED ({st}) - Starting Video Flow");
                _canSend = true;
            }
            else if (st == RTCIceConnectionState.failed || st == RTCIceConnectionState.disconnected || st == RTCIceConnectionState.closed)
            {
                 _canSend = false;
            }
        };

        // Create video capabilities based on preferred codec
        var capabilities = new List<SDPAudioVideoMediaFormat>();

        // H.265/HEVC format (preferred if encoder supports it)
        if (_preferredCodec == VideoCodec.H265)
        {
            var h265 = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                id: 96,
                name: "H265",
                clockRate: 90000,
                channels: 0,
                fmtp: "profile-id=1;level-id=120");  // Main Profile, Level 4.0
            capabilities.Add(h265);
            Console.WriteLine("[RTC-LibAv] Advertising H.265/HEVC codec");
        }

        // H.264/AVC format (always include as fallback)
        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: 97,
            name: "H264",
            clockRate: 90000,
            channels: 0,
            fmtp: "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e033");
        capabilities.Add(h264);

        var track = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: capabilities,
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        _pc.OnVideoFormatsNegotiated += fmts =>
        {
            var hasH265 = fmts?.Any(f => f.ToString()?.ToUpper().Contains("H265") == true) == true;
            var hasH264 = fmts?.Any(f => f.Codec == VideoCodecsEnum.H264) == true;
            Console.WriteLine($"[RTC-LibAv] Video formats negotiated - H265: {hasH265}, H264: {hasH264}");
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

        // Fix: Only add F if not already SAVPF (avoid SAVPF -> SAVPFF bug)
        var sdp = answer.sdp ?? "";
        return sdp.Contains("SAVPF") ? sdp : sdp.Replace("SAVP", "SAVPF");
    }

    public Task PushNV12BytesAsync(byte[] src, int width, int height)
    {
        if (!_running || _cts?.IsCancellationRequested == true || _pc == null)
            return Task.CompletedTask;

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
            _enc = new LibAvEncoder(_encW, _encH, _fps, _targetKbps * 1000, _device, _preferredCodec);
            
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
                // Wait for available item
                var item = await _nv12Chan.Reader.ReadAsync(ct);
                Interlocked.Increment(ref _deq);

                // ULTRA LOW LATENCY: Drain queue and only process the very latest frame
                // If we fell behind, drop everything except the newest frame
                int dropped = 0;
                while (_nv12Chan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    ArrayPool<byte>.Shared.Return(item.buf); // Return old buffer
                    item = newer; // Keep newer frame
                    dropped++;
                }
                
                if (dropped > 0 && (dropped % 30 == 0)) // Log occasionally to avoid spam
                    Console.WriteLine($"[RTC-LibAv] Dropped {dropped} frames (latency catch-up)");

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

    private void LogKeyFrame(byte[] au)
    {
        // Check for IDR NAL units in both H.264 and H.265
        for (int i = 0; i + 4 < au.Length; i++)
        {
            if ((au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ||
                (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1))
            {
                int offset = (au[i + 2] == 1) ? 3 : 4;
                if (i + offset < au.Length)
                {
                    byte nalByte = au[i + offset];

                    if (CurrentCodec == VideoCodec.H265)
                    {
                        // HEVC: NAL type is in bits 1-6 (shifted right by 1)
                        int hevcNalType = (nalByte >> 1) & 0x3F;
                        // IDR_W_RADL=19, IDR_N_LP=20, CRA_NUT=21
                        if (hevcNalType == 19 || hevcNalType == 20 || hevcNalType == 21)
                        {
                            Console.WriteLine($"[RTC-LibAv] HEVC IDR frame (NAL type {hevcNalType})");
                            return;
                        }
                    }
                    else
                    {
                        // H.264: NAL type is in bits 0-4
                        int h264NalType = nalByte & 0x1F;
                        if (h264NalType == 5)
                        {
                            Console.WriteLine("[RTC-LibAv] H.264 IDR frame");
                            return;
                        }
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
