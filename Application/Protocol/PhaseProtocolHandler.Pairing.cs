#nullable enable
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Application.Security;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;

namespace RemotePlayServer.Application.Protocol;

/// <summary>
/// Phase 1 pairing/auth handshake wiring (pairing-protocol-contract-v1.md). Entirely gated
/// behind <see cref="PairingPolicy.RequirePairing"/> (default OFF) — when disabled every method
/// here is a fast no-op so the legacy LAN streaming path is byte-for-byte unchanged.
///
/// Flow: after the main PC's DTLS connects (Phase2's OnAllTracksReady), the caller invokes
/// <see cref="EnsurePairedBeforeMediaAsync"/> BEFORE sending ice_ready / starting capture:
///   - pairing disabled, or the negotiated client DTLS fingerprint is already a known paired
///     peer (reconnect) ⇒ returns true, caller proceeds immediately via
///     <see cref="CompleteConnectionReadyAsync"/>.
///   - unknown/unpaired fingerprint ⇒ returns false; a pairing_required notice is sent and the
///     session waits for a pairing_client_proof message (routed here from the ICE-exchange
///     message loop). On successful verification the deferred ice_ready/capture-start flow is
///     resumed; on failure the session is closed. No media, no input, ever, for a session that
///     never completes this gate — fail-closed.
/// </summary>
public partial class PhaseProtocolHandler
{
    // Captured at the pairing gate so a later pairing_client_proof (arriving asynchronously via
    // the message loop) can be verified without re-deriving state, and so the deferred flow can
    // be resumed correctly once the proof succeeds.
    private string? _pendingPairingHostFp;
    private string? _pendingPairingClientFp;
    private int _pendingActualMonitors;

    /// <summary>Which deferred action to resume once a pending session gets paired.</summary>
    private enum PendingResumeKind { None, CompleteConnection, EnterRelay }
    private PendingResumeKind _pendingResumeKind = PendingResumeKind.None;

    /// <summary>
    /// True IFF this session may use media/input right now: pairing is disabled entirely, or
    /// this specific session has been bound to a paired peer fingerprint. Fail-closed default.
    /// Shared by the input-gate lambdas (host + viewer OnInputReceived) and the UPnP exposure
    /// gate (no port-mapping candidates advertised for an unpaired session).
    /// </summary>
    private bool IsPeerAuthorized() =>
        !PairingPolicy.RequirePairing || PeerAuthGate.Instance.IsPaired(_clientId);

    /// <summary>
    /// Synchronous pairing check shared by every media-entry gate (P2P DTLS-connect flow AND
    /// the media-relay fallback, which bypasses DTLS entirely). Attempts the reconnect bind
    /// (negotiated DTLS fingerprint already in the PairingStore ⇒ no proof round-trip needed);
    /// on failure, remembers the fingerprints and fires a fire-and-forget pairing_required
    /// notice. The caller decides what to resume via <paramref name="resumeKind"/> — stashed so
    /// <see cref="HandlePairingClientProofAsync"/> knows which deferred action to continue.
    /// </summary>
    private bool EnsurePairedSync(PendingResumeKind resumeKind)
    {
        if (!PairingPolicy.RequirePairing) return true; // legacy: pairing disabled entirely
        if (PeerAuthGate.Instance.IsPaired(_clientId)) return true; // already bound this session

        var clientFp = SdpFingerprintExtractor.ExtractDtlsFingerprint(_lastOfferSdp);
        var hostFp = HostDtlsCertificate.Instance.Fingerprint;

        if (PeerAuthGate.Instance.TryBindIfPaired(_clientId, clientFp))
        {
            Logger.Info("[Protocol] Pairing: reconnect bound via already-known DTLS fingerprint");
            return true;
        }

        // Unknown/unpaired peer: remember the fingerprints + which flow to resume so a later
        // pairing_client_proof (fresh QR pairing) can be verified and the flow continued.
        _pendingPairingHostFp = hostFp;
        _pendingPairingClientFp = clientFp;
        _pendingResumeKind = resumeKind;

        Logger.Warn("[Protocol] Pairing: session not bound — sending pairing_required and " +
                    "awaiting pairing_client_proof. No media/input until paired.");
        // Fire-and-forget: SendMessageAsync/SendTextAsync already catch+log internally, and every
        // caller of this sync gate (EnterMediaRelayMode, the DTLS-connect callback) is either void
        // or must not block on the network round-trip to decide "not yet authorized".
        _ = SendMessageAsync(new PairingRequiredMessage());

        return false;
    }

