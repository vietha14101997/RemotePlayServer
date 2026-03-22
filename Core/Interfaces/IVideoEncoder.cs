#nullable enable
using System;
using Vortice.Direct3D11;
using RemotePlayServer.Core;

namespace RemotePlayServer.Core.Interfaces;

/// <summary>
/// Core video encoding contract. Encodes D3D11 textures to H.264/H.265 NAL units.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Fired when encoded NAL data is available.
    /// WARNING: The ArraySegment's backing array is reused between calls.
    /// Consumers must NOT hold a reference to .Array after the handler returns.
    /// Copy via .ToArray() if async retention is needed.
    /// Parameters: (ArraySegment&lt;byte&gt; nalData, bool isKeyFrame, long pts)
    /// </summary>
    event Action<ArraySegment<byte>, bool, long>? OnEncodedData;

    bool IsInitialized { get; }
    int Width { get; }
    int Height { get; }
    VideoCodec CurrentCodec { get; }

    bool Initialize(int width, int height, int fps, int bitrate, ID3D11Device device);
    bool EncodeTexture(ID3D11Texture2D texture, bool forceKeyframe = false);
    void Flush();
}
