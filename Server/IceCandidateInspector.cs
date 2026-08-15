#nullable enable
using System;
using System.Net;

namespace RemotePlayServer.Server;

/// <summary>
/// Decides whether a locally-gathered ICE candidate is worth sending to the remote peer.
/// SIPSorcery gathers candidates on every interface, including loopback (127.x / ::1),
/// unspecified (0.0.0.0 / ::) and IPv6 link-local (fe80::/10) — addresses a remote
/// device can never reach. Sending them wastes connectivity checks and can stall ICE.
/// (The ICE-restart path uses the stricter FilterAnswerSdpIceCandidates, which keeps
/// only private-IPv4 host candidates; this filter is deliberately laxer so srflx/relay
/// and public addresses still flow on the initial answer + trickle path.)
/// </summary>
static class IceCandidateInspector
{
    /// <summary>
    /// True if the candidate's connection address is reachable from another device
    /// (private/public IP, mDNS ".local" hostname, srflx/relay). False only for
    /// definitely-unroutable addresses: loopback, unspecified, IPv6 link-local.
    /// Non-candidate strings (e.g. "end-of-candidates") pass through as true.
    /// Handles both SDP form ("a=candidate:..." / "candidate:...") and SIPSorcery's
    /// RTCIceCandidate.candidate bare form ("&lt;foundation&gt; &lt;component&gt; udp ..." with
    /// no "candidate:" prefix), so detection keys on the " typ " token, not the prefix.
    /// Note: on a machine whose only interface is loopback, every candidate gets
    /// dropped — same-machine browser tests need a real (LAN) interface.
    /// </summary>
    internal static bool IsRoutable(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return true;

        var s = candidate.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!s.Contains(" typ ", StringComparison.OrdinalIgnoreCase)) return true;

        // "[candidate:]<foundation> <component> <transport> <priority> <address> <port> typ <type> ..."
        // The optional "candidate:" prefix is glued to the foundation (no space),
        // so the address sits at index 4 in both forms.
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8) return true;

        // mDNS hostnames and anything unparseable are left for the resolver to handle
        if (!IPAddress.TryParse(parts[4], out var ip)) return true;

        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        // IPv6 link-local is useless without a zone id; IPv4 APIPA (169.254/16) is
        // kept — it can still connect devices on the same L2 segment.
        if (ip.IsIPv6LinkLocal) return false;
        return true;
    }

    /// <summary>
    /// True if the candidate is a host candidate bound to a private IPv4 (RFC 1918),
    /// CGNAT (100.64/10), cellular-internal (6.x), loopback, or IPv6 link-local address.
    /// In TURN / Internet mode, these candidates are not reachable across the WAN and
    /// will trigger 403 denied-peer-ip errors on coturn when paired with relay candidates.
    /// srflx and relay candidates are never dropped by this check.
    /// </summary>
    internal static bool IsPrivateOrLoopbackHostCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var s = candidate.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!s.Contains(" typ host", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return false;

        if (!IPAddress.TryParse(parts[4], out var ip)) return false;

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal)
            return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 6) return true; // DoD/carrier internal range sometimes emitted by cellular
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT host IP
            if (b[0] == 169 && b[1] == 254) return true; // Link-local APIPA
        }

        return false;
    }
}
