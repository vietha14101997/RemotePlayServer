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
    /// Dynamically change encoder FPS without reinitialization.
    /// Used for runtime FPS adjustments.
    /// </summary>
    /// <param name="fps">New target FPS.</param>
    /// <returns>True if FPS was changed successfully, false if not supported or failed.</returns>
    bool SetFps(int fps);

    /// <summary>
    /// Get current target bitrate.
    /// </summary>
    int CurrentBitrateKbps { get; }

    /// <summary>
    /// Get current target FPS.
    /// </summary>
    int CurrentFps { get; }

    /// <summary>
    /// True if this encoder supports BGRA input directly (no NV12 conversion needed).
    /// When true, use InitializeBgra() and EncodeBgraTexture() instead.
    /// </summary>
    bool SupportsBgraInput => false;

    /// <summary>
    /// True if encoder was initialized in BGRA mode.
    /// </summary>
    bool UsingBgraMode => false;

    /// <summary>
    /// Initialize encoder in BGRA mode (no color conversion needed).
    /// Only supported if SupportsBgraInput is true.
    /// </summary>
    bool InitializeBgra(int width, int height, int fps, int bitrate, ID3D11Device device)
    {
        return false; // Default: not supported
    }

    /// <summary>
    /// Encode a D3D11 BGRA texture directly.
    /// Only supported if initialized in BGRA mode.
    /// </summary>
    bool EncodeBgraTexture(ID3D11Texture2D bgraTexture, bool forceKeyframe = false)
    {
        return false; // Default: not supported
    }
}
