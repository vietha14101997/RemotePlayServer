
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;

public static class NetUtil
{
    public static IEnumerable<string> GetLocalIPv4Addresses(bool includeVirtual = false)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (!includeVirtual)
            {
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (name.Contains("virtual") || name.Contains("vmware") || name.Contains("hyper-v") || name.Contains("loopback"))
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
}
