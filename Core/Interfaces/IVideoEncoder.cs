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
    /// Parameters: (byte[] nalData, bool isKeyFrame, long pts)
    /// </summary>
    event Action<byte[], bool, long>? OnEncodedData;

    bool IsInitialized { get; }
    int Width { get; }
    int Height { get; }
    VideoCodec CurrentCodec { get; }

    bool Initialize(int width, int height, int fps, int bitrate, ID3D11Device device);
    bool EncodeTexture(ID3D11Texture2D texture, bool forceKeyframe = false);
    void Flush();
}
