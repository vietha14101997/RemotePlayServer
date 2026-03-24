#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using RemotePlayServer.Core;

namespace RemotePlayServer.Server;

/// <summary>
/// Tracks foreground window changes via SetWinEventHook.
/// Runs on a dedicated STA thread with a message pump so Win32 events fire.
/// </summary>
public sealed class ForegroundWindowTracker : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint WM_QUIT = 0x0012;

    private Thread? _thread;
    private uint _threadId;
    private WinEventDelegate? _delegate;
    private readonly Action<int> _onMonitorChanged;
    private readonly Func<IReadOnlyList<(int x, int y, int w, int h)>> _getMonitorRects;
    private int _lastMonitorIndex = -1;
    private bool _disposed;

    public ForegroundWindowTracker(
        Action<int> onMonitorChanged,
        Func<IReadOnlyList<(int x, int y, int w, int h)>> getMonitorRects)
    {
        _onMonitorChanged = onMonitorChanged;
        _getMonitorRects = getMonitorRects;
    }

    public void Start()
    {
        if (_thread != null) return;

        _thread = new Thread(MessagePumpThread)
        {
            IsBackground = true,
            Name = "ForegroundWindowTracker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread?.Join(2000);
        _thread = null;
        _threadId = 0;
    }

    private void MessagePumpThread()
    {
        _threadId = GetCurrentThreadId();
        _delegate = OnWinEvent;

        var hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero)
        {
            Logger.Error("[ForegroundTracker] SetWinEventHook failed");
            return;
        }

        Logger.Info("[ForegroundTracker] Started tracking foreground window changes");

        // Message pump — required for WINEVENT_OUTOFCONTEXT callbacks
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWinEvent(hook);
        Logger.Info("[ForegroundTracker] Stopped");
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return;

            int monitorIndex = GetMonitorIndexFromHandle(hMonitor);
            if (monitorIndex >= 0 && monitorIndex != _lastMonitorIndex)
            {
                _lastMonitorIndex = monitorIndex;
                _onMonitorChanged(monitorIndex);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ForegroundTracker] Error: {ex.Message}");
        }
    }

    private int GetMonitorIndexFromHandle(IntPtr hMonitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info)) return -1;

        var rects = _getMonitorRects();
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.x == info.rcMonitor.left && r.y == info.rcMonitor.top &&
                r.w == info.rcMonitor.right - info.rcMonitor.left &&
                r.h == info.rcMonitor.bottom - info.rcMonitor.top)
            {
                return i;
            }
        }
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
