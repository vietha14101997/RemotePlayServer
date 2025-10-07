#nullable enable
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

static class TextScaleUtil
{
    // Windows “Text size” (Make text bigger)
    // Dùng 2 key vì tuỳ build Windows sẽ đọc ở một trong hai:
    //  1) HKCU\Control Panel\Accessibility\TextScaleFactor           (DWORD, 100..225)
    //  2) HKCU\Software\Microsoft\Accessibility\TextScaleFactor      (DWORD, 100..225)
    const string KEY1 = @"Control Panel\Accessibility";
    const string KEY2 = @"Software\Microsoft\Accessibility";
    const string VAL = "TextScaleFactor"; // ĐƠN VỊ = PHẦN TRĂM (100..225), KHÔNG ×10

    public struct Snapshot { public int? K1; public int? K2; }

    public static Snapshot Read()
    {
        return new Snapshot
        {
            K1 = ReadPercent(KEY1, VAL),
            K2 = ReadPercent(KEY2, VAL)
        };
    }

    /// <summary>Đặt Text size theo phần trăm (100..225). Áp tức thời cho đa số thành phần.</summary>
    public static void SetPercent(int percent)
    {
        int v = Math.Clamp(percent, 100, 225);
        WriteDword(KEY1, VAL, v);
        WriteDword(KEY2, VAL, v);
        BroadcastSettingsChange();
    }

    public static void Restore(Snapshot snap)
    {
        WriteOrDelete(KEY1, VAL, snap.K1);
        WriteOrDelete(KEY2, VAL, snap.K2);
        BroadcastSettingsChange();
    }

    // ---------- Helpers ----------
    static int? ReadPercent(string key, string name)
    {
        using var rk = Registry.CurrentUser.OpenSubKey(key, false);
        object? o = rk?.GetValue(name);
        if (o is int iv)
        {
            // Nếu lỡ có giá trị kiểu cũ (×10) như 1250 thì quy về %.
            if (iv > 225 && iv <= 10000) return iv / 10;
            return iv;
        }
        return null;
    }

    static void WriteDword(string key, string name, int value)
    {
        using var rk = Registry.CurrentUser.OpenSubKey(key, true) ?? Registry.CurrentUser.CreateSubKey(key, true);
        rk.SetValue(name, value, RegistryValueKind.DWord);
    }

    static void WriteOrDelete(string key, string name, int? value)
    {
        using var rk = Registry.CurrentUser.OpenSubKey(key, true) ?? Registry.CurrentUser.CreateSubKey(key, true);
        if (value is int v) rk.SetValue(name, v, RegistryValueKind.DWord);
        else rk.DeleteValue(name, false);
    }

    public static void RestartExplorerShell()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) p.Kill();
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    static void BroadcastSettingsChange()
    {
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SETTINGCHANGE = 0x1A;
        // Hai chủ đề thường thấy khi Windows cập nhật metrics
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "WindowsMetrics", 0x0002, 5000, out _);
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 5000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
