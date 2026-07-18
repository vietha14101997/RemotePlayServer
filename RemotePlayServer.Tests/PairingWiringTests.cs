using System;
using RemotePlayServer.Application.Security;

namespace RemotePlayServer.Tests;

/// <summary>
/// Unit tests for the Phase 1 wiring additions layered on top of the already-tested pairing
/// spine (PairingSecurityTests.cs): the process-wide <see cref="PairingSecretManager.Instance"/>
/// singleton, the nonce-returning verification overload the Host wiring needs to build the
/// host-side reply MAC/SAS, and the <see cref="PairingPolicy"/> safe-landing default. Logic-only;
/// no SIPSorcery/WebRTC/hardware dependency.
/// </summary>
public class PairingSecretManagerInstanceTests
{
    private const string HostFp = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
    private const string ClientFp = "sha-256 11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";

    [Fact]
    public void Instance_IsSameReferenceAcrossCalls()
    {
        // The QR-minting side (ServerService) and the proof-verifying side (PhaseProtocolHandler)
        // run on different transports (LAN direct WS vs. relay-bridged) and must share one
        // process-wide sid/nonce ledger, or a legitimately minted psk would never verify.
        Assert.Same(PairingSecretManager.Instance, PairingSecretManager.Instance);
    }

    [Fact]
    public void Instance_MintAndVerify_RoundTrips()
    {
        var offer = PairingSecretManager.Instance.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);

        var verified = PairingSecretManager.Instance.VerifyClientProofWithNonce(offer.Sid, macC, HostFp, ClientFp);

        Assert.NotNull(verified);
        Assert.Equal(offer.Psk, verified!.Psk);
        Assert.Equal(offer.Nonce, verified.Nonce);
    }

    [Fact]
    public void VerifyClientProofWithNonce_ReturnsNonceNeededForHostReply()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);

        var verified = mgr.VerifyClientProofWithNonce(offer.Sid, macC, HostFp, ClientFp);

        Assert.NotNull(verified);
        Assert.Equal(offer.Nonce, verified!.Nonce);

        // The nonce is exactly what the Host-side wiring needs to build macH/sas — confirm they
        // compute identically to what an independent verifier (e.g. the Android client) would.
        var expectedMacH = PairingSecretManager.ComputeMac(offer.Psk, "H", offer.Sid, offer.Nonce, HostFp, ClientFp);
        var actualMacH = PairingSecretManager.ComputeMac(verified.Psk, "H", offer.Sid, verified.Nonce, HostFp, ClientFp);
        Assert.Equal(expectedMacH, actualMacH);
    }

    [Fact]
    public void VerifyClientProofWithNonce_Fails_ForBadMac_SameAsStringOverload()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        Assert.Null(mgr.VerifyClientProofWithNonce(offer.Sid, "wrong-mac", HostFp, ClientFp));
    }

    [Fact]
    public void VerifyClientProofWithNonce_SingleUse_SameAsStringOverload()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);

        Assert.NotNull(mgr.VerifyClientProofWithNonce(offer.Sid, macC, HostFp, ClientFp));   // first use OK
        Assert.Null(mgr.VerifyClientProofWithNonce(offer.Sid, macC, HostFp, ClientFp));      // replay rejected
    }
}

public class PairingPolicyTests
{
    [Fact]
    public void RequirePairing_DefaultsToFalse_SafeLanding()
    {
        // Phase 1 landing decision: RequirePairing must default OFF so a plain build stays
        // byte-for-byte legacy behaviour until E2E hardware validation flips it on. Whatever
        // Configuration/pairing-settings.json this test process finds (freshly created default,
        // or none), the flag must never come up true on its own.
        Assert.False(PairingPolicy.RequirePairing);
    }
}

/// <summary>
/// Regression coverage for the code-review fixes: (1) the relay-media fallback path and (2) the
/// disconnect-cleanup path both drive the PROCESS-WIDE <see cref="PeerAuthGate.Instance"/> (not
/// an injectable per-test instance), since PhaseProtocolHandler only ever reaches the shared
/// singleton (SIPSorcery/WebSocket scaffolding needed to instantiate the handler itself isn't
/// unit-testable here — see the fix report). Deliberately READ-ONLY against the singleton: it is
/// backed by the real per-user %APPDATA%\RemoteScreen\pairing.json, so tests must not call
/// PairAndBind/Add on it (would pollute the developer's real pairing store across test runs).
/// The full bind/unbind/persist behaviour is already exercised in isolation by
/// PeerAuthGateTests above (temp-path store) — these two tests only confirm the singleton is
/// wired the same way the new gate sites (EnsurePairedSync, CleanupAsync) rely on.
/// </summary>
public class PeerAuthGateInstanceTests
{
    [Fact]
    public void Instance_IsSameReferenceAcrossCalls()
    {
        Assert.Same(PeerAuthGate.Instance, PeerAuthGate.Instance);
    }

    [Fact]
    public void Instance_UnboundSession_IsNotAuthorized()
    {
        // Mirrors IsPeerAuthorized()/EnsurePairedSync's very first check: a brand-new session id
        // that was never bound must read as unpaired — this is what made the relay-media path's
        // missing gate a fail-OPEN hole (it was starting capture without ever checking this).
        var freshClientId = Guid.NewGuid();
        Assert.False(PeerAuthGate.Instance.IsPaired(freshClientId));

        // Unbind on a session that was never bound must be a safe no-op (CleanupAsync calls
        // this unconditionally for every disconnecting session, paired or not).
        PeerAuthGate.Instance.Unbind(freshClientId);
        Assert.False(PeerAuthGate.Instance.IsPaired(freshClientId));
    }
}
