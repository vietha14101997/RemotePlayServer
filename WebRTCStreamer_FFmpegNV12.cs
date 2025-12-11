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
/// Hybrid ZeroCopy streamer for AMD GPUs:
/// Pipeline: DXGI Capture (GPU) -> D3D11 Video Processor (GPU: BGRA->NV12) -> FFmpeg h264_amf (GPU encoding)
/// 
/// This combines:
/// - GPU-accelerated color conversion (Video Processor)
/// - FFmpeg h264_amf hardware encoding
/// - Reduced pipe bandwidth: NV12 is 62% smaller than BGRA
/// 
/// For 3840x768 @ 30fps:
/// - BGRA: ~377 MB/s through pipe
/// - NV12: ~141 MB/s through pipe (much more achievable)
/// </summary>
public class WebRTCStreamer_FFmpegNV12 : IDisposable
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

    // Texture queue for GPU input
    private readonly Channel<TextureFrame> _textureChan =
        Channel.CreateBounded<TextureFrame>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private long _lastEnqMs = 0;
    private readonly Stopwatch _gateSw = Stopwatch.StartNew();

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

    // FFmpeg encoder with NV12 input
    private readonly int _targetKbps;
    private readonly string? _ffmpegExe;
    private FfmpegPipeEncoder? _enc;
    private int _encW, _encH;

    // D3D11 Video Processor for BGRA->NV12 GPU conversion
    private D3D11VideoProcessorGpu? _videoProcessor;
    private ID3D11Device? _d3dDevice;
    private bool _ownsDevice = false;

    private volatile bool _canSend = false;

    private record struct TextureFrame(ID3D11Texture2D Texture, int Width, int Height, ID3D11Device? SourceDevice);

    public bool IsZeroCopyEnabled => _videoProcessor?.IsAvailable == true;

    public WebRTCStreamer_FFmpegNV12(int fps = 30, int targetKbps = 6000, string? ffmpegExe = null)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
        _targetKbps = Math.Max(500, targetKbps);
        _ffmpegExe = ffmpegExe;
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(ProcessLoop);
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
        Console.WriteLine("[RTC-NV12] PeerConnection created");

        _pc.onconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-NV12] pc.state = {st}");
            if (st == RTCPeerConnectionState.disconnected ||
                st == RTCPeerConnectionState.failed ||
                st == RTCPeerConnectionState.closed)
            {
                try { _cts?.Cancel(); } catch { }
                _running = false;
                OnPeerDisconnected?.Invoke();
            }
        };

        _pc.onicegatheringstatechange += st => Console.WriteLine($"[RTC-NV12] ice gathering = {st}");

        _pc.onicecandidate += cand =>
        {
            if (cand != null)
                Console.WriteLine($"[RTC-NV12] ice candidate: {cand.type} {cand.address}:{cand.port}");
        };

        _pc.oniceconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-NV12] ice connection = {st}");
            if (st == RTCIceConnectionState.connected)
            {
                Console.WriteLine("[RTC-NV12] ICE CONNECTED - enabling send");
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
            Console.WriteLine("NEGOTIATED VIDEO FORMATS (FFmpeg-NV12):");
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
                    Console.WriteLine($"[RTC-NV12] enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)} zeroCopy:{IsZeroCopyEnabled}");
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // SDP negotiation
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        var sdp = answer.sdp.Replace("UDP/TLS/RTP/SAVP", "UDP/TLS/RTP/SAVPF");
        return sdp;
    }

    /// <summary>
    /// Push D3D11 texture for encoding. Uses GPU Video Processor for BGRA->NV12 conversion.
    /// </summary>
    public void PushTexture(ID3D11Texture2D texture, int width, int height, ID3D11Device? sourceDevice = null)
    {
        if (!_running || _cts?.IsCancellationRequested == true) return;

        long now = _gateSw.ElapsedMilliseconds;
        if (now - _lastEnqMs < _minIntervalMs - 1) return;
        _lastEnqMs = now;

        if (_textureChan.Writer.TryWrite(new TextureFrame(texture, width, height, sourceDevice)))
        {
            Interlocked.Increment(ref _enq);
        }
    }

    private void EnsureEncoder(int w, int h, ID3D11Device? sourceDevice = null)
    {
        if (_enc != null && w == _encW && h == _encH) return;

        // Cleanup old
        _enc?.Dispose();
        _videoProcessor?.Dispose();
        if (_ownsDevice && _d3dDevice != null)
        {
            try { _d3dDevice.Dispose(); } catch { }
        }
        _d3dDevice = null;
        _ownsDevice = false;

        _encW = w;
        _encH = h;

        // Use source device if provided, otherwise create new
        if (sourceDevice != null)
        {
            _d3dDevice = sourceDevice;
            _ownsDevice = false;
            Console.WriteLine($"[RTC-NV12] Using source D3D11 device");
        }
        else
        {
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
            _ownsDevice = true;
            Console.WriteLine($"[RTC-NV12] Created D3D11 device for encoder");
        }

        // Create Video Processor for GPU BGRA->NV12 conversion
        _videoProcessor = new D3D11VideoProcessorGpu(_d3dDevice, w, h);

        // Create FFmpeg encoder with NV12 input mode
        _enc = new FfmpegPipeEncoder(w, h, _fps, _targetKbps, -1, "veryfast", true, _ffmpegExe, 
            FfmpegPipeEncoder.InputFormat.NV12);
        _enc.OnEncodedAccessUnit += (durMs, au) =>
        {
            try { _auChan.Writer.TryWrite((durMs, au)); }
            catch { }
        };
        _enc.Start();

        // Start AU sender task
        if (_sendAuTask == null)
            _sendAuTask = Task.Run(SendAuLoop);

        Console.WriteLine($"[RTC-NV12] Encoder: {w}x{h}@{_fps}fps {_targetKbps}kbps NV12-mode ZeroCopy={_videoProcessor.IsAvailable}");
    }

    private async Task ProcessLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TextureFrame texFrame;
                try
                {
                    texFrame = await _textureChan.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException) { break; }

                Interlocked.Increment(ref _deq);

                // Drop to latest
                while (_textureChan.Reader.TryRead(out var newer))
                {
                    Interlocked.Increment(ref _deq);
                    texFrame = newer;
                }

                try
                {
                    EnsureEncoder(texFrame.Width, texFrame.Height, texFrame.SourceDevice);

                    if (_videoProcessor != null && _videoProcessor.IsAvailable && _enc != null)
                    {
                        // GPU path: BGRA texture -> NV12 (GPU) -> Copy to CPU -> FFmpeg
                        var nv12Data = _videoProcessor.ProcessBgraToNv12Cpu(texFrame.Texture);
                        if (nv12Data != null)
                        {
                            uint durMs = (uint)(1000 / _fps);
                            _enc.PushNV12FrameContiguous(nv12Data, durMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RTC-NV12] Encode error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[RTC-NV12] ProcessLoop error: {ex.Message}");
        }
    }

    private async Task SendAuLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            await foreach (var (durMs, au) in _auChan.Reader.ReadAllAsync(ct))
            {
                if (!_canSend || _pc == null) continue;

                // Check for IDR/SPS/PPS
                var (hasIdr, hasSps, hasPps) = ScanNalTypes(au, out int spsPos, out int ppsPos);

                if (hasIdr && (!hasSps || !hasPps))
                {
                    // Need to prepend SPS/PPS
                    // For simplicity, just log warning - encoder should include SPS/PPS with IDR
                }

                if (hasIdr)
                    Console.WriteLine($"[H264-NV12] AU has IDR (SPS={hasSps}, PPS={hasPps})");

                if ((Interlocked.Read(ref _sent) % 30) == 0)
                    Console.WriteLine($"[SendAuLoop-NV12] AU received={Interlocked.Read(ref _sent) + 1}, _canSend={_canSend}");

                // Send
                _pc.SendVideo(durMs, au);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[RTC-NV12] SendAuLoop error: {ex.Message}");
        }
    }

    private static (bool hasIdr, bool hasSps, bool hasPps) ScanNalTypes(byte[] au, out int spsPos, out int ppsPos)
    {
        spsPos = ppsPos = -1;
        bool idr = false, sps = false, pps = false;
        int i = 0;
        while (i + 3 < au.Length)
        {
            int sc = (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) ? 4 :
                     (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ? 3 : 0;
            if (sc == 0) { i++; continue; }
            int nalStart = i + sc;
            int nalType = (nalStart < au.Length) ? (au[nalStart] & 0x1F) : -1;
            if (nalType == 7) { sps = true; spsPos = i; }
            else if (nalType == 8) { pps = true; ppsPos = i; }
            else if (nalType == 5) idr = true;
            i = nalStart + 1;
        }
        return (idr, sps, pps);
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        _textureChan.Writer.TryComplete();
        _auChan.Writer.TryComplete();
        
        try { _enc?.Dispose(); } catch { }
        try { _videoProcessor?.Dispose(); } catch { }
        if (_ownsDevice) try { _d3dDevice?.Dispose(); } catch { }
        try { _pc?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        _enc = null;
        _videoProcessor = null;
        _d3dDevice = null;
        _pc = null;
        _cts = null;
    }

    public void Dispose() => Stop();
}
