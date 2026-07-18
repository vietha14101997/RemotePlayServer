using System;
using System.IO;
using System.Threading;
using RemotePlayServer.Application.Security;

namespace RemotePlayServer.Tests;

/// <summary>
/// Unit tests for the Phase 1 pairing/auth security primitives (contract v1):
/// PairingStore (persistent allowlist, fail-closed), PeerAuthGate (session binding),
/// and PairingSecretManager (one-use QR secret + MAC/SAS, replay/expiry resistance).
/// Logic-only; no SIPSorcery/WebRTC dependency.
/// </summary>
public class PairingStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "rs-pairing-tests", Guid.NewGuid().ToString("N") + ".json");

    private const string Fp = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    [Fact]
    public void Contains_False_ForUnknownOrEmpty()
    {
        var store = new PairingStore(TempPath());
        Assert.False(store.Contains(Fp));
        Assert.False(store.Contains(null));
        Assert.False(store.Contains(""));
        Assert.False(store.Contains("   "));
    }

    [Fact]
    public void Add_Then_Contains_CaseInsensitive()
    {
        var store = new PairingStore(TempPath());
        store.Add(Fp, "phone");
        Assert.True(store.Contains(Fp));
        Assert.True(store.Contains(Fp.ToLowerInvariant()));
        Assert.True(store.Contains("  " + Fp + "  "));
    }

    [Fact]
    public void Remove_Unpairs()
    {
        var store = new PairingStore(TempPath());
        store.Add(Fp, "phone");
        Assert.True(store.Remove(Fp));
        Assert.False(store.Contains(Fp));
        Assert.False(store.Remove(Fp)); // already gone
    }

    [Fact]
    public void Persists_AcrossInstances()
    {
        var path = TempPath();
        new PairingStore(path).Add(Fp, "phone");

        var reloaded = new PairingStore(path); // fresh instance reads the same file
        Assert.True(reloaded.Contains(Fp));
        Assert.Single(reloaded.List());
    }

    [Fact]
    public void CorruptFile_YieldsEmptyAllowlist_FailClosed()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json ]");

        var store = new PairingStore(path);
        Assert.False(store.Contains(Fp)); // never "trust all" on parse failure
        Assert.Empty(store.List());
    }
}

public class PeerAuthGateTests
{
    private static PeerAuthGate NewGate() =>
        new PeerAuthGate(new PairingStore(
            Path.Combine(Path.GetTempPath(), "rs-gate-tests", Guid.NewGuid().ToString("N") + ".json")));

    private const string Fp = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    [Fact]
    public void IsPaired_False_ForUnboundSession()
    {
        Assert.False(NewGate().IsPaired(Guid.NewGuid()));
    }

    [Fact]
    public void TryBindIfPaired_False_WhenNotAllowlisted()
    {
        var gate = NewGate();
        Assert.False(gate.TryBindIfPaired(Guid.NewGuid(), Fp)); // not in store
        Assert.False(gate.TryBindIfPaired(Guid.NewGuid(), null));
    }

    [Fact]
    public void TryBindIfPaired_True_ForReconnectOfPairedPeer()
    {
        var gate = NewGate();
        var id = Guid.NewGuid();
        gate.Store.Add(Fp, "phone"); // previously paired
        Assert.True(gate.TryBindIfPaired(id, Fp));
        Assert.True(gate.IsPaired(id));
    }

    [Fact]
    public void PairAndBind_Persists_And_Binds()
    {
        var gate = NewGate();
        var id = Guid.NewGuid();
        gate.PairAndBind(id, Fp, "phone");
        Assert.True(gate.IsPaired(id));
        Assert.True(gate.Store.Contains(Fp));
    }

    [Fact]
    public void Unbind_DropsSession_KeepsPairing()
    {
        var gate = NewGate();
        var id = Guid.NewGuid();
        gate.PairAndBind(id, Fp, "phone");
        gate.Unbind(id);
        Assert.False(gate.IsPaired(id));       // session no longer authorized
        Assert.True(gate.Store.Contains(Fp));  // but peer stays paired for next reconnect
    }
}

public class PairingSecretManagerTests
{
    private const string HostFp = "sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
    private const string ClientFp = "sha-256 11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";

