#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// WebRTC streamer using native AmfWrapper.dll for true zero-copy encoding on AMD GPUs.
/// This provides the lowest latency path by encoding D3D11 textures directly.
/// </summary>
public class WebRTCStreamer_AmfNative : IDisposable
{
    private readonly int _fps;
    private readonly int _kbps;
    private ID3D11Device? _device;
    
    private AmfNativeWrapper? _encoder;
    private RTCPeerConnection? _pc;
    private MediaStreamTrack? _videoTrack;
    
    private readonly ConcurrentQueue<(byte[] nv12, int w, int h)> _nv12Queue = new();
    
    private CancellationTokenSource? _cts;
    private Task? _encodeTask;
    
    private bool _running;
    private bool _disposed;
    private long _enqueueCount;
    private long _sentCount;
    private int _width;
    private int _height;
    
    // Staging texture for zero-copy path to avoid race condition
    private ID3D11Texture2D? _stagingNV12;
    private ID3D11DeviceContext? _deviceContext;
    
    private readonly object _lock = new();

    public bool IsRunning => _running;
    public bool UseNV12Input => true;
    
    public event Action? OnPeerDisconnected;
    public event Action<string>? OnIceCandidate;

    public WebRTCStreamer_AmfNative(int fps, int kbps, ID3D11Device? device = null)
    {
        _fps = fps;
        _kbps = kbps;
        _device = device;
        Console.WriteLine($"[RTC-AmfNative] Created: {fps}fps, {kbps}kbps");
    }

    public void SetDevice(ID3D11Device device)
    {
        _device = device;
    }

    public async Task StartAsync()
    {
        Console.WriteLine("[RTC-AmfNative] StartAsync called");
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Stop();
        await Task.CompletedTask;
    }

    public void AddIceCandidate(string candidate)
    {
        if (_pc == null) return;
        try
        {
            // Parse candidate string and add to PeerConnection
            var init = new RTCIceCandidateInit { candidate = candidate, sdpMLineIndex = 0, sdpMid = "0" };
            _pc.addIceCandidate(init);
            Console.WriteLine($"[RTC-AmfNative] Added remote ICE: {candidate.Substring(0, Math.Min(50, candidate.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RTC-AmfNative] AddIceCandidate error: {ex.Message}");
        }
    }

    public async Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        if (_running) throw new InvalidOperationException("Already running");
        
