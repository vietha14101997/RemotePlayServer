#nullable enable
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RemotePlayServer.Application.Security;

/// <summary>
/// Mints and verifies the one-use pairing secrets that back the QR handshake (contract v1).
///
/// A pairing offer = {psk, nonce, exp, sid} embedded in the QR. The client proves knowledge of the
/// psk by returning macC = HMAC-SHA256(psk, "RS-PAIR-v1|C|sid|nonce|hostFp|clientFp"); the host then
/// replies with the "H"-tagged MAC and both persist the peer fingerprint. Single-use (sid consumed),
/// time-boxed (exp), and replay-resistant (consumed sids are remembered and rejected).
///
/// The HMAC key is the psk STRING's UTF-8 bytes (both platforms MUST agree — see the contract).
/// </summary>
public sealed class PairingSecretManager
{
    private const string MacVersion = "RS-PAIR-v1";
    private const int PskBytes = 32;
    private const int NonceBytes = 16;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);

    // Process-wide instance so the QR-minting side (ServerService) and the handshake-verifying
    // side (PhaseProtocolHandler, on any transport — LAN/relay/USB) share one set of live/consumed
    // sids. Mirrors the PeerAuthGate.Instance / HostDtlsCertificate.Instance singleton pattern
    // used elsewhere in this security spine. The public constructor above remains for unit tests
    // that need an isolated instance.
    private static readonly Lazy<PairingSecretManager> _instance = new(() => new PairingSecretManager());

    /// <summary>Process-wide pairing secret manager shared by QR minting and proof verification.</summary>
    public static PairingSecretManager Instance => _instance.Value;

    /// <summary>What the host embeds in the QR payload.</summary>
    public sealed record PairingOffer(string Psk, string Nonce, long ExpiryUnixMs, string Sid);

    /// <summary>
    /// Result of a successful client-proof verification. The caller needs BOTH the psk and the
    /// nonce to build the host's reply MAC/SAS (contract v1: macH/sas inputs include the nonce),
    /// but the nonce is otherwise private manager state — never sent by the client itself.
    /// </summary>
    public sealed record VerifiedPairing(string Psk, string Nonce);

    private sealed record ActiveSecret(string Psk, string Nonce, long ExpiryUnixMs);

    private readonly ConcurrentDictionary<string, ActiveSecret> _active = new();  // sid -> secret
    private readonly ConcurrentDictionary<string, byte> _consumed = new();        // sid -> tombstone

    /// <summary>Mint a fresh one-use offer for the QR payload.</summary>
    public PairingOffer Mint()
    {
        var psk = Base64Url(RandomNumberGenerator.GetBytes(PskBytes));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(NonceBytes));
        var sid = Guid.NewGuid().ToString("N");
        var exp = DateTimeOffset.UtcNow.Add(Ttl).ToUnixTimeMilliseconds();
        _active[sid] = new ActiveSecret(psk, nonce, exp);
        return new PairingOffer(psk, nonce, exp, sid);
    }

    /// <summary>Compute a tagged pairing MAC (base64). Static so tests + both roles share one impl.</summary>
    public static string ComputeMac(
        string psk, string tag, string sid, string nonce, string hostFp, string clientFp)
    {
        var msg = $"{MacVersion}|{tag}|{sid}|{nonce}|{hostFp.Trim()}|{clientFp.Trim()}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(psk));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(msg)));
    }

    /// <summary>Derive the 4-digit SAS for optional user visual comparison.</summary>
    public static string ComputeSas(string psk, string sid, string nonce, string hostFp, string clientFp)
    {
        var raw = Convert.FromBase64String(ComputeMac(psk, "SAS", sid, nonce, hostFp, clientFp));
        uint n = ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
        return (n % 10000).ToString("D4");
    }

    /// <summary>
    /// Verify the client's proof for <paramref name="sid"/>. On success consumes the sid (single-use)
    /// and returns the psk so the caller can build the host reply MAC + SAS. Returns null on ANY
    /// failure (unknown/expired/already-consumed sid, or MAC mismatch) — fail-closed.
    /// </summary>
    public string? VerifyClientProof(string sid, string clientMac, string hostFp, string clientFp)
        => VerifyClientProofWithNonce(sid, clientMac, hostFp, clientFp)?.Psk;

    /// <summary>
    /// Same verification as <see cref="VerifyClientProof"/>, but also returns the nonce — the
    /// Host-side wiring (PhaseProtocolHandler) needs it to compute the "H"-tagged reply MAC/SAS,
    /// which are NOT derivable from the psk alone. Returns null on ANY failure (fail-closed).
    /// </summary>
    public VerifiedPairing? VerifyClientProofWithNonce(string sid, string clientMac, string hostFp, string clientFp)
    {
        if (string.IsNullOrEmpty(sid) || _consumed.ContainsKey(sid)) return null;
        if (!_active.TryGetValue(sid, out var secret)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > secret.ExpiryUnixMs) { Consume(sid); return null; }

        var expected = ComputeMac(secret.Psk, "C", sid, secret.Nonce, hostFp, clientFp);
        if (!FixedTimeEquals(expected, clientMac)) return null;

        Consume(sid); // single-use: burn the sid whether or not the reply is later delivered
        return new VerifiedPairing(secret.Psk, secret.Nonce);
    }

    private void Consume(string sid) { _active.TryRemove(sid, out _); _consumed[sid] = 1; }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
