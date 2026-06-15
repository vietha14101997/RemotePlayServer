
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RemotePlayServer.Infrastructure;

public static class Win32
{
    public record WindowInfo(IntPtr hwnd, string title);

    public static List<WindowInfo> ListTopLevelWindows()
    {
        List<WindowInfo> list = new();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (!string.IsNullOrWhiteSpace(title)) list.Add(new WindowInfo(hWnd, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public record RunningAppInfo(IntPtr Hwnd, string Title, string ProcessName);

    public static List<RunningAppInfo> ListRunningApplications()
    {
        List<RunningAppInfo> list = new();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            // Get process name
            string processName = "";
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(processName))
            {
                list.Add(new RunningAppInfo(hWnd, title, processName));
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }
}
