#nullable enable
using System;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Common interface for hardware texture encoders (AMF, NVENC, QSV)
/// </summary>
public interface ITextureEncoder : IDisposable
{
    /// <summary>
    /// Event fired when encoded H.264 data is available
    /// Parameters: (byte[] nalData, bool isKeyFrame, long pts)
    /// </summary>
    event Action<byte[], bool, long>? OnEncodedData;
    
    bool IsInitialized { get; }
    int Width { get; }
    int Height { get; }
    
    /// <summary>
    /// Initialize the encoder
    /// </summary>
    bool Initialize(int width, int height, int fps, int bitrate, ID3D11Device device);
    
    /// <summary>
    /// Encode a D3D11 NV12 texture
    /// </summary>
    bool EncodeTexture(ID3D11Texture2D texture, bool forceKeyframe = false);

    /// <summary>
    /// Flush encoder to get remaining frames
    /// </summary>
    void Flush();

    /// <summary>
    /// Dynamically change encoder bitrate without reinitialization.
    /// Used for adaptive bitrate streaming.
    /// </summary>
    /// <param name="bitrateKbps">New target bitrate in kbps.</param>
    /// <returns>True if bitrate was changed successfully, false if not supported or failed.</returns>
    bool SetBitrate(int bitrateKbps);

    /// <summary>
    /// Get current target bitrate.
    /// </summary>
    int CurrentBitrateKbps { get; }
}
