#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// WebRTC streamer with true zero-copy GPU pipeline.
/// Pipeline: DXGI Capture -> D3D11 Video Processor (BGRA->NV12) -> MF Encoder -> WebRTC
/// No CPU memory copy for frame data.
/// </summary>
public class WebRTCStreamer_ZeroCopy : IDisposable
{
    private RTCPeerConnection? _pc;
    private Task? _statsTask;
    private CancellationTokenSource? _cts;

    private volatile bool _running = false;
    public bool IsRunning => _running;
    public event Action? OnPeerDisconnected;

    // Frame input
    private readonly int _fps;
    private readonly int _minIntervalMs;

    // Texture queue for zero-copy
    private readonly Channel<TextureFrame> _textureChan =
        Channel.CreateBounded<TextureFrame>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    // Fallback: CPU buffer queue
    private readonly Channel<CpuFrame> _cpuChan =
        Channel.CreateBounded<CpuFrame>(new BoundedChannelOptions(3)
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
    private Task? _encodeTask;

    // Encoder and video processor
    private readonly int _targetKbps;
    private MediaFoundationH264Encoder? _encoder;
    private D3D11VideoProcessorGpu? _videoProcessor;
    private ID3D11Device? _d3dDevice;
    private bool _ownsDevice = false; // Track if we own the device (should dispose) or not (external device)
    private int _encW, _encH;
    private volatile bool _canSend = false;
    private bool _useZeroCopy = false;

    // Frame structures
    private record struct TextureFrame(ID3D11Texture2D Texture, int Width, int Height, ID3D11Device? SourceDevice);
    private record struct CpuFrame(byte[] Buffer, int Width, int Height, int Stride, bool IsPooled);

    public bool IsZeroCopyEnabled => _useZeroCopy;

    public WebRTCStreamer_ZeroCopy(int fps = 30, int targetKbps = 6000)
    {
        _fps = Math.Max(5, fps);
        _minIntervalMs = Math.Max(1, 1000 / _fps);
        _targetKbps = Math.Max(500, targetKbps);
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
        Console.WriteLine("[RTC-ZC] PeerConnection created");

        _pc.onconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-ZC] pc.state = {st}");
            if (st == RTCPeerConnectionState.disconnected ||
                st == RTCPeerConnectionState.failed ||
                st == RTCPeerConnectionState.closed)
            {
                try { _cts?.Cancel(); } catch { }
                _running = false;
                OnPeerDisconnected?.Invoke();
            }
        };

        _pc.onicegatheringstatechange += st => Console.WriteLine($"[RTC-ZC] ice gathering = {st}");

        _pc.onicecandidate += cand =>
        {
            if (cand != null)
                Console.WriteLine($"[RTC-ZC] ice candidate: {cand.type} {cand.address}:{cand.port}");
        };

