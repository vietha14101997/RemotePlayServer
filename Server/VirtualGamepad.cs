#nullable enable
using System;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using RemotePlayServer.Core;

namespace RemotePlayServer.Server;

/// <summary>
/// Manages a virtual Xbox 360 controller via ViGEm Bus Driver.
/// Client sends gamepad state (axes + buttons) over DataChannel,
/// server feeds it into the virtual controller.
/// Requires ViGEmBus driver installed: https://github.com/nefarius/ViGEmBus/releases
/// </summary>
public class VirtualGamepad : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private bool _connected;
    private bool _disposed;

    public bool IsConnected => _connected;

    /// <summary>
    /// Try to create and plug in a virtual Xbox 360 controller.
    /// Returns false if ViGEmBus driver is not installed.
    /// </summary>
    public bool TryConnect()
    {
        if (_connected) return true;
        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.Connect();
            _connected = true;
            Logger.Info("[VirtualGamepad] Xbox 360 controller connected via ViGEm");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VirtualGamepad] Failed to connect (ViGEmBus installed?): {ex.Message}");
            _controller = null;
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    /// <summary>
    /// Update controller state from binary message.
    /// Layout (22 bytes):
    ///   [buttons: u16 LE] — Xbox360Button flags
    ///   [leftTrigger: u8] [rightTrigger: u8]
    ///   [thumbLX: i16 LE] [thumbLY: i16 LE]
    ///   [thumbRX: i16 LE] [thumbRY: i16 LE]
    ///
    /// Total: 2 + 2 + 8 = 12 bytes minimum payload (after tag+padding)
    /// </summary>
    public void UpdateState(byte[] data, int offset)
    {
        if (!_connected || _controller == null) return;
        if (data.Length < offset + 12) return;

        ushort buttons = BitConverter.ToUInt16(data, offset);
        byte leftTrigger = data[offset + 2];
        byte rightTrigger = data[offset + 3];
        short thumbLX = BitConverter.ToInt16(data, offset + 4);
        short thumbLY = BitConverter.ToInt16(data, offset + 6);
        short thumbRX = BitConverter.ToInt16(data, offset + 8);
        short thumbRY = BitConverter.ToInt16(data, offset + 10);

        _controller.SetButtonState(Xbox360Button.Up,             (buttons & 0x0001) != 0);
        _controller.SetButtonState(Xbox360Button.Down,           (buttons & 0x0002) != 0);
        _controller.SetButtonState(Xbox360Button.Left,           (buttons & 0x0004) != 0);
        _controller.SetButtonState(Xbox360Button.Right,          (buttons & 0x0008) != 0);
        _controller.SetButtonState(Xbox360Button.Start,          (buttons & 0x0010) != 0);
        _controller.SetButtonState(Xbox360Button.Back,           (buttons & 0x0020) != 0);
        _controller.SetButtonState(Xbox360Button.LeftThumb,      (buttons & 0x0040) != 0);
        _controller.SetButtonState(Xbox360Button.RightThumb,     (buttons & 0x0080) != 0);
        _controller.SetButtonState(Xbox360Button.LeftShoulder,   (buttons & 0x0100) != 0);
        _controller.SetButtonState(Xbox360Button.RightShoulder,  (buttons & 0x0200) != 0);
        _controller.SetButtonState(Xbox360Button.Guide,          (buttons & 0x0400) != 0);
        _controller.SetButtonState(Xbox360Button.A,              (buttons & 0x1000) != 0);
        _controller.SetButtonState(Xbox360Button.B,              (buttons & 0x2000) != 0);
        _controller.SetButtonState(Xbox360Button.X,              (buttons & 0x4000) != 0);
        _controller.SetButtonState(Xbox360Button.Y,              (buttons & 0x8000) != 0);

        _controller.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
        _controller.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, thumbLX);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, thumbLY);
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, thumbRX);
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, thumbRY);

        _controller.SubmitReport();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connected)
        {
            try { _controller?.Disconnect(); } catch { }
            Logger.Info("[VirtualGamepad] Controller disconnected");
        }
        _controller = null;
        _client?.Dispose();
        _client = null;
        _connected = false;
    }
}
