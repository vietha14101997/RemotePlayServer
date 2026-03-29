#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Server;

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
    /// Authenticates, registers device, fetches ICE servers, and opens presence WebSocket.
    /// Returns false if relay is disabled or any step fails.
    /// </summary>
    public static async Task<bool> InitializeAsync(InternetConfig config)
    {
        if (!config.UseRelay || string.IsNullOrEmpty(config.RelayUrl)) return false;

        var client = new RelayClient();

        if (!await client.LoginAsync(config.RelayUrl!, config.RelayEmail!, config.RelayPassword!))
        {
            client.Dispose();
            return false;
        }

        await client.RegisterDeviceAsync(Environment.MachineName);
        await client.FetchIceServersAsync();
        await client.ConnectPresenceAsync();

        // Register guest access (UltraViewer-style ID + password)
        GuestIdManager.Generate();

        var guestRegistered = false;
        for (int i = 0; i < 3 && !guestRegistered; i++)
        {
            guestRegistered = await client.RegisterGuestDeviceAsync(
                GuestIdManager.CurrentId,
                GuestIdManager.CurrentPassword,
                Environment.MachineName);
            if (!guestRegistered)
            {
                GuestIdManager.Generate(); // retry with new ID
            }
        }

        if (guestRegistered)
        {
            Logger.Info($"[Relay] Guest Access: ID={GuestIdManager.DisplayId} Password={GuestIdManager.CurrentPassword}");
        }

        _instance = new RelayClientManager(client);
        Logger.Info("[RelayClientManager] Initialized successfully");
        return true;
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
