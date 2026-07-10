#nullable enable
using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Network.Upnp;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol;

/// <summary>
/// Turns a locally-gathered LAN host candidate into an additional publicly-reachable
/// srflx candidate by asking the router (UPnP) to forward the same UDP port.
/// Unlike STUN hole punching this gives the remote peer a DETERMINISTIC door to knock
/// on, so direct P2P works even when the client sits behind symmetric/CGNAT (client
/// only needs outbound UDP). Media never touches a relay server.
/// </summary>
internal static class UpnpCandidateAugmenter
{
    // Standard ICE priority for srflx component 1: (typePref 100 << 24) | (localPref << 8) | 255.
    // localPref 65534 ranks the UPnP-mapped door just below real host candidates.
    private const long SrflxPriority = (100L << 24) | (65534L << 8) | 255;

    /// <summary>
    /// If localCandidate is a private-IPv4 UDP host candidate, map its port on the router
    /// and send a synthesized srflx candidate through the same signaling channel.
    /// Fire-and-forget: never blocks or throws into the ICE hot path.
    /// </summary>
    internal static void TryAugment(string localCandidate, string label, Func<string, Task> sendCandidateAsync)
    {
        if (!TryParsePrivateUdpHostCandidate(localCandidate, out var lanIp, out var port))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var mapping = await UpnpPortMappingService.MapUdpAsync(lanIp, port, label);
                if (mapping == null) return;

                var candidate = BuildSrflxCandidate(mapping.ExternalIp, mapping.ExternalPort, lanIp, port);
                Logger.Info($"[UPnP] Advertising public candidate for {label}: {mapping.ExternalIp}:{mapping.ExternalPort} -> {lanIp}:{port}");
                await sendCandidateAsync(candidate);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[UPnP] Candidate augment failed for {label}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Re-send the already-established public candidates for a PC label. Needed after an
    /// ICE restart: SIPSorcery does not re-gather on restart, so the srflx door mapped
    /// during the initial exchange must be re-trickled or the remote peer loses it.
    /// </summary>
    internal static void ReAdvertise(string label, Func<string, Task> sendCandidateAsync)
    {
        var mappings = UpnpPortMappingService.GetActiveMappings(label);
        if (mappings.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var mapping in mappings)
            {
                try
                {
                    var candidate = BuildSrflxCandidate(mapping.ExternalIp, mapping.ExternalPort, mapping.LanIp, mapping.InternalPort);
                    Logger.Info($"[UPnP] Re-advertising public candidate for {label} after ICE restart: {mapping.ExternalIp}:{mapping.ExternalPort}");
                    await sendCandidateAsync(candidate);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[UPnP] Re-advertise failed for {label}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Parse "[a=][candidate:]&lt;foundation&gt; &lt;component&gt; udp &lt;priority&gt; &lt;ip&gt; &lt;port&gt; typ host ..."
    /// (both SDP and SIPSorcery bare forms). True only for component-1 UDP host candidates
    /// with a private IPv4 address — the only ones a router mapping can expose.
    /// </summary>
    internal static bool TryParsePrivateUdpHostCandidate(string? candidate, out string lanIp, out int port)
    {
        lanIp = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var s = candidate.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8) return false;
        if (parts[1] != "1") return false; // component 1 (RTP) only
        if (!parts[2].Equals("udp", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parts[6].Equals("typ", StringComparison.OrdinalIgnoreCase) ||
            !parts[7].Equals("host", StringComparison.OrdinalIgnoreCase)) return false;

        if (!IPAddress.TryParse(parts[4], out var ip) || !MdnsHelper.IsPrivateV4(ip)) return false;
        if (!int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
            port <= 0 || port > 65535) return false;

        lanIp = parts[4];
        return true;
    }

    /// <summary>Build the srflx candidate line advertising the router's forwarded door.</summary>
    internal static string BuildSrflxCandidate(IPAddress externalIp, int externalPort, string lanIp, int lanPort)
        => $"candidate:upnp{lanPort} 1 udp {SrflxPriority} {externalIp} {externalPort} typ srflx raddr {lanIp} rport {lanPort} generation 0";
}
