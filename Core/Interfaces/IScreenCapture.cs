#nullable enable
using System;
using Vortice.Direct3D11;

namespace RemotePlayServer.Core.Interfaces;

/// <summary>
/// Abstraction for screen capture implementations (DXGI, WGC).
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Fired when a new frame texture is captured.
    /// Parameters: (ID3D11Texture2D texture, int width, int height, int monitorIndex)
    /// </summary>
    event Action<ID3D11Texture2D, int, int, int>? OnTextureFrame;

    void Start(int fps);
    void Stop();
    bool IsRunning { get; }
}