    [Fact]
    public void Mint_ProducesDistinctOffers()
    {
        var mgr = new PairingSecretManager();
        var a = mgr.Mint();
        var b = mgr.Mint();
        Assert.NotEqual(a.Sid, b.Sid);
        Assert.NotEqual(a.Psk, b.Psk);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.True(a.ExpiryUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ComputeMac_IsDeterministic_And_TagSensitive()
    {
        var mac1 = PairingSecretManager.ComputeMac("psk", "C", "sid", "n", HostFp, ClientFp);
        var mac2 = PairingSecretManager.ComputeMac("psk", "C", "sid", "n", HostFp, ClientFp);
        var macH = PairingSecretManager.ComputeMac("psk", "H", "sid", "n", HostFp, ClientFp);
        Assert.Equal(mac1, mac2);        // deterministic
        Assert.NotEqual(mac1, macH);     // tag changes the MAC
    }

    [Fact]
    public void ComputeMac_DiffersByKey()
    {
        var a = PairingSecretManager.ComputeMac("psk-a", "C", "sid", "n", HostFp, ClientFp);
        var b = PairingSecretManager.ComputeMac("psk-b", "C", "sid", "n", HostFp, ClientFp);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void VerifyClientProof_Succeeds_ForValidMac_ThenReturnsPsk()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);

        var psk = mgr.VerifyClientProof(offer.Sid, macC, HostFp, ClientFp);
        Assert.Equal(offer.Psk, psk); // caller uses it to build the host reply MAC
    }

    [Fact]
    public void VerifyClientProof_Fails_ForBadMac()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        Assert.Null(mgr.VerifyClientProof(offer.Sid, "not-the-right-mac", HostFp, ClientFp));
    }

    [Fact]
    public void VerifyClientProof_Fails_ForWrongFingerprints()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);
        // Attacker presents a different client fingerprint than the one the MAC was bound to.
        Assert.Null(mgr.VerifyClientProof(offer.Sid, macC, HostFp, "sha-256 DE:AD:BE:EF"));
    }

    [Fact]
    public void VerifyClientProof_Fails_OnReplay_SingleUse()
    {
        var mgr = new PairingSecretManager();
        var offer = mgr.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);

        Assert.NotNull(mgr.VerifyClientProof(offer.Sid, macC, HostFp, ClientFp)); // first use OK
        Assert.Null(mgr.VerifyClientProof(offer.Sid, macC, HostFp, ClientFp));    // replay rejected
    }

    [Fact]
    public void VerifyClientProof_Fails_ForUnknownSid()
    {
        var mgr = new PairingSecretManager();
        Assert.Null(mgr.VerifyClientProof("never-minted", "x", HostFp, ClientFp));
    }

    [Fact]
    public void ComputeSas_IsFourDigits_And_Deterministic()
    {
        var sas1 = PairingSecretManager.ComputeSas("psk", "sid", "n", HostFp, ClientFp);
        var sas2 = PairingSecretManager.ComputeSas("psk", "sid", "n", HostFp, ClientFp);
        Assert.Equal(sas1, sas2);
        Assert.Matches(@"^\d{4}$", sas1);
    }

    [Fact]
    public void VerifyClientProof_Fails_AfterExpiry()
    {
        var mgr = new PairingSecretManager(TimeSpan.FromMilliseconds(1));
        var offer = mgr.Mint();
        var macC = PairingSecretManager.ComputeMac(offer.Psk, "C", offer.Sid, offer.Nonce, HostFp, ClientFp);
        Thread.Sleep(10); // offer TTL elapses
        Assert.Null(mgr.VerifyClientProof(offer.Sid, macC, HostFp, ClientFp)); // expired → fail-closed
    }

    [Fact]
    public void Mint_SweepsExpiredUnconsumedSecrets_NoUnboundedGrowth()
    {
        var mgr = new PairingSecretManager(TimeSpan.FromMilliseconds(1));
        mgr.Mint();                              // displayed-but-never-scanned QR
        Thread.Sleep(10);                        // it expires
        Assert.Equal(1, mgr.PendingSecretCount); // still retained until the next mint sweeps
        mgr.Mint();                              // lazy sweep purges the stale sid
        Assert.Equal(1, mgr.PendingSecretCount); // only the fresh offer survives
    }
}
