#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network.Upnp;

/// <summary>
/// App-wide UPnP port-mapping manager. Discovers the router once (cached, retried
/// after a cooldown), maps host WebRTC UDP ports so a remote client can reach them
/// directly from the internet (true P2P — no media relay), renews leases so long
/// sessions outlive the router's 2h lease, and removes all mappings on shutdown.
/// Detects CGNAT: if the router's WAN address is not public, mappings are useless
/// and none are advertised.
/// </summary>
internal static class UpnpPortMappingService
{
    private const int LeaseSeconds = 7200;
    private const int RenewIntervalMs = 1_800_000; // lease/4 — refresh well before router expiry
    private const int RetryCooldownSeconds = 300;
    private const string MappingDescription = "RemotePlay WebRTC";

    private static readonly object _lock = new();
    private static Task<UpnpGatewayInfo?>? _gatewayTask;
    private static DateTime _lastFailedAttempt = DateTime.MinValue;
    private static Timer? _renewTimer;

    // Key "lanIp:port" — Lazy so concurrent callers share one AddPortMapping SOAP call
    private static readonly ConcurrentDictionary<string, Lazy<Task<UpnpMappingResult?>>> _mappings = new();

    /// <summary>Kick off router discovery in the background (call once at server start).</summary>
    internal static void WarmUp() => _ = GetGatewayAsync();

    /// <summary>
    /// Ensure router UDP forwarding exists for lanIp:internalPort. Returns null when
    /// there is no usable gateway (no UPnP, CGNAT, wrong-NIC address, mapping rejected).
    /// </summary>
    internal static async Task<UpnpMappingResult?> MapUdpAsync(string lanIp, int internalPort, string label)
    {
        var key = $"{lanIp}:{internalPort}";
        var lazy = _mappings.GetOrAdd(key,
            _ => new Lazy<Task<UpnpMappingResult?>>(() => CreateMappingAsync(lanIp, internalPort, label)));

        UpnpMappingResult? result = null;
        try { result = await lazy.Value; }
        catch (Exception ex) { Logger.Warn($"[UPnP] Mapping {key} failed: {ex.Message}"); }

        if (result == null)
            _mappings.TryRemove(key, out _); // allow a later retry (gateway may appear)
        return result;
    }

    /// <summary>Successfully established mappings for one PC label (used to re-advertise after ICE restart).</summary>
    internal static List<UpnpMappingResult> GetActiveMappings(string label)
    {
        var list = new List<UpnpMappingResult>();
        foreach (var lazy in _mappings.Values)
        {
            if (lazy is { IsValueCreated: true, Value: { IsCompletedSuccessfully: true, Result: { } mapping } } &&
                mapping.Label == label)
            {
                list.Add(mapping);
            }
        }
        return list;
    }

