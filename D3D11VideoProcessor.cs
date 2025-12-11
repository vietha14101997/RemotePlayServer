#nullable enable
using System;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// D3D11 Video Processor for GPU-accelerated color conversion (BGRA -> NV12).
/// Note: Full implementation requires D3D11 Video API which has limited managed bindings.
/// This class provides CPU fallback conversion.
/// </summary>
public sealed class D3D11VideoProcessor : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    // Output NV12 buffer (CPU fallback)
    private byte[]? _nv12Buffer;

    public bool IsAvailable { get; private set; }

    public D3D11VideoProcessor(ID3D11Device device, int width, int height)
    {
        _device = device;
        _context = device.ImmediateContext;
        _width = width;
        _height = height;

        // Allocate NV12 buffer
        int nv12Size = width * height * 3 / 2;
        _nv12Buffer = new byte[nv12Size];

        // Video processor requires D3D11 Video interfaces which have limited support
        // For now, we use CPU conversion as fallback
        IsAvailable = false;
        Console.WriteLine($"[VP] Initialized with CPU fallback: {width}x{height}");
    }

    /// <summary>
    /// Convert BGRA buffer to NV12 (CPU path)
    /// </summary>
    public byte[]? ConvertBgraToNv12(ReadOnlySpan<byte> bgra, int stride)
    {
        if (_nv12Buffer == null) return null;

        BgraToNv12Converter.Convert(bgra, _width, _height, stride, _nv12Buffer);
        return _nv12Buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nv12Buffer = null;
        Console.WriteLine("[VP] Disposed");
    }
}

/// <summary>
/// Fast CPU-based BGRA to NV12 converter.
/// </summary>
public static class BgraToNv12Converter
{
    /// <summary>
    /// Convert BGRA to NV12 format.
    /// NV12: Y plane (w*h) + interleaved UV plane (w*h/2)
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<byte> nv12)
    {
        int ySize = width * height;

        // Process 2x2 blocks for UV subsampling
        for (int y = 0; y < height; y += 2)
        {
            int srcRow0 = y * stride;
            int srcRow1 = Math.Min(y + 1, height - 1) * stride;
            int yRow0 = y * width;
            int yRow1 = Math.Min(y + 1, height - 1) * width;
            int uvRow = ySize + (y / 2) * width;

            for (int x = 0; x < width; x += 2)
            {
                int x1 = Math.Min(x + 1, width - 1);

                // Get 4 pixels in 2x2 block
                int idx00 = srcRow0 + x * 4;
                int idx01 = srcRow0 + x1 * 4;
                int idx10 = srcRow1 + x * 4;
                int idx11 = srcRow1 + x1 * 4;

                // Read BGRA values (with bounds checking)
                byte b00 = idx00 < bgra.Length ? bgra[idx00] : (byte)0;
                byte g00 = idx00 + 1 < bgra.Length ? bgra[idx00 + 1] : (byte)0;
                byte r00 = idx00 + 2 < bgra.Length ? bgra[idx00 + 2] : (byte)0;

                byte b01 = idx01 < bgra.Length ? bgra[idx01] : (byte)0;
                byte g01 = idx01 + 1 < bgra.Length ? bgra[idx01 + 1] : (byte)0;
                byte r01 = idx01 + 2 < bgra.Length ? bgra[idx01 + 2] : (byte)0;

                byte b10 = idx10 < bgra.Length ? bgra[idx10] : (byte)0;
                byte g10 = idx10 + 1 < bgra.Length ? bgra[idx10 + 1] : (byte)0;
                byte r10 = idx10 + 2 < bgra.Length ? bgra[idx10 + 2] : (byte)0;

                byte b11 = idx11 < bgra.Length ? bgra[idx11] : (byte)0;
                byte g11 = idx11 + 1 < bgra.Length ? bgra[idx11 + 1] : (byte)0;
                byte r11 = idx11 + 2 < bgra.Length ? bgra[idx11 + 2] : (byte)0;

                // Calculate Y for each pixel (BT.601)
                if (yRow0 + x < nv12.Length)
                    nv12[yRow0 + x] = ClampY(r00, g00, b00);
                if (yRow0 + x1 < nv12.Length)
                    nv12[yRow0 + x1] = ClampY(r01, g01, b01);
                if (yRow1 + x < nv12.Length)
                    nv12[yRow1 + x] = ClampY(r10, g10, b10);
                if (yRow1 + x1 < nv12.Length)
                    nv12[yRow1 + x1] = ClampY(r11, g11, b11);

                // Calculate UV (average of 2x2 block)
                int rAvg = (r00 + r01 + r10 + r11 + 2) >> 2;
                int gAvg = (g00 + g01 + g10 + g11 + 2) >> 2;
                int bAvg = (b00 + b01 + b10 + b11 + 2) >> 2;

                int u = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                int v = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;

                if (uvRow + x < nv12.Length)
                    nv12[uvRow + x] = (byte)Math.Clamp(u, 0, 255);
                if (uvRow + x + 1 < nv12.Length)
                    nv12[uvRow + x + 1] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    private static byte ClampY(int r, int g, int b)
    {
        int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
        return (byte)Math.Clamp(y, 16, 235);
    }

    /// <summary>
    /// Calculate required NV12 buffer size
    /// </summary>
    public static int CalculateNv12Size(int width, int height)
    {
        return width * height * 3 / 2;
    }
}
