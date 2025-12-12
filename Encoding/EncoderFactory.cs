#nullable enable
using System;
using System.Threading.Tasks;

/// <summary>
/// Factory for creating FFmpeg-based WebRTC streamer
/// </summary>
public static class EncoderFactory
{
    /// <summary>
    /// Create a WebRTC streamer using FFmpeg encoder
    /// </summary>
    public static IWebRTCStreamer CreateStreamer(int fps, int kbps, int crf, string preset, bool zerolatency)
    {
        Console.WriteLine("[EncoderFactory] Creating FFmpeg streamer");
        return new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolatency);
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
/// Wrapper for FFmpeg streamer (WebRTCStreamer_H264)
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
