#nullable enable
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

internal static class DxInterop
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDirect3DDeviceFromDxgi(IntPtr dxgiDevice)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var devicePtr);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);
    }

    [DllImport("combase.dll")] public static extern int RoGetActivationFactory(IntPtr hstringClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] public static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);
    [DllImport("combase.dll")] public static extern int WindowsDeleteString(IntPtr hstring);

    public static readonly Guid IID_IActivationFactory = new Guid("00000035-0000-0000-C000-000000000046");
}

internal static class WgcInterop
{
    // ===== Window filters to avoid invalid captures =====
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, ref RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_VISIBLE = 0x10000000;
    const int DWMWA_CLOAKED = 14;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    public static bool IsCapturableWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((style & WS_VISIBLE) != WS_VISIBLE) return false;
        if ((ex & WS_EX_TOOLWINDOW) == WS_EX_TOOLWINDOW) return false;

        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        var bounds = new RECT();
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ref bounds, Marshal.SizeOf<RECT>()) == 0)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        }
        return true;
    }

    // ===== WinRT interop to create GraphicsCaptureItem =====
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr hwnd, [In] ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr hmon, [In] ref Guid iid, out IntPtr item);
    }

    public static GraphicsCaptureItem CreateItemForHwndWithFallback(IntPtr hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is not supported on this system.");
        if (!IsCapturableWindow(hwnd))
            throw new InvalidOperationException("Selected window cannot be captured (tool/cloaked/invisible).");

        const string ClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        IntPtr hstr = IntPtr.Zero, actPtr = IntPtr.Zero, interopPtr = IntPtr.Zero, itemPtr = IntPtr.Zero;
        try
        {
            int hr = DxInterop.WindowsCreateString(ClassId, ClassId.Length, out hstr);
            if (hr != 0) throw new COMException($"WindowsCreateString failed: 0x{hr:X8}", hr);

            Guid iidAct = DxInterop.IID_IActivationFactory;
            hr = DxInterop.RoGetActivationFactory(hstr, ref iidAct, out actPtr);
            if (hr != 0 || actPtr == IntPtr.Zero) throw new COMException($"RoGetActivationFactory failed: 0x{hr:X8}", hr);

            Guid iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = Marshal.QueryInterface(actPtr, in iidInterop, out interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero) throw new COMException($"QueryInterface(IGraphicsCaptureItemInterop) failed: 0x{hr:X8}", hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            Guid iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            hr = interop.CreateForWindow(hwnd, ref iidItem, out itemPtr);
            if (hr != 0 || itemPtr == IntPtr.Zero)
            {
                if (hr == unchecked((int)0x80004002))
                    throw new COMException("E_NOINTERFACE: cần IID của IGraphicsCaptureItem", hr);
                throw new COMException($"GraphicsCaptureItem.CreateForWindow failed: 0x{hr:X8}", hr);
            }

            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            itemPtr = IntPtr.Zero;
            return item;
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (interopPtr != IntPtr.Zero) Marshal.Release(interopPtr);
            if (actPtr != IntPtr.Zero) Marshal.Release(actPtr);
            if (hstr != IntPtr.Zero) DxInterop.WindowsDeleteString(hstr);
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is not supported on this system.");

        const string ClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        IntPtr hstr = IntPtr.Zero, actPtr = IntPtr.Zero, interopPtr = IntPtr.Zero, itemPtr = IntPtr.Zero;
        try
        {
            int hr = DxInterop.WindowsCreateString(ClassId, ClassId.Length, out hstr);
            if (hr != 0) throw new COMException($"WindowsCreateString failed: 0x{hr:X8}", hr);

            Guid iidAct = DxInterop.IID_IActivationFactory;
            hr = DxInterop.RoGetActivationFactory(hstr, ref iidAct, out actPtr);
            if (hr != 0 || actPtr == IntPtr.Zero) throw new COMException($"RoGetActivationFactory failed: 0x{hr:X8}", hr);

            Guid iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = Marshal.QueryInterface(actPtr, in iidInterop, out interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero) throw new COMException($"QueryInterface(IGraphicsCaptureItemInterop) failed: 0x{hr:X8}", hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            Guid iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            hr = interop.CreateForMonitor(hmon, ref iidItem, out itemPtr);
            if (hr != 0 || itemPtr == IntPtr.Zero)
                throw new COMException($"GraphicsCaptureItem.CreateForMonitor failed: 0x{hr:X8}", hr);

            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            itemPtr = IntPtr.Zero;
            return item;
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (interopPtr != IntPtr.Zero) Marshal.Release(interopPtr);
            if (actPtr != IntPtr.Zero) Marshal.Release(actPtr);
            if (hstr != IntPtr.Zero) DxInterop.WindowsDeleteString(hstr);
        }
    }

    [ComImport, Guid("A9B3D012-3DF2-4DC3-B3C6-B90C2F2E3FF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }
    
    // IGraphicsSurfaceNative - alternative interface for getting DXGI surface
    [ComImport, Guid("0BF4A146-13C1-4694-BEE3-7ABF15EAF586"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    interface IGraphicsSurfaceNative 
    { 
        // Description property
        void GetDescription(out SurfaceDescription desc);
    }
    
    [StructLayout(LayoutKind.Sequential)]
    struct SurfaceDescription
    {
        public int Width;
        public int Height;
        public int Format;
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }

    public static ID3D11Texture2D? TryGetTextureFast(IDirect3DSurface surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        IntPtr nativePtr = IntPtr.Zero, accessPtr = IntPtr.Zero, texPtr = IntPtr.Zero;
        bool success = false;
        bool addedRef = false;
        try
        {
            // For CsWinRT objects, use IWinRTObject to get the native ABI pointer
            if (surface is WinRT.IWinRTObject winrtObj)
            {
                nativePtr = winrtObj.NativeObject.ThisPtr;
                if (nativePtr != IntPtr.Zero)
                {
                    Marshal.AddRef(nativePtr);
                    addedRef = true;
                }
            }
            
            // Fallback to Marshal.GetIUnknownForObject if IWinRTObject didn't work
            if (nativePtr == IntPtr.Zero)
            {
                nativePtr = Marshal.GetIUnknownForObject(surface);
                addedRef = true;
            }
            
            if (nativePtr == IntPtr.Zero) { Console.WriteLine("[WGC] TryGetTextureFast: Failed to get native pointer"); return null; }
            
            // Try IDirect3DDxgiInterfaceAccess first
            Guid iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            int hr = Marshal.QueryInterface(nativePtr, in iidAccess, out accessPtr);
            if (hr == 0 && accessPtr != IntPtr.Zero)
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                Guid iidTex2D = typeof(ID3D11Texture2D).GUID;
                texPtr = access.GetInterface(ref iidTex2D);
                if (texPtr != IntPtr.Zero)
                {
                    var result = new ID3D11Texture2D(texPtr);
                    success = true;
                    Console.WriteLine("[WGC] TryGetTextureFast: SUCCESS via IDirect3DDxgiInterfaceAccess");
                    return result;
                }
            }
            
            // Try direct QueryInterface for ID3D11Texture2D
            Guid iidTex2DDirect = typeof(ID3D11Texture2D).GUID;
            hr = Marshal.QueryInterface(nativePtr, in iidTex2DDirect, out texPtr);
            if (hr == 0 && texPtr != IntPtr.Zero)
            {
                var result = new ID3D11Texture2D(texPtr);
                success = true;
                Console.WriteLine("[WGC] TryGetTextureFast: SUCCESS via direct ID3D11Texture2D QI");
                return result;
            }
            
            // Try IDXGIResource
            Guid iidDxgiResource = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
            IntPtr dxgiResPtr = IntPtr.Zero;
            hr = Marshal.QueryInterface(nativePtr, in iidDxgiResource, out dxgiResPtr);
            if (hr == 0 && dxgiResPtr != IntPtr.Zero)
            {
                Console.WriteLine("[WGC] TryGetTextureFast: Found IDXGIResource, trying to get texture...");
                hr = Marshal.QueryInterface(dxgiResPtr, in iidTex2DDirect, out texPtr);
                Marshal.Release(dxgiResPtr);
                if (hr == 0 && texPtr != IntPtr.Zero)
                {
                    var result = new ID3D11Texture2D(texPtr);
                    success = true;
                    Console.WriteLine("[WGC] TryGetTextureFast: SUCCESS via IDXGIResource");
                    return result;
                }
            }
            
            // Try IDXGISurface (GUID: cafcb56c-6ac3-4889-bf47-9e23bbd260ec)
            Guid iidDxgiSurface = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
            IntPtr dxgiSurfPtr = IntPtr.Zero;
            hr = Marshal.QueryInterface(nativePtr, in iidDxgiSurface, out dxgiSurfPtr);
            if (hr == 0 && dxgiSurfPtr != IntPtr.Zero)
            {
                Console.WriteLine("[WGC] TryGetTextureFast: Found IDXGISurface, trying to get texture...");
                hr = Marshal.QueryInterface(dxgiSurfPtr, in iidTex2DDirect, out texPtr);
                Marshal.Release(dxgiSurfPtr);
                if (hr == 0 && texPtr != IntPtr.Zero)
                {
                    var result = new ID3D11Texture2D(texPtr);
                    success = true;
                    Console.WriteLine("[WGC] TryGetTextureFast: SUCCESS via IDXGISurface");
                    return result;
                }
            }
            
            // Log which interfaces are available for debugging
            Console.WriteLine($"[WGC] TryGetTextureFast: All methods failed. Surface type: {surface.GetType().FullName}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WGC] TryGetTextureFast exception: {ex.Message}");
            return null;
        }
        finally
        {
            if (!success && texPtr != IntPtr.Zero) Marshal.Release(texPtr);
            if (accessPtr != IntPtr.Zero) Marshal.Release(accessPtr);
            if (addedRef && nativePtr != IntPtr.Zero) Marshal.Release(nativePtr);
        }
    }

    static SoftwareBitmap CreateBitmapFromSurface(IDirect3DSurface surface)
    {
        var task = System.Threading.Tasks.Task.Run(async () =>
        {
            return await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Premultiplied);
        });
        
        if (task.Wait(TimeSpan.FromSeconds(3)))
        {
            return task.Result;
        }
        
        Console.WriteLine("[WGC] CPU fallback: CreateCopyFromSurfaceAsync timeout");
        throw new TimeoutException("CreateCopyFromSurfaceAsync timed out");
    }
    
    public unsafe static void CopySurfaceToScratch_CPU(IDirect3DSurface surface, byte[] scratch, uint w, uint h, uint strideOut)
    {
        SoftwareBitmap sb;
        try
        {
            sb = CreateBitmapFromSurface(surface);
        }
        catch (AggregateException ae)
        {
            Console.WriteLine($"[WGC] CPU fallback async error: {ae.InnerException?.Message ?? ae.Message}");
            throw ae.InnerException ?? ae;
        }
        
        using var bitmap = sb;
        using var conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var buffer = conv.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        IntPtr refAbi = IntPtr.Zero, byteAccessPtr = IntPtr.Zero;
        try
        {
            refAbi = WinRT.MarshalInterface<Windows.Foundation.IMemoryBufferReference>.FromManaged(reference);
            Guid iidMBA = typeof(IMemoryBufferByteAccess).GUID;
            int hr = Marshal.QueryInterface(refAbi, in iidMBA, out byteAccessPtr);
            if (hr != 0 || byteAccessPtr == IntPtr.Zero)
                Marshal.ThrowExceptionForHR(hr);

            var byteAccess = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(byteAccessPtr);
            byteAccess.GetBuffer(out byte* srcBase, out uint cap);

            var plane = buffer.GetPlaneDescription(0);

            // ⭐ Clamp tuyệt đối theo thông số plane thực tế
            int rows = Math.Min((int)h, plane.Height);
            uint rowBytes = (uint)Math.Min((int)strideOut, Math.Min(plane.Stride, plane.Width * 4));

            for (int y = 0; y < rows; y++)
            {
                byte* srcRow = srcBase + plane.StartIndex + y * plane.Stride;
                fixed (byte* dst = &scratch[y * strideOut])
                {
                    Buffer.MemoryCopy(srcRow, dst, strideOut, rowBytes);
                }
            }
        }
        finally
        {
            if (byteAccessPtr != IntPtr.Zero) Marshal.Release(byteAccessPtr);
            if (refAbi != IntPtr.Zero) WinRT.MarshalInterface<Windows.Foundation.IMemoryBufferReference>.DisposeAbi(refAbi);
        }
    }

    // Use EnumDisplaySettings to get actual current resolution (more accurate than DXGI)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion;
        public short dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels, dmBitsPerPel;
        public int dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplaySettingsA(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
    
    const int ENUM_CURRENT_SETTINGS = -1;
    
    static (int w, int h) GetCurrentResolution(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettingsA(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return (dm.dmPelsWidth, dm.dmPelsHeight);
        return (0, 0);
    }

    public static System.Collections.Generic.List<(IntPtr hmon, string name, int width, int height)> ListMonitorsDXGI()
    {
        var list = new System.Collections.Generic.List<(IntPtr, string, int, int)>();

        // Tạo factory (dùng kiểu 1 cho tương thích rộng)
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint ai = 0; ; ai++)
        {
            // ⚠️ API kiểu out + Result
            if (factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Failure) break;
            using (adapter)
            {
                for (uint oi = 0; ; oi++)
                {
                    if (adapter.EnumOutputs(oi, out IDXGIOutput output).Failure) break;
                    using (output)
                    {
                        var desc = output.Description;
                        string devName = desc.DeviceName ?? $"Monitor{list.Count}";
                        
                        // Use EnumDisplaySettings to get actual current resolution
                        var (w, h) = GetCurrentResolution(devName);
                        if (w == 0 || h == 0)
                        {
                            // Fallback to DXGI coordinates if EnumDisplaySettings fails
                            w = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                            h = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                        }

                        list.Add((desc.Monitor, devName, w, h));
                    }
                }
            }
        }
        return list;
    }
}

public sealed class WgcCapture : IDisposable
{
    readonly ID3D11Device _d3d;
    readonly ID3D11DeviceContext _ctx;
    readonly IDirect3DDevice _dxDevice;
    GraphicsCaptureItem _item = null!;
    Direct3D11CaptureFramePool _pool = null!;
    GraphicsCaptureSession _session = null!;
    ID3D11Texture2D _staging = null!;
    uint _w, _h, _stride;
    byte[] _scratch = Array.Empty<byte>();

    public event Action<byte[], int, int, int>? OnFrame;

    public (uint w, uint h) Size => (_w, _h);

    // Find adapter that owns a specific monitor
    static IDXGIAdapter1? FindAdapterForMonitor(IntPtr hmon)
    {
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; ; ai++)
        {
            if (factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Failure) break;
            for (uint oi = 0; ; oi++)
            {
                if (adapter.EnumOutputs(oi, out IDXGIOutput output).Failure) break;
                var desc = output.Description;
                output.Dispose();
                if (desc.Monitor == hmon)
                {
                    Console.WriteLine($"[WGC] Found adapter for monitor: {adapter.Description.Description}");
                    return adapter;
                }
            }
            adapter.Dispose();
        }
        return null;
    }

    // Create D3D device on specified adapter or default
    static (ID3D11Device d3d, ID3D11DeviceContext ctx) CreateD3DDevice(IDXGIAdapter1? adapter = null)
    {
        var levels = new[] { FeatureLevel.Level_11_0 };
        ID3D11Device d3d;
        ID3D11DeviceContext ctx;
        
        if (adapter != null)
        {
            Console.WriteLine($"[WGC] Creating D3D device on adapter: {adapter.Description.Description}");
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown, // Must use Unknown when specifying adapter
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels,
                out d3d,
                out ctx
            );
        }
        else
        {
            Console.WriteLine("[WGC] Creating D3D device on default adapter");
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels,
                out d3d,
                out ctx
            );
        }
        return (d3d, ctx);
    }

    public WgcCapture(IntPtr hwnd)
    {
        (_d3d, _ctx) = CreateD3DDevice(null);
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _dxDevice = DxInterop.CreateDirect3DDeviceFromDxgi(dxgi.NativePointer);

        _item = WgcInterop.CreateItemForHwndWithFallback(hwnd);
        InitCommon(_item);
    }

    public WgcCapture(IntPtr hmon, bool isMonitor)
    {
        // Find and use the adapter that owns this monitor
        using var adapter = FindAdapterForMonitor(hmon);
        (_d3d, _ctx) = CreateD3DDevice(adapter);
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _dxDevice = DxInterop.CreateDirect3DDeviceFromDxgi(dxgi.NativePointer);

        _item = WgcInterop.CreateItemForMonitor(hmon);
        InitCommon(_item);
    }

    void InitCommon(GraphicsCaptureItem item)
    {
        var size = item.Size;
        _w = (uint)Math.Max(16, size.Width);
        _h = (uint)Math.Max(16, size.Height);
        _stride = _w * 4;

        Console.WriteLine($"[WGC] Creating FramePool: {size.Width}x{size.Height}");
        // Use 2 buffers for better stability
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_dxDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        Console.WriteLine("[WGC] FramePool created, subscribing to FrameArrived...");
        _pool.FrameArrived += OnFrameArrived;
        Console.WriteLine("[WGC] Creating capture session...");
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;
        Console.WriteLine("[WGC] Capture session created");

        item.Closed += (s, e) =>
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
        };

        _staging = CreateStaging(_w, _h);
        EnsureScratch();
    }

    ID3D11Texture2D CreateStaging(uint w, uint h)
    {
        var desc = new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        };
        return _d3d.CreateTexture2D(desc);
    }

    void EnsureScratch()
    {
        uint need = _h * _stride;
        if (_scratch.Length != need) _scratch = new byte[need];
    }

    public void Start()
    {
        Console.WriteLine("[WGC] StartCapture() called");
        _session.StartCapture();
        Console.WriteLine("[WGC] StartCapture() completed - frames should start arriving");
    }

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Console.WriteLine("[WGC] OnFrameArrived triggered");
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) { Console.WriteLine("[WGC] TryGetNextFrame returned null"); return; }

            var sz = frame.ContentSize;
            if (sz.Width != _w || sz.Height != _h)
            {
                Console.WriteLine($"[WGC] Size mismatch: pool={_w}x{_h}, frame={sz.Width}x{sz.Height} - recreating pool");
                _w = (uint)Math.Max(16, sz.Width);
                _h = (uint)Math.Max(16, sz.Height);
                _stride = _w * 4;
                _staging.Dispose();
                _staging = CreateStaging(_w, _h);
                EnsureScratch();

                try
                {
                    // Unsubscribe before Recreate
                    _pool.FrameArrived -= OnFrameArrived;
                    _pool.Recreate(_dxDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1,
                        new SizeInt32 { Width = (int)_w, Height = (int)_h });
                    // Re-subscribe after Recreate
                    _pool.FrameArrived += OnFrameArrived;
                    Console.WriteLine($"[WGC] Pool recreated: {_w}x{_h}, event re-subscribed");
                }
                catch (Exception e)
                {
                    Console.WriteLine("[WGC] Recreate(pool) failed: " + e);
                }
                return; // bỏ frame giao thời
            }

            Console.WriteLine($"[WGC] Processing frame: {sz.Width}x{sz.Height}");
            bool fastOK = false;
            
            // Get surface and ensure proper type projection
            var rawSurface = frame.Surface;
            IDirect3DSurface? surface = rawSurface as IDirect3DSurface;
            if (surface == null && rawSurface is WinRT.IWinRTObject winrtSurf)
            {
                try
                {
                    // Try to get properly typed surface from native object
                    surface = MarshalInterface<IDirect3DSurface>.FromAbi(winrtSurf.NativeObject.ThisPtr);
                    Console.WriteLine("[WGC] Surface cast via MarshalInterface succeeded");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WGC] Surface cast failed: {ex.Message}");
                }
            }
            
            if (surface == null)
            {
                Console.WriteLine("[WGC] Failed to get IDirect3DSurface");
                return;
            }
            
            try
            {
                Console.WriteLine("[WGC] Trying fast GPU path...");
                using var src = WgcInterop.TryGetTextureFast(surface);
                using (src)
                {
                    if (src != null)
                    {
                        Console.WriteLine("[WGC] Got texture, copying to staging...");
                        _ctx.CopyResource(_staging, src);
                        Console.WriteLine("[WGC] Mapping staging buffer...");
                        var box = _ctx.Map(_staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            uint srcStride = box.RowPitch;
                            EnsureScratch();
                            Console.WriteLine($"[WGC] Copying pixels: srcStride={srcStride}, dstStride={_stride}, h={_h}");
                            unsafe
                            {
                                byte* srcBase = (byte*)box.DataPointer;
                                for (int y = 0; y < _h; y++)
                                {
                                    byte* srcRow = srcBase + y * srcStride;
                                    fixed (byte* dst = &_scratch[y * _stride])
                                    {
                                        ulong n = (ulong)Math.Min((uint)_stride, srcStride);
                                        Buffer.MemoryCopy(srcRow, dst, _stride, n);
                                    }
                                }
                            }
                            Console.WriteLine("[WGC] Pixel copy done");
                        }
                        finally { _ctx.Unmap(_staging, 0); }
                        fastOK = true;
                    }
                    else
                    {
                        Console.WriteLine("[WGC] TryGetTextureFast returned null");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[WGC] Fast path failed: " + ex.Message); fastOK = false; }

            if (!fastOK)
            {
                Console.WriteLine("[WGC] Using CPU fallback path...");
                WgcInterop.CopySurfaceToScratch_CPU(surface, _scratch, _w, _h, _stride);
                Console.WriteLine("[WGC] CPU fallback done");
            }

            Console.WriteLine("[WGC] Invoking OnFrame callback...");
            OnFrame?.Invoke(_scratch, (int)_w, (int)_h, (int)_stride);
            Console.WriteLine("[WGC] OnFrame callback completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WGC] OnFrameArrived error: " + ex);
        }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { (_ctx as IDisposable)?.Dispose(); } catch { }
        try { (_d3d as IDisposable)?.Dispose(); } catch { }
        try { (_dxDevice as IDisposable)?.Dispose(); } catch { }
    }
}
