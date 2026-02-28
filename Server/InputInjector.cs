#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemotePlayServer.Server;

/// <summary>
/// Sends keyboard, mouse, and text input events via Win32 SendInput API.
/// Requires Administrator privileges for input injection to work across desktops.
/// </summary>
public static class InputInjector
{
    public static Action<string>? OnLog;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public int type; public INPUTUNION U; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public int dwFlags; public int time; public IntPtr dwExtraInfo; }

    const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const int MOUSEEVENTF_LEFTDOWN = 0x0002;
    const int MOUSEEVENTF_LEFTUP = 0x0004;
    const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const int MOUSEEVENTF_RIGHTUP = 0x0010;
    const int MOUSEEVENTF_WHEEL = 0x0800;
    const int MOUSEEVENTF_HWHEEL = 0x01000;
    const int KEYEVENTF_KEYUP = 0x0002;
    const int KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);

    public static void Text(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        var list = new List<INPUT>();
        foreach (var ch in s)
        {
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)ch,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }
        OnLog?.Invoke($"Text \"{s}\"");
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void Wheel(int delta, bool horizontal = false)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = delta,
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        OnLog?.Invoke($"Wheel {(horizontal ? "H" : "V")} delta={delta}");
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void MoveAbsolute(int px, int py)
    {
        OnLog?.Invoke($"SetCursorPos x={px} y={py}");
        SetCursorPos(px, py);
    }

    public static void Click(bool down, bool right = false)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = right
                        ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP)
                        : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        OnLog?.Invoke($"Click {(right ? "R" : "L")} {(down ? "DOWN" : "UP")}");
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void Key(ushort vk, bool down)
    {
        OnLog?.Invoke($"Key vk=0x{vk:X2} {(down ? "DOWN" : "UP")}");
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }
}
