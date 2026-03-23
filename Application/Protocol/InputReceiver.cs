#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using RemotePlayServer.Core;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol;

/// <summary>
/// Receives binary input messages from the VR client DataChannel and dispatches
/// them to Win32 InputInjector (mouse/keyboard) or VirtualGamepad (controller).
/// All methods are safe to call from any thread.
/// </summary>
public static class InputReceiver
{
    private const byte TAG_MOUSE_MOVE   = 0x01; // 5 bytes: [tag][dx:i16][dy:i16]
    private const byte TAG_MOUSE_BUTTON = 0x02; // 3 bytes: [tag][button:1][down:1]
    private const byte TAG_MOUSE_WHEEL  = 0x03; // 5 bytes: [tag][deltaY:i16][deltaX:i16]
    private const byte TAG_KEY          = 0x04; // 4 bytes: [tag][vk:u16][down:1]
    private const byte TAG_TEXT         = 0x05; // 3+N bytes: [tag][len:u16][UTF8]
    private const byte TAG_WARP_CURSOR  = 0x06; // 10 bytes: [tag][monIdx:1][u:f32][v:f32]
    private const byte TAG_GAMEPAD      = 0x07; // 13 bytes: [tag][buttons:u16][LT:u8][RT:u8][LX:i16][LY:i16][RX:i16][RY:i16]

    /// <summary>
    /// Called after any input is injected. Used to force frame capture
    /// so the visual result of the input is sent to the client promptly.
    /// </summary>
    public static Action? OnInputInjected;

    private static VirtualGamepad? _gamepad;
    private static readonly object _gamepadLock = new();

    // Throttle OnInputInjected: max once per 16ms (~60Hz) to avoid spamming
    private static long _lastInputNotifyTicks;

    public static void HandleInputMessage(byte[] data, IReadOnlyList<(int x, int y, int w, int h)> monitorRects)
    {
        if (data == null || data.Length < 3) return;

        byte tag = data[0];
        switch (tag)
        {
            case TAG_MOUSE_MOVE when data.Length >= 5:
            {
                short dx = BitConverter.ToInt16(data, 1);
                short dy = BitConverter.ToInt16(data, 3);
                InputInjector.MoveRelative(dx, dy);
                break;
            }
            case TAG_MOUSE_BUTTON when data.Length >= 3:
            {
                byte button = data[1];
                bool down = data[2] != 0;
                InputInjector.ClickButton(button, down);
                break;
            }
            case TAG_MOUSE_WHEEL when data.Length >= 5:
            {
                short deltaY = BitConverter.ToInt16(data, 1);
                short deltaX = BitConverter.ToInt16(data, 3);
                if (deltaY != 0) InputInjector.Wheel(deltaY);
                if (deltaX != 0) InputInjector.Wheel(deltaX, horizontal: true);
                break;
            }
            case TAG_KEY when data.Length >= 4:
            {
                ushort vk = BitConverter.ToUInt16(data, 1);
                bool down = data[3] != 0;
                InputInjector.Key(vk, down);
                break;
            }
            case TAG_TEXT when data.Length >= 3:
            {
                ushort len = BitConverter.ToUInt16(data, 1);
                if (data.Length >= 3 + len && len > 0)
                {
                    string text = Encoding.UTF8.GetString(data, 3, len);
                    InputInjector.Text(text);
                }
                break;
            }
            case TAG_WARP_CURSOR when data.Length >= 10:
            {
                int monIdx = data[1];
                float u = BitConverter.ToSingle(data, 2);
                float v = BitConverter.ToSingle(data, 6);
                if (monIdx >= 0 && monIdx < monitorRects.Count)
                {
                    var rect = monitorRects[monIdx];
                    int screenX = rect.x + (int)(u * rect.w);
                    int screenY = rect.y + (int)(v * rect.h);
                    InputInjector.MoveAbsolute(screenX, screenY);
                }
                break;
            }
            case TAG_GAMEPAD when data.Length >= 13:
            {
                EnsureGamepad();
                _gamepad?.UpdateState(data, 1); // skip tag byte
                break;
            }
        }

        // Notify capture layer to force frames (throttled to ~60Hz)
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long elapsed = now - _lastInputNotifyTicks;
        if (elapsed > System.Diagnostics.Stopwatch.Frequency / 60) // ~16ms
        {
            _lastInputNotifyTicks = now;
            OnInputInjected?.Invoke();
        }
    }

    private static void EnsureGamepad()
    {
        if (_gamepad is { IsConnected: true }) return;
        lock (_gamepadLock)
        {
            if (_gamepad is { IsConnected: true }) return;
            _gamepad?.Dispose();
            _gamepad = new VirtualGamepad();
            _gamepad.TryConnect();
        }
    }

    /// <summary>
    /// Cleanup virtual gamepad on server shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (_gamepadLock)
        {
            _gamepad?.Dispose();
            _gamepad = null;
        }
    }
}
