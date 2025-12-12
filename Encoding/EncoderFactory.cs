#nullable enable
using System;
using System.Threading.Tasks;
using Vortice.Direct3D11;

/// <summary>
/// Encoder mode selection
/// </summary>
public enum EncoderMode
{
    /// <summary>FFmpeg via pipe (ffmpeg.exe process)</summary>
    FfmpegPipe,
    /// <summary>FFmpeg in-process via libavcodec (lower latency)</summary>
    LibAv
}

/// <summary>
/// Factory for creating WebRTC streamers
/// </summary>
public static class EncoderFactory
{
    /// <summary>
    /// Create a WebRTC streamer using specified encoder mode
    /// </summary>
    public static IWebRTCStreamer CreateStreamer(
        int fps, 
        int kbps, 
        EncoderMode mode = EncoderMode.FfmpegPipe,
        ID3D11Device? device = null,
        int crf = 23, 
        string preset = "p1", 
        bool zerolatency = true)
    {
        Console.WriteLine($"[EncoderFactory] Creating streamer: mode={mode}, fps={fps}, kbps={kbps}");
        
        return mode switch
        {
            EncoderMode.LibAv => new WebRTCStreamerLibAvWrapper(fps, kbps, device),
            _ => new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolatency, useNV12: true)
        };
    }
    
    /// <summary>
    /// Create FFmpeg pipe streamer (legacy method)
    /// </summary>
    public static IWebRTCStreamer CreateStreamer(int fps, int kbps, int crf, string preset, bool zerolatency, bool useNV12 = true)
    {
        Console.WriteLine($"[EncoderFactory] Creating FFmpeg pipe streamer (NV12={useNV12})");
        return new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolatency, useNV12);
    }
}

/// <summary>
/// Common interface for WebRTC streamers
/// </summary>
public interface IWebRTCStreamer : IDisposable
{
    bool IsRunning { get; }
    bool UseNV12Input { get; }
    event Action? OnPeerDisconnected;
    Task StartAsync();
    Task StopAsync();
    Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp);
    Task PushBgraBytesAsync(byte[] src, int width, int height, int stride);
    Task PushNV12BytesAsync(byte[] src, int width, int height);
    void SetDevice(ID3D11Device device);
}

/// <summary>
/// Wrapper for FFmpeg pipe streamer (WebRTCStreamer_H264)
/// </summary>
public class WebRTCStreamerFFmpegWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_H264 _streamer;
    private readonly bool _useNV12;

    public bool IsRunning => _streamer.IsRunning;
    public bool UseNV12Input => _useNV12;
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public WebRTCStreamerFFmpegWrapper(int fps, int kbps, int crf, string preset, bool zerolatency, bool useNV12 = true)
    {
        _useNV12 = useNV12;
        _streamer = new WebRTCStreamer_H264(fps, kbps, crf, preset, zerolatency, null, useNV12);
    }

    public Task StartAsync() => _streamer.StartAsync();
    public Task StopAsync() => _streamer.StopAsync();
    public Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp)
        => _streamer.SetRemoteOfferAndCreateAnswerAsync(offerSdp);
    public Task PushBgraBytesAsync(byte[] src, int width, int height, int stride)
        => _streamer.PushBgraBytesAsync(src, width, height, stride);
    public Task PushNV12BytesAsync(byte[] src, int width, int height)
        => _streamer.PushNV12BytesAsync(src, width, height);
    public void SetDevice(ID3D11Device device) { /* Not needed for pipe mode */ }
    public void Dispose() => _streamer.Dispose();
}

/// <summary>
/// Wrapper for LibAv in-process streamer (WebRTCStreamer_LibAv)
/// </summary>
public class WebRTCStreamerLibAvWrapper : IWebRTCStreamer
{
    private readonly WebRTCStreamer_LibAv _streamer;

    public bool IsRunning => _streamer.IsRunning;
    public bool UseNV12Input => true; // LibAv always uses NV12
    public event Action? OnPeerDisconnected
    {
        add => _streamer.OnPeerDisconnected += value;
        remove => _streamer.OnPeerDisconnected -= value;
    }

    public WebRTCStreamerLibAvWrapper(int fps, int kbps, ID3D11Device? device = null)
    {
        _streamer = new WebRTCStreamer_LibAv(fps, kbps, device);
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
