using System;
using System.Runtime.InteropServices;
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

    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr hstringClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

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
        [PreserveSig]
        int CreateForWindow(IntPtr hwnd, [In] ref Guid iid, out IntPtr item);
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

    [ComImport, Guid("A9B3D012-3DF7-4E55-8F1E-35F6DDA3A6CF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }

    public static ID3D11Texture2D TryGetTextureFast(IDirect3DSurface surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        IntPtr abi = IntPtr.Zero, accessPtr = IntPtr.Zero, texPtr = IntPtr.Zero;
        try
        {
            abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            Guid iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            int hr = Marshal.QueryInterface(abi, in iidAccess, out accessPtr);
            if (hr != 0 || accessPtr == IntPtr.Zero) return null;
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
            Guid iidTex2D = typeof(ID3D11Texture2D).GUID;
            texPtr = access.GetInterface(ref iidTex2D);
            if (texPtr == IntPtr.Zero) return null;
            return new ID3D11Texture2D(texPtr);
        }
        finally
        {
            if (texPtr != IntPtr.Zero) Marshal.Release(texPtr);
            if (accessPtr != IntPtr.Zero) Marshal.Release(accessPtr);
            if (abi != IntPtr.Zero) MarshalInterface<IDirect3DSurface>.DisposeAbi(abi);
        }
    }

    public unsafe static void CopySurfaceToScratch_CPU(IDirect3DSurface surface, byte[] scratch, uint w, uint h, uint strideOut)
    {
        using var sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        using var conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var buffer = conv.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        // ⚠️ KHÔNG ép kiểu trực tiếp ((IMemoryBufferByteAccess)reference) — sẽ InvalidCastException
        IntPtr refAbi = IntPtr.Zero, byteAccessPtr = IntPtr.Zero;
        try
        {
            // Lấy ABI pointer từ IMemoryBufferReference (Windows.Foundation)
            refAbi = WinRT.MarshalInterface<Windows.Foundation.IMemoryBufferReference>.FromManaged(reference);

            // QI sang IMemoryBufferByteAccess
            Guid iidMBA = typeof(IMemoryBufferByteAccess).GUID; // {5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D}
            int hr = Marshal.QueryInterface(refAbi, in iidMBA, out byteAccessPtr);
            if (hr != 0 || byteAccessPtr == IntPtr.Zero)
                Marshal.ThrowExceptionForHR(hr);

            var byteAccess = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(byteAccessPtr);
            byteAccess.GetBuffer(out byte* srcBase, out uint cap);

            var plane = buffer.GetPlaneDescription(0);
            uint rowBytes = Math.Min(strideOut, (uint)plane.Stride);

            for (int y = 0; y < (int)h; y++)
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
}

public sealed class WgcCapture : IDisposable
{
    readonly ID3D11Device _d3d;
    readonly ID3D11DeviceContext _ctx;
    readonly IDirect3DDevice _dxDevice;
    GraphicsCaptureItem _item;
    Direct3D11CaptureFramePool _pool;
    GraphicsCaptureSession _session;
    ID3D11Texture2D _staging;
    uint _w, _h, _stride;
    byte[] _scratch = Array.Empty<byte>();

#nullable enable
    public event Action<byte[], int, int, int>? OnFrame;
#nullable disable

    // ✅ expose size để Program.cs có thể đọc nếu muốn
    public (uint w, uint h) Size => (_w, _h);

    public WgcCapture(IntPtr hwnd)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            null, out _d3d, out _ctx);
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _dxDevice = DxInterop.CreateDirect3DDeviceFromDxgi(dxgi.NativePointer);

        // Tạo item an toàn (có kiểm tra support + capturable)
        _item = WgcInterop.CreateItemForHwndWithFallback(hwnd);
        if (_item is null) throw new InvalidOperationException("Failed to create GraphicsCaptureItem for the window.");

        var size = _item.Size;
        _w = (uint)Math.Max(16, size.Width);
        _h = (uint)Math.Max(16, size.Height);
        _stride = _w * 4;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_dxDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;

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

    public void Start() => _session.StartCapture();

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var sz = frame.ContentSize;
            if (sz.Width != _w || sz.Height != _h)
            {
                _w = (uint)Math.Max(16, sz.Width);
                _h = (uint)Math.Max(16, sz.Height);
                _stride = _w * 4;
                _staging.Dispose();
                _staging = CreateStaging(_w, _h);
                EnsureScratch();
            }

            bool fastOK = false;
            try
            {
                using var src = WgcInterop.TryGetTextureFast(frame.Surface);
                if (src != null)
                {
                    _ctx.CopyResource(_staging, src);
                    var box = _ctx.Map(_staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        uint srcStride = box.RowPitch;
                        EnsureScratch();
                        unsafe
                        {
                            byte* srcBase = (byte*)box.DataPointer;
                            for (int y = 0; y < _h; y++)
                            {
                                byte* srcRow = srcBase + y * srcStride;
                                fixed (byte* dst = &_scratch[y * _stride])
                                {
                                    Buffer.MemoryCopy(srcRow, dst, _stride, _stride);
                                }
                            }
                        }
                    }
                    finally { _ctx.Unmap(_staging, 0); }
                    fastOK = true;
                }
            }
            catch { fastOK = false; }

            if (!fastOK)
            {
                WgcInterop.CopySurfaceToScratch_CPU(frame.Surface, _scratch, _w, _h, _stride);
            }

            OnFrame?.Invoke(_scratch, (int)_w, (int)_h, (int)_stride);
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
