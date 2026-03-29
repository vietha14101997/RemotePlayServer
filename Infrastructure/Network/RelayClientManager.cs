#nullable enable
using System.Collections.Generic;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Singleton lifecycle wrapper for RelayClient.
/// Initialize once at startup when UseRelay=true in internet-settings.json.
/// </summary>
public class RelayClientManager
{
    private static volatile RelayClientManager? _instance;
    public static RelayClientManager? Instance => _instance;

    public RelayClient Client { get; }
    public List<IceServerConfig>? IceServers => Client.IceServers;

    private RelayClientManager(RelayClient client) { Client = client; }

    /// <summary>
    /// Sets the singleton instance with a pre-authenticated RelayClient.
    /// Called from ServerService.ConnectRelayAsync after login.
    /// </summary>
    public static void SetInstance(RelayClient client)
    {
        _instance = new RelayClientManager(client);
        Logger.Info("[RelayClientManager] Instance set with authenticated client");
    }

    /// <summary>
    /// Disposes the underlying RelayClient and clears the singleton.
    /// </summary>
    public static void Shutdown()
    {
        var inst = _instance;
        _instance = null;
        inst?.Client.Dispose();
        Logger.Info("[RelayClientManager] Shutdown");
    }
}
