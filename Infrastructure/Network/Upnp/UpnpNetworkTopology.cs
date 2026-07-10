#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemotePlayServer.Infrastructure.Network.Upnp;

/// <summary>
/// Local network topology helpers for UPnP: pick the interfaces that can actually
/// reach the gateway (multi-NIC machines gather host candidates on virtual adapters
/// like VMware/Hyper-V/VPN that the router cannot route back to), and validate that
/// SSDP-announced device URLs stay on the LAN (SSRF guard).
/// </summary>
internal static class UpnpNetworkTopology
{
    /// <summary>
    /// True when lanIp lives on the same IPv4 subnet as the gateway, judged by the
    /// local interface's own netmask. Fails open when the mask is unknown, and fails
    /// closed when lanIp does not belong to any live local interface.
    /// </summary>
    internal static bool IsOnSameSubnet(string lanIp, string gatewayHost)
    {
        if (!IPAddress.TryParse(lanIp, out var local)) return false;
        if (!IPAddress.TryParse(gatewayHost, out var gateway)) return true; // non-IP host: cannot verify, allow

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (!unicast.Address.Equals(local)) continue;
                    var mask = unicast.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any)) return true;
                    return SameNetwork(local, gateway, mask);
                }
            }
        }
        catch
        {
            return true; // interface enumeration failed: don't block the feature
        }
        return false; // stale candidate address not present on any interface
    }

    internal static bool SameNetwork(IPAddress a, IPAddress b, IPAddress mask)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork ||
            b.AddressFamily != AddressFamily.InterNetwork) return false;
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
        {
            if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
        }
        return true;
    }

    /// <summary>Live IPv4 unicast addresses to use as SSDP multicast egress (loopback excluded).</summary>
    internal static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        result.Add(ip);
                }
            }
        }
        catch { /* fall back to empty → caller uses IPAddress.Any */ }
        return result;
    }

    /// <summary>
    /// SSRF guard: a UPnP device description URL is only trusted when its host is an
    /// IP-literal on a private / link-local / loopback range. Any SSDP responder can
    /// claim an arbitrary LOCATION, and the host (running elevated) would GET it.
    /// </summary>
    internal static bool IsTrustedLanUrl(Uri url)
    {
        if (!IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true; // link-local
            if (b[0] == 127) return true;                // loopback (test setups)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT-side gateways
            return false;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || IPAddress.IsLoopback(ip);
    }
}
