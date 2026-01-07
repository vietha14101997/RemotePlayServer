#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// GPU-accelerated texture scaler using Compute Shader.
    /// Performs bilinear scaling from source BGRA texture to target BGRA texture.
    /// Each instance is tied to a single D3D11 device.
    /// </summary>
    public class GpuTextureScaler : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly int _monitorIndex;
        
        // Compute Shader resources
        private ID3D11ComputeShader? _scaleShader;
        private ID3D11Buffer? _paramsBuffer;
        private ID3D11SamplerState? _linearSampler;
        
        // Single output texture for this scaler
        private ID3D11Texture2D? _outputTexture;
        private ID3D11UnorderedAccessView? _outputUAV;
        private int _outputWidth;
        private int _outputHeight;
        
        private bool _initialized;
        private bool _disposed;

        // Shader params struct - must match HLSL
        [StructLayout(LayoutKind.Sequential)]
        private struct ScaleParams
        {
            public uint SrcWidth;
            public uint SrcHeight;
            public uint DstWidth;
            public uint DstHeight;
        }

        // HLSL Compute Shader for bilinear scaling
        private const string ScaleShaderSource = @"
// Input: Source BGRA texture
Texture2D<float4> srcTexture : register(t0);
SamplerState linearSampler : register(s0);

// Output: Destination BGRA texture  
RWTexture2D<float4> dstTexture : register(u0);

