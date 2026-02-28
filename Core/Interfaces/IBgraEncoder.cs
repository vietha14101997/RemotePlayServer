#nullable enable
using Vortice.Direct3D11;

namespace RemotePlayServer.Core.Interfaces;

/// <summary>
/// Extends encoders that accept BGRA input directly, bypassing NV12 color conversion.
/// NVENC on NVIDIA GPUs supports this for zero-overhead encoding.
/// Default implementations return false (not supported) - only override in encoders that support it.
/// </summary>
public interface IBgraEncoder
{
    bool SupportsBgraInput => false;
    bool UsingBgraMode => false;

    bool InitializeBgra(int width, int height, int fps, int bitrate, ID3D11Device device)
    {
        return false;
    }

    bool EncodeBgraTexture(ID3D11Texture2D bgraTexture, bool forceKeyframe = false)
    {
        return false;
    }
}
