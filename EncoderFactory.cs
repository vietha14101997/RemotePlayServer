#nullable enable
using System;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Encoder type selection
/// </summary>
public enum EncoderType
{
    Auto,           // Auto-detect best encoder
    MediaFoundation, // Use Media Foundation
    MediaFoundationZeroCopy, // Use Media Foundation with zero-copy GPU pipeline
    FFmpeg,         // Use FFmpeg pipe (BGRA input)
    FFmpegNV12      // Use FFmpeg with GPU Video Processor (NV12 input - best for AMD)
}

/// <summary>
/// Factory for creating the appropriate encoder based on system capabilities
/// </summary>
public static class EncoderFactory
{
    private static EncoderType _preferredEncoder = EncoderType.Auto;
    private static bool _mfAvailable = false;
    private static bool _mfChecked = false;
    private static readonly object _lock = new();

    public static EncoderType PreferredEncoder
    {
        get => _preferredEncoder;
        set => _preferredEncoder = value;
    }

    /// <summary>
    /// Check if Media Foundation hardware encoder is available.
    /// </summary>
    public static bool IsMediaFoundationAvailable()
    {
        lock (_lock)
        {
            if (_mfChecked) return _mfAvailable;
            _mfChecked = true;

            try
            {
                // Try to create a test encoder
                var levels = new FeatureLevel[] { FeatureLevel.Level_11_0 };
                ID3D11DeviceContext ctx;
                D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    levels,
                    out var device,
                    out ctx
                );
                ctx?.Dispose();

                using (device)
                {
                    using var testEncoder = new MediaFoundationH264Encoder(device, 640, 480, 30, 2000);
                    _mfAvailable = testEncoder.IsHardwareEncoder;
                    Console.WriteLine($"[EncoderFactory] Media Foundation check: HW={_mfAvailable}, Encoder={testEncoder.EncoderName}");
                }
                return _mfAvailable;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EncoderFactory] Media Foundation not available: {ex.Message}");
                _mfAvailable = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Get the best encoder type for current system
    /// </summary>
    public static EncoderType GetBestEncoderType()
    {
        if (_preferredEncoder != EncoderType.Auto)
            return _preferredEncoder;

        // Auto-detect: prefer MF if hardware encoder available
        if (IsMediaFoundationAvailable())
            return EncoderType.MediaFoundation;

        return EncoderType.FFmpeg;
    }

    /// <summary>
    /// Create a WebRTC streamer with the appropriate encoder
    /// </summary>
    public static IWebRTCStreamer CreateStreamer(int fps, int kbps, int crf, string preset, bool zerolatency)
    {
        var encoderType = GetBestEncoderType();
        Console.WriteLine($"[EncoderFactory] Creating streamer with encoder: {encoderType}");

        switch (encoderType)
        {
            case EncoderType.MediaFoundationZeroCopy:
                try
                {
                    return new WebRTCStreamerZeroCopyWrapper(fps, kbps);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncoderFactory] ZeroCopy streamer failed, trying MF: {ex.Message}");
                    goto case EncoderType.MediaFoundation;
                }

            case EncoderType.MediaFoundation:
                try
                {
                    return new WebRTCStreamerMFWrapper(fps, kbps);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncoderFactory] MF streamer failed, falling back to FFmpeg: {ex.Message}");
                    goto case EncoderType.FFmpeg;
                }

            case EncoderType.FFmpeg:
            default:
                return new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolatency);
        }
    }
}

/// <summary>
/// Common interface for WebRTC streamers
/// </summary>
public interface IWebRTCStreamer : IDisposable
{
    bool IsRunning { get; }
    event Action? OnPeerDisconnected;
    Task StartAsync();
    Task StopAsync();
    Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp);
    Task PushBgraBytesAsync(byte[] src, int width, int height, int stride);
}

/// <summary>
/// Wrapper for Media Foundation streamer
/// </summary>
public class WebRTCStreamerMFWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_MF _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public WebRTCStreamerMFWrapper(int fps, int kbps)
    {
        _streamer = new WebRTCStreamer_MF(fps, kbps);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync() => _streamer.StopAsync();
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => _streamer.PushBgraBytesAsync(src, width, height, stride);
    public void Dispose() => _streamer.Dispose();
}

/// <summary>
/// Wrapper for FFmpeg streamer (existing WebRTCStreamer_H264)
/// </summary>
public class WebRTCStreamerFFmpegWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_H264 _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public WebRTCStreamerFFmpegWrapper(int fps, int kbps, int crf, string preset, bool zerolatency)
    {
        _streamer = new WebRTCStreamer_H264(fps, kbps, crf, preset, zerolatency);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync() => _streamer.StopAsync();
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => _streamer.PushBgraBytesAsync(src, width, height, stride);
    public void Dispose() => _streamer.Dispose();
}

/// <summary>
/// Wrapper for Zero-Copy streamer (GPU pipeline)
/// </summary>
public class WebRTCStreamerZeroCopyWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_ZeroCopy _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public bool IsZeroCopyEnabled => _streamer.IsZeroCopyEnabled;

    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public WebRTCStreamerZeroCopyWrapper(int fps, int kbps)
    {
        _streamer = new WebRTCStreamer_ZeroCopy(fps, kbps);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync() => _streamer.StopAsync();
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => _streamer.PushBgraBytesAsync(src, width, height, stride);
    
    /// <summary>
    /// Push D3D11 texture directly (zero-copy GPU path)
    /// </summary>
    public void PushTexture(Vortice.Direct3D11.ID3D11Texture2D texture, int width, int height, Vortice.Direct3D11.ID3D11Device? sourceDevice = null)
        => _streamer.PushTexture(texture, width, height, sourceDevice);
    
    public void Dispose() => _streamer.Dispose();
}

/// <summary>
/// Wrapper for FFmpeg NV12 streamer (GPU Video Processor + FFmpeg h264_amf)
/// Best for AMD GPUs where MF encoder doesn't work
/// </summary>
public class WebRTCStreamerFFmpegNV12Wrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_FFmpegNV12 _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public bool IsZeroCopyEnabled => _streamer.IsZeroCopyEnabled;

    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public WebRTCStreamerFFmpegNV12Wrapper(int fps, int kbps)
    {
        _streamer = new WebRTCStreamer_FFmpegNV12(fps, kbps);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync()
    {
        _streamer.Stop();
        return Task.CompletedTask;
    }
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    
    // CPU path - not used for this encoder (needs texture input)
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => Task.CompletedTask; // No-op, this encoder requires texture input
    
    /// <summary>
    /// Push D3D11 texture directly (GPU path with NV12 conversion)
    /// </summary>
    public void PushTexture(Vortice.Direct3D11.ID3D11Texture2D texture, int width, int height, Vortice.Direct3D11.ID3D11Device? sourceDevice = null)
        => _streamer.PushTexture(texture, width, height, sourceDevice);
    
    public void Dispose() => _streamer.Dispose();
}