    /// <summary>
    /// Phase 1 pairing gate — call after DTLS connects, BEFORE sending ice_ready or starting
    /// capture. See class doc for the full flow.
    /// </summary>
    private Task<bool> EnsurePairedBeforeMediaAsync(int actualMonitors)
    {
        _pendingActualMonitors = actualMonitors;
        return Task.FromResult(EnsurePairedSync(PendingResumeKind.CompleteConnection));
    }

    /// <summary>
    /// Client -> Host pairing proof (contract v1, message 1). Verifies macC against the psk
    /// minted for the given sid; on success persists + binds the peer and resumes the deferred
    /// ice_ready/capture-start flow. ANY failure (malformed message, missing fingerprint, bad
    /// MAC, expired/replayed sid) sends pairing_failed and closes the socket — fail-closed, no
    /// media/input for this session, ever.
    /// </summary>
    private async Task HandlePairingClientProofAsync(string json)
    {
        if (!PairingPolicy.RequirePairing) return; // pairing disabled — ignore stray proof messages

        var msg = ProtocolMessageParser.Parse<PairingClientProofMessage>(json);
        var hostFp = _pendingPairingHostFp ?? HostDtlsCertificate.Instance.Fingerprint;
        var clientFp = _pendingPairingClientFp ?? SdpFingerprintExtractor.ExtractDtlsFingerprint(_lastOfferSdp);

        if (msg == null || string.IsNullOrEmpty(msg.Sid) || string.IsNullOrEmpty(msg.MacC) || string.IsNullOrEmpty(clientFp))
        {
            Logger.Error("[Protocol] Pairing: malformed pairing_client_proof or missing client fingerprint — rejecting");
            await FailPairingAsync("malformed_proof");
            return;
        }

        var verified = PairingSecretManager.Instance.VerifyClientProofWithNonce(msg.Sid, msg.MacC, hostFp, clientFp);
        if (verified == null)
        {
            Logger.Error("[Protocol] Pairing: client proof verification FAILED (bad MAC, expired, or replayed sid)");
            await FailPairingAsync("invalid_proof");
            return;
        }

        // Success: persist the peer fingerprint + bind THIS session before replying.
        PeerAuthGate.Instance.PairAndBind(_clientId, clientFp, label: _remoteIp?.ToString() ?? "paired-device");

        var macH = PairingSecretManager.ComputeMac(verified.Psk, "H", msg.Sid, verified.Nonce, hostFp, clientFp);
        var sas = PairingSecretManager.ComputeSas(verified.Psk, msg.Sid, verified.Nonce, hostFp, clientFp);
        await SendMessageAsync(new PairingHostProofMessage { MacH = macH, Sas = sas });
        Logger.Info("[Protocol] Pairing: client proof verified — session bound, host proof sent");

        // Resume whichever flow was deferred waiting for this proof.
        var resumeKind = _pendingResumeKind;
        _pendingResumeKind = PendingResumeKind.None;
        switch (resumeKind)
        {
            case PendingResumeKind.EnterRelay:
                EnterMediaRelayMode(); // re-entrant: now paired, so its own gate passes immediately
                break;
            case PendingResumeKind.CompleteConnection:
            default:
                await CompleteConnectionReadyAsync(_pendingActualMonitors);
                break;
        }
    }

    /// <summary>Reject a pairing attempt: notify the client, then close the connection.</summary>
    private async Task FailPairingAsync(string reason)
    {
        try { await SendMessageAsync(new PairingFailedMessage { Reason = reason }); } catch { }
        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "pairing_failed", CancellationToken.None); }
        catch { }
    }
}
