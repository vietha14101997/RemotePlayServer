#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

static class DpiUtil
{
    public static void RestartExplorerShell()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
                p.Kill();
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    public static void BroadcastForSettingsChange() => BroadcastSettingsChange();

    private static void BroadcastSettingsChange()
    {
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SETTINGCHANGE = 0x1A;

        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet",
            0x0002 /*SMTO_ABORTIFHUNG*/, 5000, out _);
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "WindowsMetrics",
            0x0002, 5000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
