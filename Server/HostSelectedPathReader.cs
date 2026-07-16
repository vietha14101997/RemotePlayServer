#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RemotePlayServer.Core;
using SIPSorcery.Net;

namespace RemotePlayServer.Server;

/// <summary>Selected ICE candidate pair for one RTCPeerConnection, contract-v1 enum strings.</summary>
public readonly record struct SelectedPathSnapshot(
    string LocalCandidateType,
    string RemoteCandidateType,
    string AddressFamily,
    string Protocol,
    string RelayProtocol,
    string PathClass);

/// <summary>
/// Reads SIPSorcery's REAL nominated ICE candidate pair for a PeerConnection — the media-path
/// source of truth (not a heuristic like <see cref="Application.Streaming.SIPSorceryStreamer.DetectIceConnectionType"/>,
/// which only compares the connected remote endpoint against a known TURN IP list for the
/// existing UI display and is left untouched by this phase).
///
/// LIMITATION (verified against SIPSorcery 8.0.23 via metadata inspection — no public API
/// exists for this): RTCPeerConnection's public surface (IRTCPeerConnection) does NOT expose
/// its internal RtpIceChannel or the nominated candidate pair. The underlying field
/// (RTCPeerConnection._rtpIceChannel) is `private`; the ICE channel's own `NominatedEntry`
/// property IS public, as are ChecklistEntry.LocalCandidate/RemoteCandidate and every
/// RTCIceCandidate property we read (type/protocol/address). So this is a single, narrow
/// reflection bridge to reach the one private field — every value actually read afterwards
/// comes through SIPSorcery's normal public object model, nothing is fabricated. If a future
/// SIPSorcery version renames/removes the field, <see cref="TryRead"/> fails closed to `null`
/// (never throws to the caller) — treat `null` as "selected pair not yet known", not an error.
/// </summary>
public static class HostSelectedPathReader
{
    private static readonly FieldInfo? IceChannelField =
        typeof(RTCPeerConnection).GetField("_rtpIceChannel", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Returns the currently-nominated pair for <paramref name="pc"/>, or null when the pair
    /// isn't known yet (not connected, ICE not nominated, or the reflection bridge failed).
    /// </summary>
    public static SelectedPathSnapshot? TryRead(RTCPeerConnection? pc)
    {
        if (pc == null || IceChannelField == null) return null;

        try
        {
            if (IceChannelField.GetValue(pc) is not RtpIceChannel iceChannel) return null;

            var nominated = iceChannel.NominatedEntry;
            var local = nominated?.LocalCandidate;
            var remote = nominated?.RemoteCandidate;
            if (local == null || remote == null) return null;

            string localType = ClassifyCandidateType(local.type);
            string remoteType = ClassifyCandidateType(remote.type);
            string family = ClassifyFamily(local.address);
            string protocol = local.protocol == RTCIceProtocol.tcp ? "tcp" : "udp";

            bool isRelay = localType == "relay" || remoteType == "relay";
            string relayProtocol = "none";
            if (isRelay)
            {
                var relayCandidate = localType == "relay" ? local : remote;
                relayProtocol = DetectRelayTransport(relayCandidate);
            }

            string pathClass = isRelay ? "relay" : "direct";

            return new SelectedPathSnapshot(localType, remoteType, family, protocol, relayProtocol, pathClass);
        }
        catch (Exception ex)
        {
            // Never let a diagnostics read affect the connection — fail closed to "unknown".
            Logger.Debug($"[HostSelectedPathReader] TryRead failed (non-fatal, telemetry only): {ex.Message}");
            return null;
        }
    }

    private static string ClassifyCandidateType(RTCIceCandidateType type) => type switch
    {
        RTCIceCandidateType.host => "host",
        RTCIceCandidateType.srflx => "srflx",
        RTCIceCandidateType.prflx => "prflx",
        RTCIceCandidateType.relay => "relay",
        _ => "unknown"
    };

    private static string ClassifyFamily(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "unknown";
        // mDNS ".local" hostnames and anything unparseable fold to "unknown" (bounded enum).
        if (!IPAddress.TryParse(address, out var ip)) return "unknown";
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
    }

    /// <summary>
    /// Best-effort TURN transport for a relay candidate. SIPSorcery's RTCIceCandidate exposes
    /// an `IceServer` back-reference, but its type (SIPSorcery.Net.IceServer) only carries the
    /// resolved endpoint + System.Net.Sockets.ProtocolType — NOT the turn:/turns: URL scheme —
    /// so TLS can never be distinguished from plain TCP here. We report the candidate's own
    /// wire transport (udp/tcp) and never claim "tls" without real evidence for it.
    /// </summary>
    private static string DetectRelayTransport(RTCIceCandidate relayCandidate) =>
        relayCandidate.protocol == RTCIceProtocol.tcp ? "tcp" : "udp";
}
