#nullable enable

namespace RemotePlayServer.Configuration;

/// <summary>
/// Runtime display/stream configuration.
/// Updated dynamically when clients connect and negotiate settings.
/// </summary>
public static class DisplayConfig
{
    public static int MonitorCount = 3;
    public static int RefreshRate = 60;
    public static int StreamFps = 30;

    /// <summary>
    /// Monitor type: "standard", "ultrawide", or "super_ultrawide".
    /// Set during Phase 2 when client sends display_config.
    /// </summary>
    public static string MonitorType = "standard";

    /// <summary>
    /// Preferred video codec for streaming.
    /// "H264" = lighter decode on mobile (less thermal), higher bandwidth
    /// "H265" = better compression (lower bandwidth), heavier decode
    /// "Auto" = negotiate best mutual codec (default: H265 > H264 > VP9 > VP8)
    /// Can be changed at runtime; takes effect on next client connection.
    /// </summary>
    public static string PreferredCodec = "Auto";
}
