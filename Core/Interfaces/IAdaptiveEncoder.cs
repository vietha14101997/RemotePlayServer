#nullable enable

namespace RemotePlayServer.Core.Interfaces;

/// <summary>
/// Extends encoders with dynamic bitrate/FPS adjustment for adaptive streaming.
/// </summary>
public interface IAdaptiveEncoder
{
    int CurrentBitrateKbps { get; }
    int CurrentFps { get; }
    bool SetBitrate(int bitrateKbps);
    bool SetFps(int fps);
}
