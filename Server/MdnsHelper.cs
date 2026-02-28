#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace RemotePlayServer.Server;

/// <summary>
/// Handles mDNS candidate resolution for WebRTC ICE candidates.
/// Chrome/Edge may hide local IPs by using mDNS hostnames like "uuid.local".
/// </summary>
static class MdnsHelper
{
    internal static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    internal static async Task<string> MaybeResolveMdnsCandidateAsync(string candStr, int timeoutMs = 2000)
    {
        var parts = candStr.Split(' ');
        if (parts.Length < 6) return candStr;

        var addr = parts[4];
        if (!addr.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return candStr;

        Console.WriteLine($"[Cluster Signal] Attempting to resolve mDNS: '{addr}'");

        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var resolveTask = Dns.GetHostAddressesAsync(addr);
                var completed = await Task.WhenAny(resolveTask, Task.Delay(timeoutMs));
                if (completed != resolveTask)
                {
                    Console.WriteLine($"[Cluster Signal] mDNS resolve timed out for '{addr}' (attempt {attempt}/{maxAttempts}, timeout={timeoutMs}ms)");
                    return candStr;
                }

                var addrs = await resolveTask;
                if (addrs == null || addrs.Length == 0)
                {
                    Console.WriteLine($"[Cluster Signal] mDNS resolve returned no addresses for '{addr}' (attempt {attempt}/{maxAttempts})");
                    return candStr;
                }

                var chosen = addrs.FirstOrDefault(IsPrivateV4)
                          ?? addrs.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

                if (chosen == null || !IsPrivateV4(chosen))
                {
                    Console.WriteLine($"[Cluster Signal] mDNS resolved '{addr}' -> {chosen} (Public/Invalid IP). Ignoring to prevent loopback failure.");
                    return candStr;
                }

                parts[4] = chosen.ToString();
                Console.WriteLine($"[Cluster Signal] mDNS resolved '{addr}' -> {parts[4]} after {attempt} attempt(s)");
                return string.Join(' ', parts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cluster Signal] mDNS resolve attempt {attempt}/{maxAttempts} failed for '{addr}': {ex.Message}");
                if (attempt < maxAttempts)
                {
                    await Task.Delay(100);
                }
                else
                {
                    Console.WriteLine($"[Cluster Signal] All mDNS resolve attempts failed, will use fallback for '{addr}'");
                }
            }
        }

        return candStr;
    }

    internal static string MaybeReplaceMdnsWithRemoteIp(string candStr, IPAddress? remoteIp)
    {
        if (remoteIp == null || !IsPrivateV4(remoteIp)) return candStr;

        var parts = candStr.Split(' ');
        if (parts.Length < 6) return candStr;

        var addr = parts[4];
        if (!addr.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return candStr;

        parts[4] = remoteIp.ToString();
        var newCandStr = string.Join(' ', parts);

        Console.WriteLine($"[Cluster Signal] mDNS fallback: '{addr}' -> {parts[4]} (using remote IP)");
        Console.WriteLine($"[Cluster Signal] Rewritten candidate: {newCandStr}");

        return newCandStr;
    }
}
