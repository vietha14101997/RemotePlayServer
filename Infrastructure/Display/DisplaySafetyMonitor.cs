#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Display;

/// <summary>
/// Independent safety monitor for VDD-only mode. Two safety layers:
/// 1. Keyboard escape hatch: Ctrl+Alt+F12 restores physical monitors instantly
/// 2. Max duration timer: auto-restore after 4 hours as ultimate safety net
///
/// Runs on dedicated background thread (survives ThreadPool starvation).
/// Each layer works independently — any single layer is sufficient for recovery.
/// </summary>
public sealed class DisplaySafetyMonitor : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const int HOTKEY_ID = 0x7F12; // unique ID
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_F12 = 0x7B;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;

    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan DurationCheckInterval = TimeSpan.FromMinutes(1);

    private Thread? _hotkeyThread;
    private uint _hotkeyThreadId;
    private Timer? _durationTimer;
    private volatile bool _running;
    private int _restored; // 0 = not restored, 1 = restored (atomic via Interlocked)
    private Action? _onEmergencyRestore;

    /// <summary>
    /// Start safety monitor. Call after entering VDD-only mode.
    /// </summary>
    /// <param name="onEmergencyRestore">Optional callback when emergency restore triggers (e.g., to reset _displayModified).</param>
    public void Start(Action? onEmergencyRestore = null)
    {
        if (_running) return;
        _running = true;
        Interlocked.Exchange(ref _restored, 0);
        _onEmergencyRestore = onEmergencyRestore;

        // Layer 1: Keyboard escape hatch (dedicated thread with Win32 message pump)
        _hotkeyThread = new Thread(HotkeyThreadProc)
        {
            Name = "DisplaySafetyMonitor",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _hotkeyThread.Start();

        // Layer 2: Max duration timer (independent of hotkey thread)
        _durationTimer = new Timer(_ => CheckMaxDuration(), null, DurationCheckInterval, DurationCheckInterval);

        Logger.Info("[Safety] DisplaySafetyMonitor started — Ctrl+Alt+F12 to emergency restore, 4h max duration");
    }

    /// <summary>
    /// Stop safety monitor. Call on normal disconnect/restore.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        // Stop duration timer
        _durationTimer?.Dispose();
        _durationTimer = null;

        // Stop hotkey thread by posting WM_QUIT
        if (_hotkeyThreadId != 0)
        {
            PostThreadMessage(_hotkeyThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // Wait for thread to exit (with timeout)
        _hotkeyThread?.Join(3000);
        _hotkeyThread = null;

        Logger.Info("[Safety] DisplaySafetyMonitor stopped");
    }

    private void HotkeyThreadProc()
    {
        _hotkeyThreadId = GetCurrentThreadId();

        // Register Ctrl+Alt+F12
        bool registered = RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F12);
        if (!registered)
        {
            Logger.Warn("[Safety] RegisterHotKey(Ctrl+Alt+F12) failed — hotkey escape hatch unavailable");
            // Thread still runs for WM_QUIT handling
        }
        else
        {
            Logger.Info("[Safety] Registered Ctrl+Alt+F12 emergency hotkey");
        }

        // Win32 message pump
        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_HOTKEY && msg.wParam == (IntPtr)HOTKEY_ID)
            {
                Logger.Info("[Safety] *** EMERGENCY RESTORE triggered by Ctrl+Alt+F12 ***");
                EmergencyRestore();
            }
        }

        // Cleanup
        if (registered)
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
    }

    private void CheckMaxDuration()
    {
        if (Volatile.Read(ref _restored) != 0 || !_running) return;

        var (active, startTime) = DisplayGuard.GetVddOnlyState();
        if (!active || startTime == null) return;

        var elapsed = DateTime.UtcNow - startTime.Value;
        if (elapsed > MaxDuration)
        {
            Logger.Info($"[Safety] *** MAX DURATION RESTORE — VDD-only active for {elapsed.TotalHours:F1}h (limit: {MaxDuration.TotalHours}h) ***");
            EmergencyRestore();
        }
    }

    private void EmergencyRestore()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;

        try
        {
            DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(10));
            Logger.Info("[Safety] Emergency restore completed");
            _onEmergencyRestore?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Safety] Emergency restore failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
