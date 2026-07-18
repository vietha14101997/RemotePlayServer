#nullable enable
using System;
using System.Collections.Generic;

namespace RemotePlayServer.Application.Security;

/// <summary>
/// Authorization gate binding an active session (its <c>clientId</c> Guid) to a paired peer
/// fingerprint. A session becomes "paired" only via:
///   • <see cref="TryBindIfPaired"/> — reconnect whose negotiated DTLS fingerprint is already in
///     the <see cref="PairingStore"/> allowlist (DTLS proves key possession ⇒ authenticated), or
///   • <see cref="PairAndBind"/> — a fresh pairing handshake that just succeeded.
///
/// The input path (PhaseProtocolHandler host + viewer OnInputReceived lambdas) and the media-start
/// path call <see cref="IsPaired"/> and MUST drop/deny when it is false. Fail-closed: an unknown
/// session is never paired.
///
/// Process-wide <see cref="Instance"/> is provided for the wiring sites (which only have the session
/// Guid in scope); the public constructor keeps the type unit-testable with a temp-path store.
/// </summary>
public sealed class PeerAuthGate
{
    private static readonly Lazy<PeerAuthGate> _instance = new(() => new PeerAuthGate(new PairingStore()));

    /// <summary>Process-wide gate backed by the default %APPDATA% pairing store.</summary>
    public static PeerAuthGate Instance => _instance.Value;

    private readonly PairingStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, string> _boundFingerprint = new();

    public PeerAuthGate(PairingStore store) => _store = store;

    /// <summary>Underlying allowlist (for pairing handshake lookups / device-management UI).</summary>
    public PairingStore Store => _store;

    /// <summary>True IFF this session has been bound to a paired peer. Unknown session ⇒ false.</summary>
    public bool IsPaired(Guid clientId)
    {
        lock (_lock) return _boundFingerprint.ContainsKey(clientId);
    }

    /// <summary>
    /// Reconnect path: bind the session ONLY IF <paramref name="fingerprint"/> is already an
    /// allowlisted paired peer. Returns whether the bind (and thus authorization) succeeded.
    /// </summary>
    public bool TryBindIfPaired(Guid clientId, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || !_store.Contains(fingerprint)) return false;
        lock (_lock) _boundFingerprint[clientId] = fingerprint.Trim();
        return true;
    }

    /// <summary>
    /// Pairing path: after a verified handshake, persist the peer to the allowlist and bind the
    /// current session. Caller MUST have validated the pairing proof before calling this.
    /// </summary>
    public void PairAndBind(Guid clientId, string fingerprint, string label)
    {
        _store.Add(fingerprint, label);
        lock (_lock) _boundFingerprint[clientId] = fingerprint.Trim();
    }

    /// <summary>Release the session binding on disconnect (does not unpair the peer).</summary>
    public void Unbind(Guid clientId)
    {
        lock (_lock) _boundFingerprint.Remove(clientId);
    }
}