        Console.WriteLine("[RTC-AmfNative] Processing offer");
        
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };
        
        _pc = new RTCPeerConnection(cfg);
        Console.WriteLine("[RTC-AmfNative] PeerConnection created");

        // Create H264 video track
        var h264 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: 0,
            name: "H264",
            clockRate: 90000,
            channels: 0,
            fmtp: "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f");
        
        _videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { h264 },
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(_videoTrack);
        
        Console.WriteLine($"[RTC-AmfNative] canSend(H264) = {_pc.VideoLocalTrack != null}");

        // Forward local ICE candidates to client
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Console.WriteLine($"[RTC-AmfNative] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                Console.WriteLine("[RTC-AmfNative] ICE gathering complete");
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        _pc.oniceconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[RTC-AmfNative] ice = {state}");
            if (state == RTCIceConnectionState.connected)
            {
                Console.WriteLine("[RTC-AmfNative] ICE CONNECTED");
            }
            else if (state == RTCIceConnectionState.disconnected || state == RTCIceConnectionState.failed)
            {
                OnPeerDisconnected?.Invoke();
            }
        };
        
        _pc.onconnectionstatechange += (state) =>
        {
            Console.WriteLine($"[RTC-AmfNative] pc.state = {state}");
        };

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        _running = true;
        _cts = new CancellationTokenSource();
        _encodeTask = Task.Run(() => EncodeLoop(_cts.Token));
        
        // Stats logging task for debugging
        _ = Task.Run(async () =>
        {
            while (_running && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000);
                Console.WriteLine($"[RTC-AmfNative] stats: enq={_enqueueCount} sent={_sentCount}");
            }
        });
        
        return answer.sdp ?? "";
    }

    public async Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
    {
        Console.WriteLine("[RTC-AmfNative] BGRA input not supported - use NV12 or texture");
        await Task.CompletedTask;
    }

    public async Task PushNV12BytesAsync(byte[] src, int width, int height)
    {
        if (!_running || _disposed) return;
        
        _width = width;
        _height = height;
        
        byte[] copy = new byte[src.Length];
        Buffer.BlockCopy(src, 0, copy, 0, src.Length);
        
        _nv12Queue.Enqueue((copy, width, height));
        Interlocked.Increment(ref _enqueueCount);
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// TRUE ZERO-COPY: Encode directly from NV12 GPU texture
    /// Uses staging texture copy to avoid race condition with capture pipeline
    /// </summary>
    public void PushTexture(ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed) return;
        
        lock (_lock)
        {
            if (_encoder == null)
            {
                InitializeEncoder(width, height);
            }
            
            if (_encoder != null && _device != null)
            {
                // Create staging texture on first use or if size changed
                if (_stagingNV12 == null || _width != width || _height != height)
                {
                    _stagingNV12?.Dispose();
                    _deviceContext = _device.ImmediateContext;
                    
                    var desc = new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.NV12,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    };
                    _stagingNV12 = _device.CreateTexture2D(desc);
                    _width = width;
                    _height = height;
                    Console.WriteLine($"[RTC-AmfNative] Created staging NV12 texture {width}x{height}");
                }
                
                // Copy source texture to staging (prevents race condition)
                _deviceContext?.CopyResource(_stagingNV12, nv12Texture);
                
                // Encode from staging texture
                bool forceIdr = (Interlocked.Read(ref _sentCount) == 0);
                _encoder.EncodeTexture(_stagingNV12, forceKeyframe: forceIdr);
            }
        }
    }

    public void Stop()
    {
        if (!_running && _pc == null) return;
        Console.WriteLine("[RTC-AmfNative] Stopping...");
        
        _running = false;
        _cts?.Cancel();
        
        try { _encodeTask?.Wait(1000); } catch { }
        
        _pc?.close();
        _pc = null;
        
        _encoder?.Dispose();
        _encoder = null;
        
        _stagingNV12?.Dispose();
        _stagingNV12 = null;
        
        Console.WriteLine("[RTC-AmfNative] Stopped");
    }

    private void InitializeEncoder(int width, int height)
    {
        if (_encoder != null) return;
        if (_device == null)
        {
            Console.WriteLine("[RTC-AmfNative] No D3D11 device - cannot use zero-copy encoder");
            return;
        }
        
        _encoder = new AmfNativeWrapper();
        _encoder.OnEncodedData += OnEncodedData;
        
        if (!_encoder.Initialize(width, height, _fps, _kbps, _device))
        {
            Console.WriteLine("[RTC-AmfNative] Encoder initialization failed!");
            _encoder.Dispose();
            _encoder = null;
        }
        else
        {
            Console.WriteLine($"[RTC-AmfNative] Encoder started {width}x{height} @ {_fps}fps (zero-copy)");
        }
    }

    private void OnEncodedData(byte[] nalData, bool isKeyframe, long pts)
    {
        if (!_running || _pc == null) return;
        
        _pc.SendVideo((uint)(90000 / _fps), nalData);
        long sent = Interlocked.Increment(ref _sentCount);
        
        // Debug: log first 5 frames and keyframes
        if (sent <= 5 || isKeyframe)
        {
            Console.WriteLine($"[RTC-AmfNative] Frame #{sent}: {nalData.Length} bytes, keyframe={isKeyframe}");
        }
    }

    private void EncodeLoop(CancellationToken ct)
    {
        bool firstFrame = true;
        
        while (!ct.IsCancellationRequested && _running)
        {
            while (_nv12Queue.TryDequeue(out var item))
            {
                lock (_lock)
                {
                    if (_encoder == null)
                    {
                        InitializeEncoder(item.w, item.h);
                    }
                    
                    if (_encoder != null)
                    {
                        // Force IDR only on first frame
                        _encoder.EncodeNV12Bytes(item.nv12, forceKeyframe: firstFrame);
                        firstFrame = false;
                    }
                }
            }
            
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Stop();
        Console.WriteLine("[RTC-AmfNative] Disposed");
    }
}

/// <summary>
/// Wrapper for WebRTCStreamer_AmfNative that implements IWebRTCStreamer
/// </summary>
public class WebRTCStreamerAmfNativeWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_AmfNative _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public bool UseNV12Input => _streamer.UseNV12Input;
    public bool UseTextureInput => true; // Native AMF supports TRUE zero-copy!
    
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public event Action<string>? OnIceCandidate
    {
        add => _streamer.OnIceCandidate += value;
        remove => _streamer.OnIceCandidate -= value;
    }

    public WebRTCStreamerAmfNativeWrapper(int fps, int kbps, ID3D11Device? device = null)
    {
        _streamer = new WebRTCStreamer_AmfNative(fps, kbps, device);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync() => _streamer.StopAsync();
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => _streamer.PushBgraBytesAsync(src, width, height, stride);
    public Task PushNV12BytesAsync(byte[] src, int width, int height)
        => _streamer.PushNV12BytesAsync(src, width, height);
    public void PushTexture(ID3D11Texture2D nv12Texture, int width, int height)
        => _streamer.PushTexture(nv12Texture, width, height);
    public void SetDevice(ID3D11Device device) => _streamer.SetDevice(device);
    public void AddIceCandidate(string candidate) => _streamer.AddIceCandidate(candidate);
    public void Dispose() => _streamer.Dispose();
}