// Parameters
cbuffer ScaleParams : register(b0)
{
    uint srcWidth;
    uint srcHeight;
    uint dstWidth;
    uint dstHeight;
};

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    // Check bounds
    if (dispatchThreadId.x >= dstWidth || dispatchThreadId.y >= dstHeight)
        return;
    
    // Calculate source UV coordinates (0-1 range)
    float u = (float(dispatchThreadId.x) + 0.5f) / float(dstWidth);
    float v = (float(dispatchThreadId.y) + 0.5f) / float(dstHeight);
    
    // Sample source texture with bilinear filtering
    float4 color = srcTexture.SampleLevel(linearSampler, float2(u, v), 0);
    
    // Write to destination
    dstTexture[dispatchThreadId.xy] = color;
}
";

        public GpuTextureScaler(ID3D11Device device, int monitorIndex)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _context = device.ImmediateContext;
            _monitorIndex = monitorIndex;
            
            InitializeShader();
            
            if (!_initialized)
                throw new InvalidOperationException($"Failed to initialize GPU scaler for monitor {monitorIndex}");
        }

        private void InitializeShader()
        {
            try
            {
                // Compile compute shader
                var shaderBlob = CompileShader(ScaleShaderSource, "CSMain", "cs_5_0");
                if (shaderBlob == IntPtr.Zero)
                {
                    Console.WriteLine("[GpuTextureScaler] Failed to compile shader");
                    return;
                }

                try
                {
                    // Get blob size and data using VTable calls
                    var blobSize = D3DCompilerHelper.GetBlobSize(shaderBlob);
                    var blobData = D3DCompilerHelper.GetBlobPointer(shaderBlob);
                    
                    // Create shader from blob using ReadOnlySpan
                    unsafe
                    {
                        var shaderBytecode = new ReadOnlySpan<byte>((void*)blobData, blobSize);
                        _scaleShader = _device.CreateComputeShader(shaderBytecode);
                    }
                }
                finally
                {
                    // Release blob
                    Marshal.Release(shaderBlob);
                }

                // Create constant buffer for parameters
                var bufferDesc = new BufferDescription
                {
                    ByteWidth = (uint)Marshal.SizeOf<ScaleParams>(),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ConstantBuffer,
                    CPUAccessFlags = CpuAccessFlags.Write
                };
                _paramsBuffer = _device.CreateBuffer(bufferDesc);

                // Create sampler state for bilinear filtering (reuse across frames)
                var samplerDesc = new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    MipLODBias = 0,
                    MaxAnisotropy = 1,
                    ComparisonFunc = ComparisonFunction.Never,
                    MinLOD = 0,
                    MaxLOD = float.MaxValue
                };
                _linearSampler = _device.CreateSamplerState(samplerDesc);

                _initialized = _scaleShader != null && _paramsBuffer != null && _linearSampler != null;
                if (_initialized)
                {
                    Console.WriteLine($"[GpuTextureScaler] Monitor {_monitorIndex}: Compute shader initialized");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuTextureScaler] InitializeShader failed: {ex.Message}");
                _initialized = false;
            }
        }

        private IntPtr CompileShader(string source, string entryPoint, string target)
        {
            int result = D3DCompilerHelper.D3DCompile(
                source,
                source.Length,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                entryPoint,
                target,
                0, // D3DCOMPILE_OPTIMIZATION_LEVEL3
                0,
                out IntPtr shaderBlob,
                out IntPtr errorBlob);

            if (result != 0)
            {
                if (errorBlob != IntPtr.Zero)
                {
                    var errorPtr = D3DCompilerHelper.GetBlobPointer(errorBlob);
                    var errorSize = D3DCompilerHelper.GetBlobSize(errorBlob);
                    string error = Marshal.PtrToStringAnsi(errorPtr, errorSize) ?? "Unknown error";
                    Console.WriteLine($"[GpuTextureScaler] Shader compile error: {error}");
                    Marshal.Release(errorBlob);
                }
                return IntPtr.Zero;
            }

            if (errorBlob != IntPtr.Zero)
                Marshal.Release(errorBlob);

            return shaderBlob;
        }

        /// <summary>
        /// Scale a BGRA texture from source dimensions to target dimensions.
        /// monitorIndex is ignored (kept for API compatibility) - this scaler only handles one monitor.
        /// </summary>
        public ID3D11Texture2D? Scale(
            ID3D11Texture2D sourceTexture, 
            int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight,
            int monitorIndex)
        {
            if (_disposed || !_initialized) return null;
            if (_scaleShader == null || _paramsBuffer == null || _linearSampler == null) return null;

            try
            {
                // Create or resize output texture if needed
                if (_outputTexture == null || _outputWidth != targetWidth || _outputHeight != targetHeight)
                {
                    // Dispose old resources
                    try { _outputUAV?.Dispose(); } catch { }
                    try { _outputTexture?.Dispose(); } catch { }
                    
                    // Create output texture with UAV support
                    var outputDesc = new Texture2DDescription
                    {
                        Width = (uint)targetWidth,
                        Height = (uint)targetHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    
                    _outputTexture = _device.CreateTexture2D(outputDesc);
                    _outputWidth = targetWidth;
                    _outputHeight = targetHeight;
                    
                    // Create UAV for output
                    var uavDesc = new UnorderedAccessViewDescription
                    {
                        Format = Format.B8G8R8A8_UNorm,
                        ViewDimension = UnorderedAccessViewDimension.Texture2D
                    };
                    uavDesc.Texture2D.MipSlice = 0;
                    
                    _outputUAV = _device.CreateUnorderedAccessView(_outputTexture, uavDesc);
                    
                    Console.WriteLine($"[GpuTextureScaler] Monitor {_monitorIndex}: Output texture {targetWidth}x{targetHeight}");
                }

                // Create SRV for source texture (must create each frame as source changes)
                var srvDesc = new ShaderResourceViewDescription
                {
                    Format = Format.B8G8R8A8_UNorm,
                    ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D
                };
                srvDesc.Texture2D.MipLevels = 1;
                srvDesc.Texture2D.MostDetailedMip = 0;
                
                using var sourceSRV = _device.CreateShaderResourceView(sourceTexture, srvDesc);

                // Update constant buffer
                var mappedBuffer = _context.Map(_paramsBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                var scaleParams = new ScaleParams
                {
                    SrcWidth = (uint)sourceWidth,
                    SrcHeight = (uint)sourceHeight,
                    DstWidth = (uint)targetWidth,
                    DstHeight = (uint)targetHeight
                };
                Marshal.StructureToPtr(scaleParams, mappedBuffer.DataPointer, false);
                _context.Unmap(_paramsBuffer, 0);

                // Set compute shader resources
                _context.CSSetShader(_scaleShader);
                _context.CSSetConstantBuffer(0, _paramsBuffer);
                _context.CSSetShaderResource(0, sourceSRV);
                _context.CSSetSampler(0, _linearSampler);
                _context.CSSetUnorderedAccessView(0, _outputUAV);

                // Dispatch compute shader (16x16 thread groups)
                uint groupsX = ((uint)targetWidth + 15) / 16;
                uint groupsY = ((uint)targetHeight + 15) / 16;
                _context.Dispatch(groupsX, groupsY, 1);

                // Unbind resources to prevent hazards
                _context.CSSetShaderResource(0, null);
                _context.CSSetUnorderedAccessView(0, null);

                return _outputTexture;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuTextureScaler] Monitor {_monitorIndex} scale failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _outputUAV?.Dispose(); } catch { }
            try { _outputTexture?.Dispose(); } catch { }
            try { _linearSampler?.Dispose(); } catch { }
            try { _paramsBuffer?.Dispose(); } catch { }
            try { _scaleShader?.Dispose(); } catch { }
        }
    }

    // D3DCompiler P/Invoke
    internal static class D3DCompilerHelper
    {
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.Winapi)]
        public static extern int D3DCompile(
            [MarshalAs(UnmanagedType.LPStr)] string pSrcData,
            int SrcDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
            IntPtr pDefines,
            IntPtr pInclude,
            [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
            [MarshalAs(UnmanagedType.LPStr)] string pTarget,
            int Flags1,
            int Flags2,
            out IntPtr ppCode,
            out IntPtr ppErrorMsgs);

        // ID3DBlob VTable access (COM interface)
        // VTable: 0=QueryInterface, 1=AddRef, 2=Release, 3=GetBufferPointer, 4=GetBufferSize
        public static unsafe IntPtr GetBlobPointer(IntPtr blob)
        {
            IntPtr* vtable = *(IntPtr**)blob;
            IntPtr func = vtable[3];
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr> getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)func;
            return getBufferPointer(blob);
        }

        public static unsafe int GetBlobSize(IntPtr blob)
        {
            IntPtr* vtable = *(IntPtr**)blob;
            IntPtr func = vtable[4];
            delegate* unmanaged[Stdcall]<IntPtr, int> getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, int>)func;
            return getBufferSize(blob);
        }
    }
}