        _pc.oniceconnectionstatechange += st =>
        {
            Console.WriteLine($"[RTC-ZC] ice connection = {st}");
            if (st == RTCIceConnectionState.connected)
            {
                Console.WriteLine("[RTC-ZC] ICE CONNECTED - enabling send");
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
            Console.WriteLine("NEGOTIATED VIDEO FORMATS (ZC):");
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
                    Console.WriteLine($"[RTC-ZC] enq:{Interlocked.Read(ref _enq)} deq:{Interlocked.Read(ref _deq)} sent:{Interlocked.Read(ref _sent)} zeroCopy:{_useZeroCopy}");
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
    /// Push D3D11 texture directly (zero-copy path)
    /// </summary>
    public void PushTexture(ID3D11Texture2D texture, int width, int height, ID3D11Device? sourceDevice = null)
    {
        if (!_running || _cts?.IsCancellationRequested == true) return;

        long now = _gateSw.ElapsedMilliseconds;
        if (now - _lastEnqMs < _minIntervalMs - 1) return;
        _lastEnqMs = now;

        // For zero-copy, we need to copy the texture to our own texture
        // because the source texture may be released immediately after this call
        // This is still faster than copying to CPU memory
        
        if (_textureChan.Writer.TryWrite(new TextureFrame(texture, width, height, sourceDevice)))
        {
            Interlocked.Increment(ref _enq);
        }
    }

    /// <summary>
    /// Push BGRA frame from CPU memory (fallback path)
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

        var frame = new CpuFrame(buf, width, height, stride, true);
        if (_cpuChan.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _enq);
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Task.CompletedTask;
    }

    private void EnsureEncoder(int w, int h, ID3D11Device? sourceDevice = null)
    {
        if (_encoder != null && w == _encW && h == _encH) return;

        // Cleanup old
        _encoder?.Dispose();
        _videoProcessor?.Dispose();
        // Only dispose device if we own it
        if (_ownsDevice && _d3dDevice != null)
        {
            try { _d3dDevice.Dispose(); } catch { }
        }
        _d3dDevice = null;
        _ownsDevice = false;

        _encW = w;
        _encH = h;

        // Use source device if provided (for zero-copy), otherwise create new device
        if (sourceDevice != null)
        {
            _d3dDevice = sourceDevice;
            _ownsDevice = false; // We don't own this device
            Console.WriteLine($"[RTC-ZC] Using source D3D11 device for zero-copy");
        }
        else
        {
            // Create D3D11 device
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
            _ownsDevice = true; // We own this device

            Console.WriteLine($"[RTC-ZC] Created D3D11 device for encoder");
        }

        // Create MF encoder
        _encoder = new MediaFoundationH264Encoder(_d3dDevice, w, h, _fps, _targetKbps);
        _encoder.OnEncodedSample += (data, durMs) =>
        {
            try { _auChan.Writer.TryWrite((durMs, data)); }
            catch { }
        };
        _encoder.Start();

        // Create video processor for BGRA->NV12 conversion
        _videoProcessor = new D3D11VideoProcessorGpu(_d3dDevice, w, h);
        _useZeroCopy = _videoProcessor.IsAvailable;

        // Start AU sender task
        if (_sendAuTask == null)
            _sendAuTask = Task.Run(SendAuLoop);

        Console.WriteLine($"[RTC-ZC] Encoder: {w}x{h}@{_fps}fps {_targetKbps}kbps HW={_encoder.IsHardwareEncoder} ZeroCopy={_useZeroCopy}");
    }

    private async Task ProcessLoop()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Try to read from texture channel first (zero-copy path)
                // Then fall back to CPU channel
                
                bool processed = false;

                // Check texture channel
                if (_textureChan.Reader.TryRead(out var texFrame))
                {
                    Interlocked.Increment(ref _deq);
                    
                    // Drop to latest
                    while (_textureChan.Reader.TryRead(out var newer))
                    {
                        Interlocked.Increment(ref _deq);
                        texFrame = newer;
                    }

                    try
                    {
                        // Pass source device for zero-copy (same device for texture and video processor)
                        EnsureEncoder(texFrame.Width, texFrame.Height, texFrame.SourceDevice);
                        
                        // Check if encoder supports GPU texture path
                        bool useGpuPath = _useZeroCopy && _videoProcessor != null && _encoder != null && _encoder.IsGpuTexturePath;
                        
                        if (useGpuPath)
                        {
                            // Zero-copy path: BGRA texture -> NV12 texture -> Encoder (GPU)
                            var nv12Tex = _videoProcessor.ProcessBgraToNv12(texFrame.Texture);
                            if (nv12Tex != null)
                            {
                                // Try GPU encode, fallback to CPU if it fails
                                if (!_encoder.EncodeNv12Texture(nv12Tex))
                                {
                                    // GPU path failed, use CPU fallback
                                    _encoder.EncodeTexture(texFrame.Texture);
                                }
                            }
                        }
                        else if (_encoder != null)
                        {
                            // CPU path: copy BGRA to CPU, convert to NV12, encode
                            _encoder.EncodeTexture(texFrame.Texture);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RTC-ZC] Texture encode error: {ex.Message}");
                    }

                    processed = true;
                }

                // Check CPU channel
                if (!processed && _cpuChan.Reader.TryRead(out var cpuFrame))
                {
                    Interlocked.Increment(ref _deq);
                    
                    // Drop to latest
                    while (_cpuChan.Reader.TryRead(out var newer))
                    {
                        Interlocked.Increment(ref _deq);
                        if (cpuFrame.IsPooled) ArrayPool<byte>.Shared.Return(cpuFrame.Buffer);
                        cpuFrame = newer;
                    }

                    try
                    {
                        EnsureEncoder(cpuFrame.Width, cpuFrame.Height);
                        _encoder?.EncodeBgraFrame(cpuFrame.Buffer.AsSpan(0, cpuFrame.Stride * cpuFrame.Height), cpuFrame.Stride);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RTC-ZC] CPU encode error: {ex.Message}");
                    }
                    finally
                    {
                        if (cpuFrame.IsPooled) ArrayPool<byte>.Shared.Return(cpuFrame.Buffer);
                    }

                    processed = true;
                }

                if (!processed)
                {
                    // Wait for input
                    await Task.Delay(1, ct);
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
                    Console.WriteLine($"[SendAuLoop-ZC] AU received={auReceived}, _canSend={_canSend}");
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
                    var processedAu = EnsureAnnexB(item.au);
                    _pc.SendVideo(rtpStep, processedAu);
                    Interlocked.Increment(ref _sent);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static byte[] EnsureAnnexB(byte[] data)
    {
        if (data.Length >= 4)
        {
            bool hasStartCode = (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) ||
                               (data[0] == 0 && data[1] == 0 && data[2] == 1);
            if (hasStartCode) return data;
        }

        var result = new byte[4 + data.Length];
        result[0] = 0; result[1] = 0; result[2] = 0; result[3] = 1;
        Buffer.BlockCopy(data, 0, result, 4, data.Length);
        return result;
    }

    public Task StopAsync()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        _sendAuTask = null;
        try { _textureChan.Writer.TryComplete(); } catch { }
        try { _cpuChan.Writer.TryComplete(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _sendAuTask?.Wait(100); } catch { }
        _sendAuTask = null;
        try { _statsTask?.Wait(200); } catch { }

        try { _encoder?.Dispose(); } catch { }
        try { _videoProcessor?.Dispose(); } catch { }
        // Only dispose device if we own it
        if (_ownsDevice && _d3dDevice != null)
        {
            try { _d3dDevice.Dispose(); } catch { }
        }
        _d3dDevice = null;

        try { _pc?.Close("dispose"); _pc?.Dispose(); } catch { }
    }
}
