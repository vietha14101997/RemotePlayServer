using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

internal static class DxInterop
{
    // Wrap DXGI -> WinRT IDirect3DDevice
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDirect3DDeviceFromDxgi(IntPtr dxgiDevice)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var devicePtr);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        // QUAN TRỌNG: bọc đúng kiểu WinRT, KHÔNG dùng Marshal.GetObjectForIUnknown
        return MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);
    }

    // WinRT activation — tạo HSTRING thủ công để tránh lỗi marshal string/HSTRING
    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr hstringClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    // IID của IActivationFactory (chuẩn COM/WinRT)
    public static readonly Guid IID_IActivationFactory = new Guid("00000035-0000-0000-C000-000000000046");
}

internal static class WgcInterop
{
    // IGraphicsCaptureItemInterop cho CreateForWindow(HWND,…)
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr hwnd, [In] ref Guid iid, out IntPtr item);
    }

    public static GraphicsCaptureItem CreateItemForHwnd(IntPtr hwnd)
    {
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
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
            if (hr != 0 || actPtr == IntPtr.Zero)
                throw new COMException($"RoGetActivationFactory failed: 0x{hr:X8}", hr);

            Guid iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = Marshal.QueryInterface(actPtr, ref iidInterop, out interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero)
                throw new COMException($"QueryInterface(IGraphicsCaptureItemInterop) failed: 0x{hr:X8}", hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);

            // Thử với GUID của GraphicsCaptureItem (WinRT runtime class)
            Guid iidItem = typeof(GraphicsCaptureItem).GUID;
            hr = interop.CreateForWindow(hwnd, ref iidItem, out itemPtr);
            if (hr != 0 || itemPtr == IntPtr.Zero)
            {
                // fallback: thử IInspectable
                Guid iidInspectable = new Guid("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");
                hr = interop.CreateForWindow(hwnd, ref iidInspectable, out itemPtr);
                if (hr != 0 || itemPtr == IntPtr.Zero)
                    throw new COMException($"CreateForWindow failed: 0x{hr:X8}", hr);
            }

            // ---- CHỖ QUAN TRỌNG: bọc WinRT đúng cách ----
            // FromAbi tạo instance GraphicsCaptureItem từ ABI pointer của WinRT
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (interopPtr != IntPtr.Zero) Marshal.Release(interopPtr);
            if (actPtr != IntPtr.Zero) Marshal.Release(actPtr);
            if (hstr != IntPtr.Zero) DxInterop.WindowsDeleteString(hstr);
        }
    }

    // ===== helper: kiểm tra cửa sổ có capturable không =====
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, ref RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_VISIBLE = 0x10000000;
    const int DWMWA_CLOAKED = 14;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public static bool IsCapturableWindow(IntPtr hwnd)
    {
        try
        {
            // Validate window handle
            if (!IsWindow(hwnd))
            {
                Console.WriteLine($"[WGC] Window {hwnd} is not a valid window handle");
                return false;
            }

            // Check window styles
            int style = GetWindowLong(hwnd, GWL_STYLE);
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);

            if ((style & WS_VISIBLE) != WS_VISIBLE)
            {
                Console.WriteLine($"[WGC] Window {hwnd} is not visible");
                return false;
            }

            if ((ex & WS_EX_TOOLWINDOW) == WS_EX_TOOLWINDOW)
            {
                Console.WriteLine($"[WGC] Window {hwnd} is a tool window");
                return false;
            }

            // Check if window is cloaked
            int cloaked = 0;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                Console.WriteLine($"[WGC] Window {hwnd} is cloaked");
                return false;
            }

            // Check if window has valid bounds
            var bounds = new RECT();
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ref bounds, Marshal.SizeOf<RECT>()) == 0)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Console.WriteLine($"[WGC] Window {hwnd} has invalid bounds: {bounds.Width}x{bounds.Height}");
                    return false;
                }
            }

            // Get process info
            GetWindowThreadProcessId(hwnd, out int pid);
            Console.WriteLine($"[WGC] Window {hwnd} belongs to process {pid}");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WGC] Error checking window {hwnd}: {ex.Message}");
            return false;
        }
    }

    // Thêm method để thử capture với fallback
    public static GraphicsCaptureItem CreateItemForHwndWithFallback(IntPtr hwnd)
    {
        try
        {
            return CreateItemForHwnd(hwnd);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004002)) // E_NOINTERFACE
        {
            Console.WriteLine($"[WGC] Direct capture failed for window {hwnd}, trying alternative approach...");

            // Thử đợi một chút và thử lại (có thể window đang trong quá trình thay đổi)
            System.Threading.Thread.Sleep(100);

            try
            {
                return CreateItemForHwnd(hwnd);
            }
            catch (COMException ex2)
            {
                Console.WriteLine($"[WGC] Retry also failed: 0x{ex2.HResult:X8}");
                throw new InvalidOperationException($"Cannot capture window {hwnd}. This window may be protected or not compatible with Windows Graphics Capture.", ex2);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // Lấy ID3D11Texture2D từ IDirect3DSurface (WGC)
    [ComImport, Guid("A9B3D012-3DF7-4E55-8F1E-35F6DDA3A6CF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    public static ID3D11Texture2D GetD3D11Texture2DFromSurface(IDirect3DSurface surface)
    {
        var iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        var iidTex2D = typeof(ID3D11Texture2D).GUID;

        IntPtr unk = Marshal.GetIUnknownForObject(surface);
        Marshal.QueryInterface(unk, in iidAccess, out var accessPtr);
        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
        IntPtr texPtr = access.GetInterface(ref iidTex2D);

        Marshal.Release(accessPtr);
        Marshal.Release(unk);

        return new ID3D11Texture2D(texPtr);
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
    public (uint w, uint h) Size => (_w, _h);

    public WgcCapture(IntPtr hwnd)
    {
        // D3D11 device + context
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            null,
            out _d3d,
            out _ctx);

        // Wrap thành WinRT IDirect3DDevice mà KHÔNG cần WinRT.Runtime
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _dxDevice = DxInterop.CreateDirect3DDeviceFromDxgi(dxgi.NativePointer);

        // GraphicsCaptureItem cho HWND với fallback
        _item = WgcInterop.CreateItemForHwndWithFallback(hwnd);
        var size = _item.Size;
        _w = (uint)Math.Max(16, size.Width);
        _h = (uint)Math.Max(16, size.Height);
        _stride = _w * 4;

        // Frame pool + session
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _dxDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);

        // Staging texture
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
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        using var src = WgcInterop.GetD3D11Texture2DFromSurface(frame.Surface);
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

            OnFrame?.Invoke(_scratch, (int)_stride, (int)_w, (int)_h);
        }
        finally
        {
            _ctx.Unmap(_staging, 0);
        }

        var sz = frame.ContentSize;
        if (sz.Width != _w || sz.Height != _h)
        {
            _w = (uint)Math.Max(16, sz.Width);
            _h = (uint)Math.Max(16, sz.Height);
            _stride = _w * 4;
            _staging.Dispose();
            _staging = CreateStaging(_w, _h);
            _pool.Recreate(_dxDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                new Windows.Graphics.SizeInt32 { Width = (int)_w, Height = (int)_h });
            EnsureScratch();
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
