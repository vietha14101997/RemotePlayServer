#nullable enable
using System;
using System.Net;
using System.Net.Sockets;

namespace RemotePlayServer.Server;

/// <summary>
/// Contract-v1 classification of one ICE candidate wire string (see
/// plans/260715-2315-wan-p2p-turn-quality-hardening/telemetry-snapshot-contract-v1.md).
/// Every field is a bounded enum string — never a raw address/port — so it is safe to
/// ship in a telemetry payload.
/// </summary>
public readonly record struct IceCandidateClassification(
    string CandidateType,   // host | srflx | prflx | relay | unknown
    string AddressFamily,   // ipv4 | ipv6 | unknown
    string Protocol,        // udp | tcp | unknown
    string RelayProtocol);  // udp | tcp | tls | none | unknown

/// <summary>
/// Pure, static classifier for ICE candidate lines (both SDP "a=candidate:..."/"candidate:..."
/// wire form and SIPSorcery's bare RTCIceCandidate.candidate form with no "candidate:" prefix).
/// Deliberately independent of <see cref="IceCandidateInspector"/> — that type answers a
/// different question (is this candidate routable enough to forward to the peer?); this one
/// answers "what contract-v1 enum values describe this candidate?". Kept pure/static so it is
/// unit-testable without a live PeerConnection.
/// </summary>
public static class IceCandidateClassifier
{
    /// <summary>
    /// Classify a single candidate line. Returns all-"unknown" (RelayProtocol="unknown") for
    /// malformed/unparseable input — callers must fold this into the bounded telemetry enums,
    /// never crash and never guess.
    /// </summary>
    public static IceCandidateClassification Classify(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return new IceCandidateClassification("unknown", "unknown", "unknown", "unknown");

        var s = candidate.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

        // "[candidate:]<foundation> <component> <transport> <priority> <address> <port> typ <type> ..."
        // Same layout IceCandidateInspector.IsRoutable relies on: address at index 4,
        // "typ" token at index 6, type at index 7 (the optional "candidate:" prefix is glued
        // to the foundation with no space, so indices are identical in both wire forms).
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8 || !parts[6].Equals("typ", StringComparison.OrdinalIgnoreCase))
            return new IceCandidateClassification("unknown", "unknown", "unknown", "unknown");

        string candidateType = parts[7].ToLowerInvariant() switch
        {
            "host" => "host",
            "srflx" => "srflx",
            "prflx" => "prflx",
            "relay" => "relay",
            _ => "unknown"
        };

        string protocol = parts[2].ToLowerInvariant() switch
        {
            "udp" => "udp",
            "tcp" => "tcp",
            _ => "unknown"
        };

        string family = "unknown";
        if (IPAddress.TryParse(parts[4], out var ip))
        {
            family = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
        }

        // relay_protocol: "none" when this candidate is not itself a relay candidate.
        // A plain ICE candidate line never carries the TURN allocation's TLS-vs-TCP
        // distinction (turn: vs turns: is a property of the server URL, not the candidate) —
        // so for relay candidates we can only report the candidate's own wire transport
        // (udp/tcp); "tls" can never be derived from the candidate line alone. This is a
        // documented Phase 00 limitation, not a bug: see HostSelectedPathReader for the
        // richer (but still bounded) selected-pair path that at least reads real SIPSorcery
        // candidate objects instead of re-parsing strings.
        string relayProtocol = candidateType == "relay"
            ? protocol
            : (candidateType == "unknown" ? "unknown" : "none");

        return new IceCandidateClassification(candidateType, family, protocol, relayProtocol);
    }
}
