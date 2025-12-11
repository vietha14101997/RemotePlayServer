#nullable enable
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Vortice.Direct3D11;
using Vortice.DXGI;

// Delegate for ID3D10Multithread::SetMultithreadProtected
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate bool SetMultithreadProtectedDelegate(IntPtr pThis, [MarshalAs(UnmanagedType.Bool)] bool bMTProtect);

/// <summary>
/// Hardware H.264 encoder using Media Foundation.
/// Supports both CPU (NV12 buffer) and GPU (D3D11 texture) input.
/// </summary>
public sealed class MediaFoundationH264Encoder : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private IMFTransform? _encoder;
    private IMFDXGIDeviceManager? _dxgiManager;
    private uint _resetToken;
    private IntPtr _deviceHandle;

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _bitrate;

    private bool _started;
    private bool _disposed;
    private long _frameIndex;
    private long _sampleTime;
    private string _inputFormat = "NV12";

    // Staging texture for CPU input (BGRA)
    private ID3D11Texture2D? _stagingTexture;
    
    // Track if GPU texture path is available
    private bool _gpuPathFailed = false;

    // NV12 buffer for color conversion
    private byte[]? _nv12Buffer;

    public event Action<byte[], uint>? OnEncodedSample;

    public int Width => _width;
    public int Height => _height;
    public bool IsHardwareEncoder { get; private set; }
    public string EncoderName { get; private set; } = "Unknown";
    public bool IsGpuTexturePath => !_gpuPathFailed;

    public MediaFoundationH264Encoder(ID3D11Device device, int width, int height, int fps = 30, int bitrateKbps = 6000)
    {
        _device = device;
        _context = device.ImmediateContext;
        _width = AlignTo2(width);
        _height = AlignTo2(height);
        _fps = fps;
        _bitrate = bitrateKbps * 1000;

        Console.WriteLine($"[MF-H264] Initializing encoder: {_width}x{_height}@{_fps}fps, {bitrateKbps}kbps");

        Initialize();
    }

    // Align DOWN to even number (H.264 requires even dimensions)
    private static int AlignTo2(int value) => value & ~1;

    private void Initialize()
    {
        // Initialize Media Foundation
        int hr = MFInterop.MFStartup(MFInterop.MF_VERSION, 0);
        if (hr < 0)
        {
            Console.WriteLine($"[MF-H264] MFStartup failed: 0x{hr:X8}");
            throw new InvalidOperationException("Failed to initialize Media Foundation");
        }

        // Create DXGI Device Manager for hardware encoder support
        hr = MFInterop.MFCreateDXGIDeviceManager(out _resetToken, out IntPtr managerPtr);
        if (hr >= 0 && managerPtr != IntPtr.Zero)
        {
            _dxgiManager = (IMFDXGIDeviceManager)Marshal.GetObjectForIUnknown(managerPtr);
            Marshal.Release(managerPtr);

            // Enable multithread protection on D3D11 device via COM
            try
            {
                var iidMultithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
                IntPtr mtPtr;
                int mtHr = Marshal.QueryInterface(_device.NativePointer, ref iidMultithread, out mtPtr);
                if (mtHr >= 0 && mtPtr != IntPtr.Zero)
                {
                    // Call SetMultithreadProtected(TRUE) - vtable index 4
                    var vtable = Marshal.ReadIntPtr(mtPtr);
                    var setProtected = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
                    var del = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtectedDelegate>(setProtected);
                    del(mtPtr, true);
                    Marshal.Release(mtPtr);
                    Console.WriteLine("[MF-H264] Multithread protection enabled");
                }
            }
            catch { }

            // ResetDevice needs ID3D11Device native pointer
            IntPtr d3dDevicePtr = _device.NativePointer;
            hr = _dxgiManager.ResetDevice(Marshal.GetObjectForIUnknown(d3dDevicePtr), _resetToken);
            if (hr >= 0)
            {
                Console.WriteLine($"[MF-H264] DXGI Device Manager created, token={_resetToken}");
            }
            else
            {
                Console.WriteLine($"[MF-H264] ResetDevice failed: 0x{hr:X8}, continuing without DXGI manager");
                _dxgiManager = null;
            }
        }

        // Find and create H.264 encoder
        if (!TryCreateEncoder())
        {
            throw new InvalidOperationException("No H.264 encoder available");
        }

        // Allocate NV12 buffer for color conversion
        int nv12Size = _width * _height * 3 / 2;
        _nv12Buffer = new byte[nv12Size];

        // Create staging texture
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
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

        Console.WriteLine($"[MF-H264] Encoder initialized: {_width}x{_height}, HW={IsHardwareEncoder}, Name={EncoderName}");
    }

    private bool TryCreateEncoder()
    {
        var encoders = EnumerateH264Encoders();
        if (encoders.Count == 0)
        {
            Console.WriteLine("[MF-H264] No H.264 encoders found");
            return false;
        }

        // Track AMD encoder failures for better diagnostics
        bool amdEncoderFailed = false;

        foreach (var (activate, name, isHardware) in encoders)
        {
            try
            {
                Console.WriteLine($"[MF-H264] Trying encoder: {name} (HW={isHardware})");
                
                // Check for AMD encoder - they have known issues with MF interface
                bool isAmdEncoder = name.Contains("AMD", StringComparison.OrdinalIgnoreCase);

                // Activate the encoder
                var iid = MFInterop.IID_IMFTransform;
                int hr = activate.ActivateObject(ref iid, out object encoderObj);
                if (hr < 0)
                {
                    Console.WriteLine($"[MF-H264] ActivateObject failed: 0x{hr:X8}");
                    continue;
                }

                _encoder = (IMFTransform)encoderObj;

                // Set D3D manager if available - BUT skip for AMD encoders (causes issues)
                bool skipD3DManager = isAmdEncoder;
                if (_dxgiManager != null && !skipD3DManager)
                {
                    IntPtr managerPtr = Marshal.GetIUnknownForObject(_dxgiManager);
                    hr = _encoder.ProcessMessage(MFInterop.MFT_MESSAGE_SET_D3D_MANAGER, managerPtr);
                    Marshal.Release(managerPtr);

                    if (hr >= 0)
                    {
                        Console.WriteLine("[MF-H264] D3D manager set on encoder");
                    }
                }
                else if (isAmdEncoder)
                {
                    Console.WriteLine("[MF-H264] Skipping D3D manager for AMD encoder (testing without)");
                }

                // Configure encoder
                if (ConfigureEncoder())
                {
                    IsHardwareEncoder = isHardware;
                    EncoderName = name;
                    Console.WriteLine($"[MF-H264] Encoder configured successfully: {name}");
                    return true;
                }

                // Track AMD failures for diagnostics
                if (isAmdEncoder)
                {
                    amdEncoderFailed = true;
                }

                // Failed to configure, release and try next
                Marshal.ReleaseComObject(_encoder);
                _encoder = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MF-H264] Encoder activation failed: {ex.Message}");
                if (_encoder != null)
                {
                    Marshal.ReleaseComObject(_encoder);
                    _encoder = null;
                }
            }
        }

        // Provide helpful diagnostics if AMD encoder failed
        if (amdEncoderFailed)
        {
            Console.WriteLine("[MF-H264] ⚠ AMD hardware encoder failed to initialize.");
            Console.WriteLine("[MF-H264] This is usually caused by missing AMD AMF Runtime (amfrt64.dll).");
            Console.WriteLine("[MF-H264] Please ensure AMD Adrenalin drivers are installed and up to date.");
        }

        return false;
    }

    private List<(IMFActivate activate, string name, bool isHardware)> EnumerateH264Encoders()
    {
        var result = new List<(IMFActivate, string, bool)>();

        // Try hardware encoders first
        EnumerateEncodersWithFlags(result, MFInterop.MFT_ENUM_FLAG_HARDWARE | MFInterop.MFT_ENUM_FLAG_SORTANDFILTER, true);

        // Then software encoders
        EnumerateEncodersWithFlags(result, MFInterop.MFT_ENUM_FLAG_SYNCMFT | MFInterop.MFT_ENUM_FLAG_SORTANDFILTER, false);

        return result;
    }

    private void EnumerateEncodersWithFlags(List<(IMFActivate, string, bool)> result, uint flags, bool isHardware)
    {
        var category = MFInterop.MFT_CATEGORY_VIDEO_ENCODER;

        int hr = MFInterop.MFTEnumEx(ref category, flags, IntPtr.Zero, IntPtr.Zero, out IntPtr activatesPtr, out uint count);
        if (hr < 0 || count == 0) return;

        try
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr activatePtr = Marshal.ReadIntPtr(activatesPtr, (int)(i * IntPtr.Size));
                if (activatePtr == IntPtr.Zero) continue;

                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(activatePtr);

                // Get friendly name
                string name = "Unknown";
                var nameGuid = MFInterop.MFT_FRIENDLY_NAME_Attribute;
                if (activate.GetAllocatedString(ref nameGuid, out string allocName, out _) >= 0)
                {
                    name = allocName;
                }

                // Filter: only include H.264 encoders
                // Skip H.265/HEVC, AV1, VP9 encoders
                string nameLower = name.ToLowerInvariant();
                bool isH264Encoder = nameLower.Contains("h264") || nameLower.Contains("h.264") || 
                                     nameLower.Contains("264") && !nameLower.Contains("265");
                
                // Software H264 Encoder MFT is always valid
                if (name == "H264 Encoder MFT") isH264Encoder = true;
                
                // Skip non-H.264 encoders
                if (nameLower.Contains("hevc") || nameLower.Contains("h265") || nameLower.Contains("h.265") ||
                    nameLower.Contains("av1") || nameLower.Contains("vp9") || nameLower.Contains("vp8") ||
                    nameLower.Contains("heif"))
                {
                    isH264Encoder = false;
                }

                if (!isH264Encoder)
                {
                    Marshal.ReleaseComObject(activate);
                    continue;
                }

                result.Add((activate, name, isHardware));
            }
        }
        finally
        {
            MFInterop.CoTaskMemFree(activatesPtr);
        }
    }

    private bool ConfigureEncoder()
    {
        if (_encoder == null) return false;

        try
        {
            var majorType = MFInterop.MF_MT_MAJOR_TYPE;
            var subType = MFInterop.MF_MT_SUBTYPE;
            var frameSize = MFInterop.MF_MT_FRAME_SIZE;
            var frameRate = MFInterop.MF_MT_FRAME_RATE;
            var avgBitrate = MFInterop.MF_MT_AVG_BITRATE;
            var interlace = MFInterop.MF_MT_INTERLACE_MODE;
            var pixelAspect = MFInterop.MF_MT_PIXEL_ASPECT_RATIO;
            var profile = MFInterop.MF_MT_MPEG2_PROFILE;
            var videoType = MFInterop.MFMediaType_Video;
            var h264Type = MFInterop.MFVideoFormat_H264;

            // Try different profiles: Main (77) first for better AMD compatibility, then High (100), then Baseline (66)
            var profiles = new[] { (77, "Main"), (100, "High"), (66, "Baseline") };
            
            foreach (var (profileValue, profileName) in profiles)
            {
                // Configure output type (H.264)
                int hr = MFInterop.MFCreateMediaType(out IntPtr outputTypePtr);
                if (hr < 0) return false;

                var outputType = (IMFMediaType)Marshal.GetObjectForIUnknown(outputTypePtr);
                Marshal.Release(outputTypePtr);

                outputType.SetGUID(ref majorType, ref videoType);
                outputType.SetGUID(ref subType, ref h264Type);
                outputType.SetUINT64(ref frameSize, (ulong)MFInterop.PackSize(_width, _height));
                outputType.SetUINT64(ref frameRate, (ulong)MFInterop.PackSize(_fps, 1));
                outputType.SetUINT32(ref avgBitrate, (uint)_bitrate);
                outputType.SetUINT32(ref interlace, 2); // MFVideoInterlace_Progressive
                outputType.SetUINT64(ref pixelAspect, (ulong)MFInterop.PackSize(1, 1));
                outputType.SetUINT32(ref profile, (uint)profileValue);

                hr = _encoder.SetOutputType(0, outputType, 0);
                Marshal.ReleaseComObject(outputType);

                if (hr < 0)
                {
                    Console.WriteLine($"[MF-H264] SetOutputType failed with {profileName} profile: 0x{hr:X8}");
                    continue;
                }

                // First, enumerate available input types from encoder
                Console.WriteLine($"[MF-H264] Enumerating available input types for {profileName} profile...");
                var availableInputTypes = new List<(Guid format, string name)>();
                for (uint i = 0; i < 20; i++)
                {
                    hr = _encoder.GetInputAvailableType(0, i, out IMFMediaType? availType);
                    if (hr < 0) break;
                    if (availType == null) continue;

                    try
                    {
                        var subTypeGuid = MFInterop.MF_MT_SUBTYPE;
                        hr = availType.GetGUID(ref subTypeGuid, out Guid formatGuid);
                        if (hr >= 0)
                        {
                            string formatName = GetFormatName(formatGuid);
                            availableInputTypes.Add((formatGuid, formatName));
                            Console.WriteLine($"[MF-H264]   Available input: {formatName}");
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(availType);
                    }
                }

                // Try to set input type - PREFER NV12 since Video Processor outputs NV12
                bool inputTypeSet = false;
                
                // First: try NV12 if available (this is what Video Processor outputs)
                if (availableInputTypes.Any(t => t.name == "NV12"))
                {
                    if (TrySetInputType(MFInterop.MFVideoFormat_NV12, "NV12"))
                    {
                        inputTypeSet = true;
                    }
                }
                
                // Second: try other available types
                if (!inputTypeSet && availableInputTypes.Count > 0)
                {
                    // Prefer formats in order: NV12, ARGB32, RGB32, then others
                    var preferredOrder = new[] { "NV12", "ARGB32", "RGB32" };
                    var sortedTypes = availableInputTypes
                        .OrderBy(t => {
                            int idx = Array.IndexOf(preferredOrder, t.name);
                            return idx >= 0 ? idx : 100;
                        })
                        .ToList();
                    
                    foreach (var (formatGuid, formatName) in sortedTypes)
                    {
                        if (TrySetInputType(formatGuid, formatName))
                        {
                            inputTypeSet = true;
                            break;
                        }
                    }
                }

                // Fallback: try common formats directly
                if (!inputTypeSet)
                {
                    var fallbackFormats = new[] {
                        (MFInterop.MFVideoFormat_NV12, "NV12"),
                        (MFInterop.MFVideoFormat_ARGB32, "ARGB32"),
                        (MFInterop.MFVideoFormat_RGB32, "RGB32")
                    };

                    foreach (var (formatGuid, formatName) in fallbackFormats)
                    {
                        if (TrySetInputType(formatGuid, formatName))
                        {
                            inputTypeSet = true;
                            break;
                        }
                    }
                }

                if (inputTypeSet)
                {
                    Console.WriteLine($"[MF-H264] Using {profileName} profile");
                    return true;
                }
            }

            Console.WriteLine($"[MF-H264] SetInputType failed for all formats and profiles");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MF-H264] ConfigureEncoder error: {ex.Message}");
            return false;
        }
    }

    private bool TrySetInputType(Guid formatGuid, string formatName)
    {
        if (_encoder == null) return false;

        var majorType = MFInterop.MF_MT_MAJOR_TYPE;
        var subType = MFInterop.MF_MT_SUBTYPE;
        var frameSize = MFInterop.MF_MT_FRAME_SIZE;
        var frameRate = MFInterop.MF_MT_FRAME_RATE;
        var interlace = MFInterop.MF_MT_INTERLACE_MODE;
        var pixelAspect = MFInterop.MF_MT_PIXEL_ASPECT_RATIO;
        var videoType = MFInterop.MFMediaType_Video;

        int hr = MFInterop.MFCreateMediaType(out IntPtr inputTypePtr);
        if (hr < 0) return false;

        var inputType = (IMFMediaType)Marshal.GetObjectForIUnknown(inputTypePtr);
        Marshal.Release(inputTypePtr);

        try
        {
            var inputFormat = formatGuid;
            inputType.SetGUID(ref majorType, ref videoType);
            inputType.SetGUID(ref subType, ref inputFormat);
            inputType.SetUINT64(ref frameSize, (ulong)MFInterop.PackSize(_width, _height));
            inputType.SetUINT64(ref frameRate, (ulong)MFInterop.PackSize(_fps, 1));
            inputType.SetUINT32(ref interlace, 2);
            inputType.SetUINT64(ref pixelAspect, (ulong)MFInterop.PackSize(1, 1));

            hr = _encoder.SetInputType(0, inputType, 0);
            if (hr >= 0)
            {
                _inputFormat = formatName;
                Console.WriteLine($"[MF-H264] Input format set: {formatName}");
                
                // Configure encoder settings via ICodecAPI
                try
                {
                    var codecApi = (ICodecAPI)_encoder;
                    ConfigureCodecApi(codecApi);
                }
                catch
                {
                    Console.WriteLine("[MF-H264] ICodecAPI not available");
                }
                
                return true;
            }
            else
            {
                Console.WriteLine($"[MF-H264] SetInputType({formatName}) failed: 0x{hr:X8}");
                return false;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(inputType);
        }
    }

    private static string GetFormatName(Guid formatGuid)
    {
        if (formatGuid == MFInterop.MFVideoFormat_NV12) return "NV12";
        if (formatGuid == MFInterop.MFVideoFormat_ARGB32) return "ARGB32";
        if (formatGuid == MFInterop.MFVideoFormat_RGB32) return "RGB32";
        
        // Check for other common formats by FourCC
        byte[] bytes = formatGuid.ToByteArray();
        uint fourcc = BitConverter.ToUInt32(bytes, 0);
        
        // Common FourCC codes
        if (fourcc == 0x56555949) return "IYUV";  // 'IYUV'
        if (fourcc == 0x32595559) return "YUY2";  // 'YUY2'
        if (fourcc == 0x59565955) return "UYVY";  // 'UYVY'
        if (fourcc == 0x50313234) return "P010";  // 'P010'
        if (fourcc == 0x36313050) return "P016";  // 'P016'
        
        return formatGuid.ToString().Substring(0, 8);
    }

    private void ConfigureCodecApi(ICodecAPI codecApi)
    {
        // Set low latency mode
        var lowLatency = MFInterop.CODECAPI_AVLowLatencyMode;
        if (codecApi.IsSupported(ref lowLatency) >= 0)
        {
            var variant = new MFInterop.PROPVARIANT { vt = 11, data1 = (IntPtr)1 }; // VT_BOOL = true
            codecApi.SetValue(ref lowLatency, ref variant);
            Console.WriteLine("[MF-H264] Low latency mode enabled");
        }

        // Set GOP size
        var gopSize = MFInterop.CODECAPI_AVEncMPVGOPSize;
        if (codecApi.IsSupported(ref gopSize) >= 0)
        {
            var variant = new MFInterop.PROPVARIANT { vt = 19, data1 = (IntPtr)_fps }; // VT_UI4
            codecApi.SetValue(ref gopSize, ref variant);
            Console.WriteLine($"[MF-H264] GOP size set to {_fps}");
        }

        // Set rate control to CBR
        var rateControl = MFInterop.CODECAPI_AVEncCommonRateControlMode;
        if (codecApi.IsSupported(ref rateControl) >= 0)
        {
            var variant = new MFInterop.PROPVARIANT { vt = 19, data1 = (IntPtr)2 }; // eAVEncCommonRateControlMode_CBR
            codecApi.SetValue(ref rateControl, ref variant);
            Console.WriteLine("[MF-H264] Rate control set to CBR");
        }

        // Disable CABAC for baseline profile compatibility
        var cabac = MFInterop.CODECAPI_AVEncH264CABACEnable;
        if (codecApi.IsSupported(ref cabac) >= 0)
        {
            var variant = new MFInterop.PROPVARIANT { vt = 11, data1 = IntPtr.Zero }; // VT_BOOL = false
            codecApi.SetValue(ref cabac, ref variant);
            Console.WriteLine("[MF-H264] CABAC disabled");
        }
    }

    public void Start()
    {
        if (_started || _encoder == null) return;

        int hr = _encoder.ProcessMessage(MFInterop.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        if (hr < 0) Console.WriteLine($"[MF-H264] BEGIN_STREAMING failed: 0x{hr:X8}");

        hr = _encoder.ProcessMessage(MFInterop.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        if (hr < 0) Console.WriteLine($"[MF-H264] START_OF_STREAM failed: 0x{hr:X8}");

        _started = true;
        _frameIndex = 0;
        _sampleTime = 0;
        Console.WriteLine("[MF-H264] Encoder started");
    }

    public void Stop()
    {
        if (!_started || _encoder == null) return;

        _encoder.ProcessMessage(MFInterop.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
        _encoder.ProcessMessage(MFInterop.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
        DrainEncoder();
        _encoder.ProcessMessage(MFInterop.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);

        _started = false;
        Console.WriteLine("[MF-H264] Encoder stopped");
    }

    /// <summary>
    /// Encode a BGRA frame from CPU memory
    /// </summary>
    public void EncodeBgraFrame(ReadOnlySpan<byte> bgraData, int stride)
    {
        if (!_started || _encoder == null || _nv12Buffer == null) return;

        try
        {
            // Convert BGRA to NV12
            ConvertBgraToNv12(bgraData, stride, _nv12Buffer);

            // Create input sample with NV12 data
            EncodeNv12Frame(_nv12Buffer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MF-H264] Encode error: {ex.Message}");
        }
    }

    /// <summary>
    /// Encode directly from D3D11 BGRA texture (copies to CPU, then encodes)
    /// </summary>
    public void EncodeTexture(ID3D11Texture2D texture)
    {
        if (!_started || _stagingTexture == null) return;

        try
        {
            // Copy texture to staging
            _context.CopyResource(_stagingTexture, texture);

            // Map and read
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int size = _height * (int)mapped.RowPitch;
                var data = new byte[size];
                Marshal.Copy(mapped.DataPointer, data, 0, Math.Min(size, data.Length));
                EncodeBgraFrame(data, (int)mapped.RowPitch);
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MF-H264] Texture encode error: {ex.Message}");
        }
    }

    /// <summary>
    /// Encode directly from NV12 D3D11 texture (zero-copy path).
    /// Returns false if GPU path not supported (caller should use EncodeTexture instead).
    /// </summary>
    public bool EncodeNv12Texture(ID3D11Texture2D nv12Texture)
    {
        if (!_started || _encoder == null) return false;

        // If GPU path already failed, tell caller to use CPU path
        if (_gpuPathFailed)
        {
            return false;
        }

        try
        {
            // Create MF sample from D3D11 texture
            int hr = MFInterop.MFCreateSample(out IntPtr samplePtr);
            if (hr < 0)
            {
                Console.WriteLine($"[MF-H264] MFCreateSample failed: 0x{hr:X8}");
                return false;
            }

            var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
            Marshal.Release(samplePtr);

            try
            {
                // Create DXGI surface buffer from texture
                var iid = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec"); // IID_IDXGISurface
                IntPtr texturePtr = nv12Texture.NativePointer;

                hr = MFInterop.MFCreateDXGISurfaceBuffer(ref iid, texturePtr, 0, false, out IntPtr bufferPtr);

                if (hr < 0)
                {
                    // GPU path failed - remember this so caller uses CPU path
                    if (!_gpuPathFailed)
                    {
                        Console.WriteLine($"[MF-H264] GPU texture path not supported (0x{hr:X8}), use EncodeTexture for CPU path");
                        _gpuPathFailed = true;
                    }
                    Marshal.ReleaseComObject(sample);
                    return false;
                }

                var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPtr);
                Marshal.Release(bufferPtr);

                // Set buffer length (NV12 size)
                int nv12Size = _width * _height * 3 / 2;
                buffer.SetCurrentLength(nv12Size);

                sample.AddBuffer(buffer);

                // Set sample time and duration
                long duration = 10_000_000 / _fps; // 100ns units
                sample.SetSampleTime(_sampleTime);
                sample.SetSampleDuration(duration);
                _sampleTime += duration;

                // Set keyframe flag if needed
                if (_frameIndex % _fps == 0)
                {
                    var cleanPoint = MFInterop.MFSampleExtension_CleanPoint;
                    sample.SetUINT32(ref cleanPoint, 1);
                }

                _frameIndex++;

                // Process input
                hr = _encoder.ProcessInput(0, sample, 0);
                if (hr < 0 && hr != MFInterop.MF_E_NOTACCEPTING)
                {
                    Console.WriteLine($"[MF-H264] ProcessInput (texture) failed: 0x{hr:X8}");
                }
                else
                {
                    ProcessEncoderOutput();
                }

                Marshal.ReleaseComObject(buffer);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MF-H264] EncodeNv12Texture error: {ex.Message}");
            return false;
        }
    }

    private void EncodeNv12Frame(byte[] nv12Data)
    {
        if (_encoder == null) return;

        // Create MF sample
        int hr = MFInterop.MFCreateSample(out IntPtr samplePtr);
        if (hr < 0) return;

        var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
        Marshal.Release(samplePtr);

        try
        {
            // Create buffer
            hr = MFInterop.MFCreateMemoryBuffer(nv12Data.Length, out IntPtr bufferPtr);
            if (hr < 0)
            {
                Marshal.ReleaseComObject(sample);
                return;
            }

            var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPtr);
            Marshal.Release(bufferPtr);

            // Copy NV12 data to buffer
            hr = buffer.Lock(out IntPtr dataPtr, out _, out _);
            if (hr >= 0)
            {
                Marshal.Copy(nv12Data, 0, dataPtr, nv12Data.Length);
                buffer.Unlock();
                buffer.SetCurrentLength(nv12Data.Length);
            }

            sample.AddBuffer(buffer);

            // Set sample time and duration
            long duration = 10_000_000 / _fps; // 100ns units
            sample.SetSampleTime(_sampleTime);
            sample.SetSampleDuration(duration);
            _sampleTime += duration;

            // Set keyframe flag if needed
            if (_frameIndex % _fps == 0)
            {
                var cleanPoint = MFInterop.MFSampleExtension_CleanPoint;
                sample.SetUINT32(ref cleanPoint, 1);
            }

            _frameIndex++;

            // Process input
            hr = _encoder.ProcessInput(0, sample, 0);
            if (hr < 0 && hr != MFInterop.MF_E_NOTACCEPTING)
            {
                Console.WriteLine($"[MF-H264] ProcessInput failed: 0x{hr:X8}");
            }

            // Get output
            ProcessEncoderOutput();

            Marshal.ReleaseComObject(buffer);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private void ProcessEncoderOutput()
    {
        if (_encoder == null) return;

        while (true)
        {
            // Get output stream info
            _encoder.GetOutputStreamInfo(0, out var streamInfo);

            // Create output sample
            int hr = MFInterop.MFCreateSample(out IntPtr outSamplePtr);
            if (hr < 0) break;

            var outSample = (IMFSample)Marshal.GetObjectForIUnknown(outSamplePtr);
            Marshal.Release(outSamplePtr);

            // Create output buffer
            int bufferSize = (int)Math.Max(streamInfo.cbSize, _width * _height * 2);
            hr = MFInterop.MFCreateMemoryBuffer(bufferSize, out IntPtr outBufferPtr);
            if (hr < 0)
            {
                Marshal.ReleaseComObject(outSample);
                break;
            }

            var outBuffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(outBufferPtr);
            Marshal.Release(outBufferPtr);

            outSample.AddBuffer(outBuffer);

            // Create output data buffer
            var outputBuffer = new MFInterop.MFT_OUTPUT_DATA_BUFFER
            {
                dwStreamID = 0,
                pSample = Marshal.GetIUnknownForObject(outSample),
                dwStatus = 0,
                pEvents = IntPtr.Zero
            };

            var outputBuffers = new MFInterop.MFT_OUTPUT_DATA_BUFFER[] { outputBuffer };

            // Process output
            hr = _encoder.ProcessOutput(0, 1, outputBuffers, out uint status);

            if (hr == MFInterop.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                Marshal.Release(outputBuffer.pSample);
                Marshal.ReleaseComObject(outBuffer);
                Marshal.ReleaseComObject(outSample);
                break;
            }

            if (hr < 0)
            {
                Marshal.Release(outputBuffer.pSample);
                Marshal.ReleaseComObject(outBuffer);
                Marshal.ReleaseComObject(outSample);
                break;
            }

            // Extract encoded data
            ExtractEncodedData(outSample);

            Marshal.Release(outputBuffer.pSample);
            Marshal.ReleaseComObject(outBuffer);
            Marshal.ReleaseComObject(outSample);

            // Check if more output is available
            if ((outputBuffers[0].dwStatus & MFInterop.MFT_OUTPUT_DATA_BUFFER_INCOMPLETE) == 0)
                break;
        }
    }

    private void ExtractEncodedData(IMFSample sample)
    {
        try
        {
            sample.ConvertToContiguousBuffer(out var buffer);

            int hr = buffer.Lock(out IntPtr dataPtr, out _, out int currentLength);
            if (hr >= 0 && currentLength > 0)
            {
                var encodedData = new byte[currentLength];
                Marshal.Copy(dataPtr, encodedData, 0, currentLength);
                buffer.Unlock();

                // Calculate duration in ms
                uint durationMs = (uint)(1000 / _fps);

                // Log keyframes
                if (encodedData.Length > 4)
                {
                    bool hasKeyframe = ContainsKeyframe(encodedData);
                    if (hasKeyframe)
                    {
                        Console.WriteLine($"[MF-H264] Keyframe: {encodedData.Length} bytes");
                    }
                }

                OnEncodedSample?.Invoke(encodedData, durationMs);
            }

            Marshal.ReleaseComObject(buffer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MF-H264] ExtractEncodedData error: {ex.Message}");
        }
    }

    private static bool ContainsKeyframe(byte[] data)
    {
        // Look for IDR NAL unit (type 5)
        for (int i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                int offset = (data[i + 2] == 1) ? 3 : (data[i + 2] == 0 && data[i + 3] == 1) ? 4 : 0;
                if (offset > 0 && i + offset < data.Length)
                {
                    int nalType = data[i + offset] & 0x1F;
                    if (nalType == 5 || nalType == 7) return true;
                }
            }
        }
        return false;
    }

    private void DrainEncoder()
    {
        // Process remaining output
        for (int i = 0; i < 10; i++)
        {
            ProcessEncoderOutput();
        }
    }

    private void ConvertBgraToNv12(ReadOnlySpan<byte> bgra, int stride, byte[] nv12)
    {
        int ySize = _width * _height;
        int yIndex = 0;
        int uvIndex = ySize;

        for (int y = 0; y < _height; y++)
        {
            int srcRowOffset = y * stride;

            for (int x = 0; x < _width; x++)
            {
                int srcIndex = srcRowOffset + x * 4;

                byte b = srcIndex < bgra.Length ? bgra[srcIndex] : (byte)0;
                byte g = srcIndex + 1 < bgra.Length ? bgra[srcIndex + 1] : (byte)0;
                byte r = srcIndex + 2 < bgra.Length ? bgra[srcIndex + 2] : (byte)0;

                // BT.601 Y calculation
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yIndex++] = (byte)Math.Clamp(yVal, 16, 235);

                // UV subsampling (2x2)
                if ((y & 1) == 0 && (x & 1) == 0 && uvIndex + 1 < nv12.Length)
                {
                    int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    nv12[uvIndex++] = (byte)Math.Clamp(uVal, 0, 255);
                    nv12[uvIndex++] = (byte)Math.Clamp(vVal, 0, 255);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _stagingTexture?.Dispose();

        if (_encoder != null)
        {
            Marshal.ReleaseComObject(_encoder);
            _encoder = null;
        }

        if (_dxgiManager != null)
        {
            if (_deviceHandle != IntPtr.Zero)
            {
                _dxgiManager.CloseDeviceHandle(_deviceHandle);
            }
            Marshal.ReleaseComObject(_dxgiManager);
            _dxgiManager = null;
        }

        try { MFInterop.MFShutdown(); } catch { }

        Console.WriteLine("[MF-H264] Encoder disposed");
    }
}
