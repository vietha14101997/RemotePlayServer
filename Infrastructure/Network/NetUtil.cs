
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;

namespace RemotePlayServer.Infrastructure.Network;

public static class NetUtil
{
    // VPN/virtual adapter keywords to filter out
    private static readonly string[] VpnKeywords = new[]
    {
        "virtual", "vmware", "hyper-v", "loopback", "vpn", "zerotier",
        "hamachi", "tap-", "tun", "tunnel", "vethernet", "docker",
        "wsl", "vbox", "virtualbox", "parallels", "utun"
    };

    public static IEnumerable<string> GetLocalIPv4Addresses(bool includeVirtual = false)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (!includeVirtual)
            {
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VpnKeywords.Any(kw => name.Contains(kw)))
                    continue;
            }

            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address;
                if (IPAddress.IsLoopback(ip)) continue;
                var s = ip.ToString();
                if (s.StartsWith("169.254.")) continue;
                yield return s;
            }
        }
    }

    /// <summary>
    /// Get the best local IP for LAN streaming.
    /// Prioritizes: 192.168.x.x > 172.16-31.x.x > 10.x.x.x
    /// </summary>
    public static string GetPreferredLocalIP()
    {
        var ips = GetLocalIPv4Addresses().ToList();

        // Priority 1: 192.168.x.x (most common home/office LAN)
        var preferred = ips.FirstOrDefault(ip => ip.StartsWith("192.168."));
        if (preferred != null) return preferred;

        // Priority 2: 172.16-31.x.x (private class B)
        preferred = ips.FirstOrDefault(ip =>
        {
            if (!ip.StartsWith("172.")) return false;
            var parts = ip.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int second)) return false;
            return second >= 16 && second <= 31;
        });
        if (preferred != null) return preferred;

        // Priority 3: 10.x.x.x (often VPN but could be corporate LAN)
        preferred = ips.FirstOrDefault(ip => ip.StartsWith("10."));
        if (preferred != null) return preferred;

        // Fallback to any IP or localhost
        return ips.FirstOrDefault() ?? "127.0.0.1";
    }

    /// <summary>
    /// Check if client IP is on the same subnet as any local interface.
    /// Used to distinguish LAN clients from internet clients.
    /// </summary>
    public static bool IsClientOnLAN(IPAddress clientIp)
    {
        if (clientIp.AddressFamily != AddressFamily.InterNetwork)
            return false;

        // Loopback is always local
        if (IPAddress.IsLoopback(clientIp))
            return true;

        // If client IP is not private, it's definitely internet
        if (!IsPrivateIp(clientIp))
            return false;

        var clientBytes = clientIp.GetAddressBytes();

        // Compare against all local interfaces' subnets
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var maskBytes = ua.IPv4Mask.GetAddressBytes();
                var localBytes = ua.Address.GetAddressBytes();
                bool sameSubnet = true;
                for (int i = 0; i < 4; i++)
                {
                    if ((clientBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i]))
                    { sameSubnet = false; break; }
                }
                if (sameSubnet) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check if an IP address is in a private (RFC 1918) or CGN (RFC 6598) range.
    /// </summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127); // CGN range
    }
}