    private static async Task<UpnpMappingResult?> CreateMappingAsync(string lanIp, int internalPort, string label)
    {
        var gateway = await GetGatewayAsync();
        if (gateway == null) return null;

        // Multi-NIC guard: only map addresses the router can actually route back to
        // (virtual adapters — VMware/Hyper-V/VPN — also produce private host candidates).
        if (!UpnpNetworkTopology.IsOnSameSubnet(lanIp, gateway.GatewayHost))
        {
            Logger.Debug($"[UPnP] {lanIp} is not on the gateway's subnet — skipping mapping");
            return null;
        }

        try
        {
            var externalPort = await AddMappingWithRetryAsync(gateway.Client, internalPort, lanIp);
            if (externalPort == 0) return null;

            var result = new UpnpMappingResult(gateway.ExternalIp, externalPort, internalPort, lanIp, label);
            Logger.Info($"[UPnP] Mapped UDP {gateway.ExternalIp}:{externalPort} -> {lanIp}:{internalPort} ({label})");
            EnsureRenewTimer();
            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[UPnP] AddPortMapping for {lanIp}:{internalPort} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Try external=internal first; on conflict try random high ports; on lease rejection retry permanent.</summary>
    private static async Task<int> AddMappingWithRetryAsync(UpnpSoapClient client, int internalPort, string lanIp)
    {
        var candidatePorts = new List<int> { internalPort };
        var rng = new Random();
        for (int i = 0; i < 3; i++)
            candidatePorts.Add(rng.Next(40000, 60000));

        foreach (var externalPort in candidatePorts)
        {
            try
            {
                await client.AddUdpMappingAsync(externalPort, internalPort, lanIp, MappingDescription, LeaseSeconds);
                return externalPort;
            }
            catch (UpnpSoapException ex) when (ex.ErrorCode == UpnpSoapException.OnlyPermanentLeasesSupported)
            {
                await client.AddUdpMappingAsync(externalPort, internalPort, lanIp, MappingDescription, 0);
                return externalPort;
            }
            catch (UpnpSoapException ex) when (ex.ErrorCode == UpnpSoapException.ConflictInMappingEntry)
            {
                Logger.Debug($"[UPnP] External port {externalPort} busy, trying another");
            }
        }
        Logger.Warn("[UPnP] No free external port found after retries");
        return 0;
    }

    private static void EnsureRenewTimer()
    {
        lock (_lock)
        {
            _renewTimer ??= new Timer(_ => _ = RenewAllAsync(), null, RenewIntervalMs, RenewIntervalMs);
        }
    }

    /// <summary>Re-issue AddPortMapping for live mappings (IGD treats a duplicate add as a lease refresh).</summary>
    private static async Task RenewAllAsync()
    {
        var gateway = await GetGatewayAsync();
        if (gateway == null) return;

        foreach (var (key, lazy) in _mappings)
        {
            if (lazy is not { IsValueCreated: true, Value: { IsCompletedSuccessfully: true, Result: { } m } })
                continue;
            try
            {
                await gateway.Client.AddUdpMappingAsync(m.ExternalPort, m.InternalPort, m.LanIp, MappingDescription, LeaseSeconds);
                Logger.Debug($"[UPnP] Renewed lease for UDP {m.ExternalPort}");
            }
            catch (Exception ex)
            {
                // Router rebooted or refused: drop the entry so the next session re-maps fresh
                Logger.Warn($"[UPnP] Lease renewal for UDP {m.ExternalPort} failed ({ex.Message}) — dropping mapping");
                _mappings.TryRemove(key, out _);
            }
        }
    }

    private static Task<UpnpGatewayInfo?> GetGatewayAsync()
    {
        lock (_lock)
        {
            // Retry a failed/absent discovery after a cooldown (router may come up later)
            if (_gatewayTask is { IsCompleted: true } completed &&
                (completed.IsFaulted || completed.IsCanceled ||
                 (completed.IsCompletedSuccessfully && completed.Result == null)) &&
                (DateTime.UtcNow - _lastFailedAttempt).TotalSeconds > RetryCooldownSeconds)
            {
                _gatewayTask = null;
            }
            _gatewayTask ??= DiscoverGatewayAsync();
            return _gatewayTask;
        }
    }

    private static async Task<UpnpGatewayInfo?> DiscoverGatewayAsync()
    {
        try
        {
            var endpoint = await UpnpDiscovery.DiscoverAsync();
            if (endpoint == null)
            {
                Logger.Warn("[UPnP] No UPnP gateway found on LAN (router UPnP disabled?) — no public candidates will be advertised");
                return MarkFailed();
            }

            var client = new UpnpSoapClient(endpoint);
            var externalIp = await client.GetExternalIpAsync();
            if (externalIp == null)
            {
                Logger.Warn("[UPnP] Gateway found but GetExternalIPAddress failed");
                return MarkFailed();
            }

            if (!IsPublicV4(externalIp))
            {
                Logger.Warn($"[UPnP] Router WAN IP {externalIp} is private/CGNAT — port mapping unreachable from internet; a TURN relay is required for cross-network sessions");
                return MarkFailed();
            }

            Logger.Info($"[UPnP] Gateway ready: {client.GatewayHost} ({endpoint.ServiceType}), WAN IP {externalIp}");
            return new UpnpGatewayInfo(client, externalIp, client.GatewayHost);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[UPnP] Gateway discovery failed: {ex.Message}");
            return MarkFailed();
        }

        static UpnpGatewayInfo? MarkFailed()
        {
            _lastFailedAttempt = DateTime.UtcNow;
            return null;
        }
    }

    /// <summary>True when the address is directly reachable from the internet (not private/CGNAT/loopback/link-local).</summary>
    internal static bool IsPublicV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return false;                              // 10/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16/12
        if (b[0] == 192 && b[1] == 168) return false;              // 192.168/16
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // 100.64/10 CGNAT
        if (b[0] == 127) return false;                             // loopback
        if (b[0] == 169 && b[1] == 254) return false;              // link-local
        if (b[0] == 0) return false;                               // unspecified
        return true;
    }

    /// <summary>Best-effort removal of all mappings (call on app shutdown).</summary>
    internal static async Task ShutdownAsync(int timeoutMs = 4000)
    {
        lock (_lock)
        {
            _renewTimer?.Dispose();
            _renewTimer = null;
        }

        Task<UpnpGatewayInfo?>? gatewayTask;
        lock (_lock) { gatewayTask = _gatewayTask; }
        if (gatewayTask is not { IsCompletedSuccessfully: true, Result: not null }) return;

        var client = gatewayTask.Result!.Client;
        var cleanup = Task.WhenAll(BuildDeleteTasks(client));
        await Task.WhenAny(cleanup, Task.Delay(timeoutMs));
        _mappings.Clear();
    }

    private static IEnumerable<Task> BuildDeleteTasks(UpnpSoapClient client)
    {
        foreach (var lazy in _mappings.Values)
        {
            if (lazy is not { IsValueCreated: true, Value: { IsCompletedSuccessfully: true, Result: { } mapping } })
                continue;
            yield return Task.Run(async () =>
            {
                try
                {
                    await client.DeleteUdpMappingAsync(mapping.ExternalPort);
                    Logger.Info($"[UPnP] Removed mapping UDP {mapping.ExternalPort}");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[UPnP] DeletePortMapping {mapping.ExternalPort} failed: {ex.Message}");
                }
            });
        }
    }

    private sealed record UpnpGatewayInfo(UpnpSoapClient Client, IPAddress ExternalIp, string GatewayHost);
}

/// <summary>An established router port mapping for one local WebRTC socket.</summary>
internal sealed record UpnpMappingResult(
    IPAddress ExternalIp, int ExternalPort, int InternalPort, string LanIp, string Label);
