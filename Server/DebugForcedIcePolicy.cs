#nullable enable
using RemotePlayServer.Core;

namespace RemotePlayServer.Server;

/// <summary>
/// DEBUG-only, local-admin-set flag that forces the Host's own PeerConnections to
/// iceTransportPolicy=relay, for symmetric WAN testing against the Android client's
/// equivalent debug force-relay flag (telemetry-snapshot-contract-v1.md, "Forced-path
/// policy"). Default OFF; normal (STUN-only) ICE is used otherwise.
///
/// Fail-closed by construction, not just by runtime check: in a RELEASE build this type
/// compiles to a read-only `false` with no setter at all — there is no field to flip, no
/// config key, and no network message handler wired to it anywhere in the codebase, so no
/// remote actor can ever force relay-only in a shipped build. Only local, in-process code
/// (e.g. a future debug menu) may set it, and only in DEBUG builds.
///
/// KNOWN GAP (documented, not fixed by this flag): SIPSorceryStreamer.BuildIceConfiguration
/// is intentionally STUN-only — feeding SIPSorcery 8.0.23 any TURN server entries breaks its
/// candidate gathering entirely (see that method's doc comment). Setting ForceRelayOnly=true
/// today makes the Host request relay-only candidates from a STUN-only server list, which
/// yields NO usable candidates (STUN servers never hand out relay candidates). Forcing
/// relay-only on the Host therefore requires solving the "TURN breaks SIPSorcery gathering"
/// bug first — tracked separately, out of scope for Phase 00's runtime-truth baseline.
/// </summary>
public static class DebugForcedIcePolicy
{
#if DEBUG
    private static volatile bool _forceRelayOnly;

    /// <summary>
    /// Local-admin-only setter. NEVER wire this to a network/signaling message handler —
    /// doing so would defeat the "release rejects unsigned remote force policy" requirement
    /// even in a debug build used for internal testing.
    /// </summary>
    public static bool ForceRelayOnly
    {
        get => _forceRelayOnly;
        set
        {
            _forceRelayOnly = value;
            Logger.Warn($"[DebugForcedIcePolicy] ForceRelayOnly set to {value} (DEBUG build only, local-admin controlled)");
        }
    }
#else
    /// <summary>RELEASE builds: permanently false, no setter exists — nothing can enable forcing.</summary>
    public static bool ForceRelayOnly => false;
#endif
}
