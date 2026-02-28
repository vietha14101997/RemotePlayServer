
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
}
