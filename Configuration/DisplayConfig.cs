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
}
