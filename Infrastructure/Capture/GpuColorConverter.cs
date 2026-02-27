#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Capture;

// Add P/Invoke for D3DCompiler
internal static class D3DCompiler
{
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPStr)] string pSrcData,
        int SrcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
        IntPtr pDefines,
        IntPtr pInclude, // ID3DInclude*
        [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        int Flags1,
        int Flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);
}

/// <summary>
/// GPU-accelerated BGRA to NV12 color conversion using D3D11 Compute Shader.
/// This eliminates CPU-bound color conversion bottleneck for hardware encoding.
/// </summary>
public sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    // Double buffered NV12 textures for Compute Shader Output
    private ID3D11Texture2D[]? _nv12Textures;
    private ID3D11UnorderedAccessView[]? _yPlaneUAVs;
    private ID3D11UnorderedAccessView[]? _uvPlaneUAVs;
    private int _bufferIndex = 0;

    // CPU Staging (Single is fine as we only read one at a time)
    private ID3D11Texture2D? _nv12Staging;
    private ID3D11Texture2D? _bgraTexture; // Internal GPU copy
    private ID3D11Texture2D? _bgraStagingTexture; // CPU fallback copy
    private ID3D11ShaderResourceView? _bgraSRV;

    private int _width;
    private int _height;
    private readonly byte[] _nv12Buffer;
    private bool _disposed;

    private bool _needsIntermediateCopy; // Flag for safe copy (tested at runtime)
    private bool _directBindingTested; // Whether we've tested direct SRV binding

    // Cached SRV for direct binding mode (avoids per-frame CreateShaderResourceView)
    private ID3D11ShaderResourceView? _cachedDirectSRV;
    private IntPtr _cachedDirectSRVTexturePtr; // Track which texture the cached SRV was created for

    // Compute Shader resources
    private ID3D11ComputeShader? _computeShader;
    private ID3D11Buffer? _csParamsBuffer;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct CSParams
    {
        public uint Width;
        public uint Height;
        public uint Pad1;
        public uint Pad2;
    }

    public int Width => _width;
    public int Height => _height;
    public int NV12Size => _width * _height * 3 / 2;
    
    /// <summary>
    /// GPU NV12 texture for zero-copy encoding path
    /// </summary>
    public ID3D11Texture2D? NV12Texture => _nv12Textures?[_bufferIndex];

    public GpuColorConverter(ID3D11Device device, int width, int height)
    {
        _device = device;
        _width = width;
        _height = height;
        _nv12Buffer = new byte[NV12Size];
        
        _context = device.ImmediateContext;

        _needsIntermediateCopy = false; // Will be tested at runtime - try direct binding first
        _directBindingTested = false;
        Logger.Info($"[GpuColorConverter] Using COMPUTE SHADER for BGRA->NV12 (will test direct binding)");

        // Create NV12 output texture
        CreateNV12Textures(width, height);

        Logger.Info($"[GpuColorConverter] Initialized {width}x{height} BGRA->NV12 converter");

        InitializeComputeShader();
    }
    
    private void CreateNV12Textures(int width, int height)
    {
        // Dispose old textures if resizing
    if (_nv12Textures != null)
    {
        foreach (var tex in _nv12Textures) tex?.Dispose();
    }
    if (_yPlaneUAVs != null) foreach (var uav in _yPlaneUAVs) uav?.Dispose();
    if (_uvPlaneUAVs != null) foreach (var uav in _uvPlaneUAVs) uav?.Dispose();

    _nv12Staging?.Dispose();
    _bgraTexture?.Dispose();
    _bgraSRV?.Dispose();
    
    // NV12 output textures (GPU) - Double Buffered
    _nv12Textures = new ID3D11Texture2D[2];
    for (int i = 0; i < 2; i++)
    {
        _nv12Textures[i] = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None
        });
    }

        // NV12 staging texture for CPU readback/write
        _nv12Staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write
        });
        
        // Internal BGRA texture for Compute Shader input (Safe Copy fallback)
        _bgraTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None
        });

        _bgraSRV = _device.CreateShaderResourceView(_bgraTexture);

    // Create UAVs for Compute Shader (Y and UV planes) - DOUBLE BUFFERED
    if (_nv12Textures != null)
    {
        _yPlaneUAVs = new ID3D11UnorderedAccessView[2];
        _uvPlaneUAVs = new ID3D11UnorderedAccessView[2];
        
        for (int i = 0; i < 2; i++)
        {
            // Y Plane UAV (R8_UINT format)
            var uavDescY = new UnorderedAccessViewDescription
            {
                Format = Format.R8_UInt, 
                ViewDimension = UnorderedAccessViewDimension.Texture2D
            };
            uavDescY.Texture2D.MipSlice = 0;
            _yPlaneUAVs[i] = _device.CreateUnorderedAccessView(_nv12Textures[i], uavDescY);
            
            var uavDescUV = new UnorderedAccessViewDescription
            {
                Format = Format.R8G8_UInt,
                ViewDimension = UnorderedAccessViewDimension.Texture2D
            };
            uavDescUV.Texture2D.MipSlice = 0;
            _uvPlaneUAVs[i] = _device.CreateUnorderedAccessView(_nv12Textures[i], uavDescUV);
        }
        
    }
    
    _width = width;
    _height = height;
}
    
    /// <summary>
    /// Convert BGRA texture to NV12 on GPU and copy to CPU buffer.
    /// </summary>
    public bool Convert(ID3D11Texture2D bgraTexture, byte[] outputNV12Buffer)
    {
        if (_disposed) return false;

        // CPU fallback if compute shader not available
        if (_computeShader == null)
        {
            return ConvertViaCPU(bgraTexture, outputNV12Buffer);
        }

        try
        {
            // Convert via Compute Shader
            if (!ConvertViaComputeShader(bgraTexture)) return false;

            // Copy from GPU NV12 texture to Staging texture
            var currentTex = _nv12Textures![_bufferIndex];
            _context.CopyResource(_nv12Staging!, currentTex);

            // Map staging texture
            var mapped = _context.Map(_nv12Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyNV12Data(mapped, outputNV12Buffer);
            }
            finally
            {
                _context.Unmap(_nv12Staging!, 0);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[GpuColorConverter] Convert error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// CPU fallback for Convert() - used when Compute Shader is unavailable
    /// </summary>
    private bool ConvertViaCPU(ID3D11Texture2D bgraTexture, byte[] outputNV12Buffer)
    {
        try
        {
            // Create staging texture for CPU read if not exists
            if (_bgraStagingTexture == null)
            {
                _bgraStagingTexture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                });
            }
            
            // Copy BGRA texture to staging
            _context.CopyResource(_bgraStagingTexture, bgraTexture);
            
            // Map and convert
            var mapped = _context.Map(_bgraStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    int srcPitch = (int)mapped.RowPitch;
                    
                    fixed (byte* dst = outputNV12Buffer)
                    {
                        ConvertBgraToNv12(src, srcPitch, dst, _width, _width, _height);
                    }
                }
            }
            finally
            {
                _context.Unmap(_bgraStagingTexture, 0);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[GpuColorConverter] ConvertViaCPU error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Convert BGRA texture to NV12 on GPU only (zero-copy path).
    /// Returns the NV12 texture directly without CPU readback.
    /// </summary>
    public ID3D11Texture2D? ConvertToTexture(ID3D11Texture2D bgraTexture)
    {
        if (_disposed) return null;
        if (bgraTexture == null)
        {
            Logger.Warn("[GpuColorConverter] ConvertToTexture: input texture is null");
            return null;
        }

        if (_nv12Textures == null) return null;

        // Compute Shader path (primary)
        if (_computeShader != null)
        {
            try
            {
                if (ConvertViaComputeShader(bgraTexture))
                    return _nv12Textures[_bufferIndex];
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[GpuColorConverter] Compute Shader error: {ex.Message}");
                return null;
            }
        }

        // CPU fallback
        return ConvertToTextureViaCPU(bgraTexture);
    }
    

    
    /// <summary>
    /// CPU fallback for BGRA->NV12 conversion when Compute Shader is not available
    /// </summary>
    private ID3D11Texture2D? ConvertToTextureViaCPU(ID3D11Texture2D bgraTexture)
    {
        try
        {
            // Create staging texture for CPU read if not exists
            if (_bgraStagingTexture == null)
            {
                _bgraStagingTexture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                });
            }
            
            // Copy BGRA texture to staging
            _context.CopyResource(_bgraStagingTexture, bgraTexture);
            
            // Map BGRA staging texture
            var bgraMapped = _context.Map(_bgraStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            
            try
            {
                // Map NV12 staging texture for write
                var nv12Mapped = _context.Map(_nv12Staging!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                
                try
                {
                    unsafe
                    {
                        byte* bgraSrc = (byte*)bgraMapped.DataPointer;
                        int bgraPitch = (int)bgraMapped.RowPitch;
                        byte* nv12Dst = (byte*)nv12Mapped.DataPointer;
                        int nv12Pitch = (int)nv12Mapped.RowPitch;
                        
                        // Convert BGRA to NV12 (Y plane + UV plane)
                        ConvertBgraToNv12(bgraSrc, bgraPitch, nv12Dst, nv12Pitch, _width, _height);
                    }
                }
                finally
                {
                    _context.Unmap(_nv12Staging!, 0);
                }
            }
            finally
            {
                _context.Unmap(_bgraStagingTexture, 0);
            }
            
            // Copy from staging to output texture
            var currentTex = _nv12Textures?[_bufferIndex];
            if (currentTex != null)
            {
                _context.CopyResource(currentTex, _nv12Staging!);
                return currentTex;
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"[GpuColorConverter] CPU fallback error: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Convert BGRA to NV12 in CPU memory
    /// </summary>
    private static unsafe void ConvertBgraToNv12(byte* bgra, int bgraPitch, byte* nv12, int nv12Pitch, int width, int height)
    {
        // Y plane
        byte* yPlane = nv12;
        for (int y = 0; y < height; y++)
        {
            byte* bgraRow = bgra + y * bgraPitch;
            byte* yRow = yPlane + y * nv12Pitch;
            
            for (int x = 0; x < width; x++)
            {
                byte b = bgraRow[x * 4 + 0];
                byte g = bgraRow[x * 4 + 1];
                byte r = bgraRow[x * 4 + 2];
                
                // BT.601 Y conversion
                yRow[x] = (byte)Math.Clamp((66 * r + 129 * g + 25 * b + 128 >> 8) + 16, 0, 255);
            }
        }
        
        // UV plane (interleaved, half resolution)
        byte* uvPlane = nv12 + nv12Pitch * height;
        for (int y = 0; y < height / 2; y++)
        {
            byte* bgraRow0 = bgra + (y * 2) * bgraPitch;
            byte* bgraRow1 = bgra + (y * 2 + 1) * bgraPitch;
            byte* uvRow = uvPlane + y * nv12Pitch;
            
            for (int x = 0; x < width / 2; x++)
            {
                // Average 2x2 block
                int b = (bgraRow0[x * 8 + 0] + bgraRow0[x * 8 + 4] + bgraRow1[x * 8 + 0] + bgraRow1[x * 8 + 4]) / 4;
                int g = (bgraRow0[x * 8 + 1] + bgraRow0[x * 8 + 5] + bgraRow1[x * 8 + 1] + bgraRow1[x * 8 + 5]) / 4;
                int r = (bgraRow0[x * 8 + 2] + bgraRow0[x * 8 + 6] + bgraRow1[x * 8 + 2] + bgraRow1[x * 8 + 6]) / 4;
                
                // BT.601 U, V conversion
                uvRow[x * 2 + 0] = (byte)Math.Clamp((-38 * r - 74 * g + 112 * b + 128 >> 8) + 128, 0, 255); // U
                uvRow[x * 2 + 1] = (byte)Math.Clamp((112 * r - 94 * g - 18 * b + 128 >> 8) + 128, 0, 255);  // V
            }
        }
    }
    
    private void CopyNV12Data(MappedSubresource mapped, byte[] outputBuffer)
    {
        unsafe
        {
            byte* src = (byte*)mapped.DataPointer;
            int srcRowPitch = (int)mapped.RowPitch;
            
            // NV12 format: Y plane (full resolution) followed by interleaved UV plane (half resolution)
            int yPlaneSize = _width * _height;
            int uvPlaneSize = _width * _height / 2;
            
            fixed (byte* dst = outputBuffer)
            {
                // Copy Y plane
                if (srcRowPitch == _width)
                {
                    // Contiguous - single copy
                    Buffer.MemoryCopy(src, dst, yPlaneSize, yPlaneSize);
                }
                else
                {
                    // Row by row copy for Y plane
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + y * srcRowPitch,
                            dst + y * _width,
                            _width,
                            _width
                        );
                    }
                }
                
                // UV plane starts after Y plane in the texture (at height offset in NV12)
                byte* uvSrc = src + srcRowPitch * _height;
                byte* uvDst = dst + yPlaneSize;
                
                int uvHeight = _height / 2;
                if (srcRowPitch == _width)
                {
                    Buffer.MemoryCopy(uvSrc, uvDst, uvPlaneSize, uvPlaneSize);
                }
                else
                {
                    for (int y = 0; y < uvHeight; y++)
                    {
                        Buffer.MemoryCopy(
                            uvSrc + y * srcRowPitch,
                            uvDst + y * _width,
                            _width,
                            _width
                        );
                    }
                }
            }
        }
    }
    
    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth == _width && newHeight == _height) return;
        CreateNV12Textures(newWidth, newHeight);
        Logger.Info($"[GpuColorConverter] Resized to {newWidth}x{newHeight}");
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_nv12Textures != null) foreach (var t in _nv12Textures) t?.Dispose();
        if (_yPlaneUAVs != null) foreach (var u in _yPlaneUAVs) u?.Dispose();
        if (_uvPlaneUAVs != null) foreach (var u in _uvPlaneUAVs) u?.Dispose();

        _nv12Staging?.Dispose();
        _computeShader?.Dispose();
        _csParamsBuffer?.Dispose();
        _cachedDirectSRV?.Dispose();
        _bgraTexture?.Dispose();
        _bgraStagingTexture?.Dispose();
        _bgraSRV?.Dispose();
    }

    // === Compute Shader Implementation ===
    private const string ComputeShaderSource = @"
cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    uint2 Padding;
};

