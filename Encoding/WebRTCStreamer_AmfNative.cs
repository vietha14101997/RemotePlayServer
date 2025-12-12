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
    
    private readonly object _lock = new();

    public bool IsRunning => _running;
    public bool UseNV12Input => true;
    
    public event Action? OnPeerDisconnected;

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
        
        // Pure send - no logging overhead
        _pc.SendVideo((uint)(90000 / _fps), nalData);
        Interlocked.Increment(ref _sentCount);
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
    
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
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
    public void SetDevice(ID3D11Device device) => _streamer.SetDevice(device);
    public void Dispose() => _streamer.Dispose();
}
