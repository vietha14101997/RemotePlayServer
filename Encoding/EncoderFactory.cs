#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RemotePlayServer.Encoding;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Encoder mode selection
/// </summary>
public enum EncoderMode
{
    /// <summary>FFmpeg via pipe (ffmpeg.exe process)</summary>
    FfmpegPipe,
    /// <summary>FFmpeg in-process via libavcodec (hardware-accelerated)</summary>
    LibAv
}

/// <summary>
/// Factory for creating WebRTC streamers with optimal encoder selection
/// </summary>
public static class EncoderFactory
{
    /// <summary>
    /// Create a WebRTC streamer using specified encoder mode
    /// </summary>
    public static IWebRTCStreamer CreateStreamer(
        int fps, 
        int kbps, 
        EncoderMode mode = EncoderMode.LibAv,
        ID3D11Device? device = null,
        int crf = 23, 
        string preset = "p1", 
        bool zerolatency = true)
    {
        Console.WriteLine($"[EncoderFactory] Creating streamer: mode={mode}, fps={fps}, kbps={kbps}");
        
        // Detect GPU vendor for optimal encoder selection
        var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();
        
        switch (gpuVendor)
        {
            case GpuVendorDetector.GpuVendor.AMD:
                Console.WriteLine("[EncoderFactory] AMD GPU detected");
                
                // Check for native AmfWrapper.dll first (hardware accelerated)
                bool nativeAmfAvailable = false;
                try
                {
                    nativeAmfAvailable = AmfNativeWrapper.IsAvailable();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncoderFactory] AmfNativeWrapper check failed: {ex.Message}");
                }
                Console.WriteLine($"[EncoderFactory] Native AMF wrapper available: {nativeAmfAvailable}");
                
                // Check for AMF runtime (for LibAv fallback)
                bool amfRuntimeAvailable = GpuVendorDetector.IsAmfAvailable();
                Console.WriteLine($"[EncoderFactory] AMF runtime available: {amfRuntimeAvailable}");
                
                // Native AMF encoder for AMD GPUs
                // Provides hardware-accelerated encoding via AMD VCN
                bool useNativeAmf = true;
                if (useNativeAmf && nativeAmfAvailable && device != null)
                {
                    try
                    {
                        Console.WriteLine("[EncoderFactory] Attempting native AMF encoder...");
                        return new WebRTCStreamerAmfNativeWrapper(fps, kbps, device);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[EncoderFactory] Native AMF failed: {ex.Message}");
                        Console.WriteLine("[EncoderFactory] Falling back to LibAv h264_amf");
                    }
                }
                
                // Fallback to LibAv (will use h264_amf if AMF runtime available)
                if (amfRuntimeAvailable)
                {
                    Console.WriteLine("[EncoderFactory] Using LibAv with h264_amf encoder");
                }
                else
                {
                    Console.WriteLine("[EncoderFactory] Warning: No AMF support, using software encoder");
                }
                break;
                
            case GpuVendorDetector.GpuVendor.NVIDIA:
                Console.WriteLine("[EncoderFactory] NVIDIA GPU detected");
                Console.WriteLine("[EncoderFactory] Using LibAv with h264_nvenc encoder (CUDA accelerated)");
                break;
                
            case GpuVendorDetector.GpuVendor.Intel:
                Console.WriteLine("[EncoderFactory] Intel GPU detected");
                Console.WriteLine("[EncoderFactory] Using LibAv with h264_qsv encoder");
                break;
                
            default:
                Console.WriteLine("[EncoderFactory] Unknown GPU vendor - using software encoder fallback");
                break;
        }
        
        // Final return - always falls back to LibAv which handles GPU detection internally
        try
        {
            return mode switch
            {
                EncoderMode.LibAv => new WebRTCStreamerLibAvWrapper(fps, kbps, device),
                _ => new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolatency, useNV12: true)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EncoderFactory] LibAv failed: {ex.Message}");
            Console.WriteLine("[EncoderFactory] Last resort: FFmpeg pipe encoder");
            return new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolatency, useNV12: true);
        }
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
/// GPU vendor detection and hardware availability checks
/// </summary>
public static class GpuVendorDetector
{
    public enum GpuVendor
    {
        Unknown,
        NVIDIA,
        AMD,
        Intel
    }
    
    /// <summary>
    /// Detect the primary GPU vendor from DXGI adapters
    /// </summary>
    public static GpuVendor DetectPrimaryGpuVendor()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure) break;
                
                try
                {
                    var desc = adapter.Description;
                    uint vendorId = (uint)desc.VendorId;
                    
                    // Check by vendor ID (most reliable)
                    if (vendorId == 0x10DE) // NVIDIA
                    {
                        Console.WriteLine($"[GpuVendorDetector] Detected NVIDIA GPU: {desc.Description}");
                        return GpuVendor.NVIDIA;
                    }
                    else if (vendorId == 0x1002 || vendorId == 0x1022) // AMD/ATI
                    {
                        Console.WriteLine($"[GpuVendorDetector] Detected AMD GPU: {desc.Description}");
                        return GpuVendor.AMD;
                    }
                    else if (vendorId == 0x8086) // Intel
                    {
                        Console.WriteLine($"[GpuVendorDetector] Detected Intel GPU: {desc.Description}");
                        return GpuVendor.Intel;
                    }
                    
                    // Fallback to name check
                    string name = desc.Description.ToUpperInvariant();
                    if (name.Contains("NVIDIA") || name.Contains("GEFORCE") || name.Contains("RTX") || name.Contains("GTX"))
                        return GpuVendor.NVIDIA;
                    else if (name.Contains("AMD") || name.Contains("RADEON") || name.Contains("ATI"))
                        return GpuVendor.AMD;
                    else if (name.Contains("INTEL"))
                        return GpuVendor.Intel;
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuVendorDetector] Detection failed: {ex.Message}");
        }
        
        return GpuVendor.Unknown;
    }
    
    public static bool IsAmfAvailable()
    {
        try
        {
            IntPtr handle = NativeLibrary.Load("amfrt64.dll");
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch { }
        return false;
    }
    
    public static bool IsNvencAvailable()
    {
        try
        {
            IntPtr handle = NativeLibrary.Load("nvEncodeAPI64.dll");
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch { }
        return false;
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
/// Provides hardware-accelerated encoding via h264_amf (AMD), nvenc (NVIDIA), or qsv (Intel)
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