Texture2D<float4> InputTexture : register(t0);
RWTexture2D<uint> OutputY : register(u0);
RWTexture2D<uint2> OutputUV : register(u1);

[numthreads(16, 16, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint x = dispatchThreadID.x;
    uint y = dispatchThreadID.y;
    
    // Bounds check to prevent TDR/Device Removed
    if (x >= Width || y >= Height) return;
    
    // Y Plane: One pixel per thread
    // Read BGRA
    float4 pixel = InputTexture[uint2(x, y)];
    
    // Standard BT.601 conversion (Limited Range)
    // Coeffs: R=0.257, G=0.504, B=0.098
    // In HLSL B8G8R8A8_UNorm: .x=Red, .y=Green, .z=Blue
    float Y = (0.257 * pixel.x + 0.504 * pixel.y + 0.098 * pixel.z) * 255.0 + 16.0;
    OutputY[uint2(x, y)] = (uint)clamp(Y, 0, 255);
    
    // UV Plane: One UV pair per 2x2 block
    // We execute threads for full res, but only (x%2==0 && y%2==0) calculate UV
    if ((x % 2 == 0) && (y % 2 == 0))
    {
        // Sample 4 pixels for subsampling (box filter)
        uint x1 = min(x + 1, Width - 1);
        uint y1 = min(y + 1, Height - 1);
        
        float4 p00 = pixel;
        float4 p01 = InputTexture[uint2(x, y1)];
        float4 p10 = InputTexture[uint2(x1, y)];
        float4 p11 = InputTexture[uint2(x1, y1)];
        
        float4 avg = (p00 + p01 + p10 + p11) * 0.25;
        
        // U = -0.148*R - 0.291*G + 0.439*B + 128
        // V =  0.439*R - 0.368*G - 0.071*B + 128
        // Correct channel mapping: .x=R, .y=G, .z=B
        float U = (-0.148 * avg.x - 0.291 * avg.y + 0.439 * avg.z) * 255.0 + 128.0;
        float V = ( 0.439 * avg.x - 0.368 * avg.y - 0.071 * avg.z) * 255.0 + 128.0;
        
        // Write to UV plane (half resolution coordinates)
        OutputUV[uint2(x/2, y/2)] = uint2((uint)clamp(U, 0, 255), (uint)clamp(V, 0, 255));
    }
}";

    private void InitializeComputeShader()
    {
        try
        {
            Logger.Info("[GpuColorConverter] Compiling Compute Shader for BGRA->NV12...");
            
            IntPtr codeBlob = IntPtr.Zero;
            IntPtr errorBlob = IntPtr.Zero;
            
            // Compile
            int hr = D3DCompiler.D3DCompile(
                ComputeShaderSource,
                ComputeShaderSource.Length,
                "BgraToNv12",
                IntPtr.Zero,
                IntPtr.Zero,
                "main",
                "cs_5_0",
                0, // Flags1
                0, // Flags2
                out codeBlob,
                out errorBlob
            );
            
            if (hr != 0 || codeBlob == IntPtr.Zero)
            {
                // string errorMsg = "Unknown compile error"; // UNUSED
                if (errorBlob != IntPtr.Zero)
                {
                    // ID3DBlob interface: GetBufferPointer at offset 8 (vtable?) or just access memory?
                    // Usually it's a COM object pointer.
                    // To keep it simple, assume failure and print error
                    // But we can't easily marshal the blob string without definition.
                    Logger.Error($"[GpuColorConverter] Shader compile failed (hr=0x{hr:X})");
                    if (errorBlob != IntPtr.Zero) Marshal.Release(errorBlob);
                    return;
                }
            }
            
            // Retrieve pointer and size from Blob
            // ID3DBlob: virtual LPVOID GetBufferPointer(); virtual SIZE_T GetBufferSize();
            // We need to call GetBufferPointer (index 3) and GetBufferSize (index 4)
            // Or use Marshal.ReadIntPtr for GetBufferPointer?
            // Let's rely on SharpGen/Vortice constructor if possible, or manual invocation.
            // Using Vortice.Direct3D.Blob which wraps ID3DBlob if we could cast it...
            // But D3DCompile returns raw IntPtr.
            
            // Manual VTable call to get pointer and size
            IntPtr pBuffer = GetBlobPointer(codeBlob);
            int size = GetBlobSize(codeBlob);
            
            unsafe 
            {
                var shaderBytecode = new ReadOnlySpan<byte>((void*)pBuffer, size);
                _computeShader = _device.CreateComputeShader(shaderBytecode);
            }
            Logger.Info("[GpuColorConverter] Compute Shader compiled and created successfully");
            
            Marshal.Release(codeBlob);
            if (errorBlob != IntPtr.Zero) Marshal.Release(errorBlob);
            
            // Create Constant Buffer
            var cbDesc = new BufferDescription
            {
                ByteWidth = 16, // 4 * 4 bytes
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.None
            };
            _csParamsBuffer = _device.CreateBuffer(cbDesc);
        }
        catch (Exception ex)
        {
            Logger.Error($"[GpuColorConverter] InitComputeShader error: {ex.Message}");
        }
    }
    
    private unsafe IntPtr GetBlobPointer(IntPtr blob)
    {
        // Simple VTable logic for ID3DBlob: 
        // 0: QueryInterface, 1: AddRef, 2: Release
        // 3: GetBufferPointer
        IntPtr* vtable = *(IntPtr**)blob;
        IntPtr func = vtable[3];
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr> getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)func;
        return getBufferPointer(blob);
    }
    
    private unsafe int GetBlobSize(IntPtr blob)
    {
        // 4: GetBufferSize
        IntPtr* vtable = *(IntPtr**)blob;
        IntPtr func = vtable[4];
        delegate* unmanaged[Stdcall]<IntPtr, int> getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, int>)func;
        return getBufferSize(blob);
    }

    private bool ConvertViaComputeShader(ID3D11Texture2D bgraTexture)
    {
        if (_computeShader == null || 
            _yPlaneUAVs == null || 
            _uvPlaneUAVs == null || 
            _bgraTexture == null || 
            _bgraSRV == null || 
            _csParamsBuffer == null) 
        {
            Logger.Warn("[GpuColorConverter] ConvertViaComputeShader failed: Missing resources");
            return false;
        }
        
        // Swap buffer
        _bufferIndex = (_bufferIndex + 1) % 2;
        var currentYUAV = _yPlaneUAVs[_bufferIndex];
        var currentUVUAV = _uvPlaneUAVs[_bufferIndex];
        
        try
        {
            // Zero-copy optimization: try to bind Desktop Dup texture directly as SRV
            // This avoids an 8MB copy per frame (1920x1080 BGRA = 8MB)
            ID3D11ShaderResourceView? inputSRV = null;

            if (!_directBindingTested)
            {
                // First frame: test if direct SRV binding works
                try
                {
                    _cachedDirectSRV = _device.CreateShaderResourceView(bgraTexture);
                    _cachedDirectSRVTexturePtr = bgraTexture.NativePointer;
                    inputSRV = _cachedDirectSRV;
                    _needsIntermediateCopy = false;
                    Logger.Debug("[GpuColorConverter] Direct SRV binding succeeded - zero-copy mode enabled");
                }
                catch
                {
                    _needsIntermediateCopy = true;
                    Logger.Info("[GpuColorConverter] Direct SRV binding failed - using safe copy mode");
                }
                _directBindingTested = true;
            }

            if (_needsIntermediateCopy)
            {
                // Safe copy mode: copy to internal texture, use cached SRV
                _context.CopyResource(_bgraTexture, bgraTexture);
                inputSRV = _bgraSRV;
            }
            else if (inputSRV == null)
            {
                // Direct binding mode: reuse cached SRV if same texture, otherwise recreate
                var texturePtr = bgraTexture.NativePointer;
                if (_cachedDirectSRV != null && _cachedDirectSRVTexturePtr == texturePtr)
                {
                    inputSRV = _cachedDirectSRV;
                }
                else
                {
                    // Texture changed (rare: happens on resolution change) - recreate SRV
                    _cachedDirectSRV?.Dispose();
                    _cachedDirectSRV = _device.CreateShaderResourceView(bgraTexture);
                    _cachedDirectSRVTexturePtr = texturePtr;
                    inputSRV = _cachedDirectSRV;
                }
            }

            // Update Constant Buffer
            var paramsData = new CSParams { Width = (uint)_width, Height = (uint)_height };
            _context.UpdateSubresource(paramsData, _csParamsBuffer!);

            _context.CSSetShader(_computeShader);
            _context.CSSetConstantBuffer(0, _csParamsBuffer);
            _context.CSSetShaderResource(0, inputSRV);
            _context.CSSetUnorderedAccessView(0, currentYUAV);
            _context.CSSetUnorderedAccessView(1, currentUVUAV);

            // Dispatch
            int dispatchX = (_width + 15) / 16;
            int dispatchY = (_height + 15) / 16;
            _context.Dispatch((uint)dispatchX, (uint)dispatchY, 1);

            // Clean up bindings (don't dispose cached SRV)
            _context.CSSetShaderResource(0, null);
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetUnorderedAccessView(1, null);
            _context.CSSetConstantBuffer(0, null);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[GpuColorConverter] Compute Shader dispatch error: {ex.Message}");
            return false;
        }
    }
}
