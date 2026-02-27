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
}
