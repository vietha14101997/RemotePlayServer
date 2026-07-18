#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemotePlayServer.Application.Security;

/// <summary>
/// Persistent allowlist of paired peer DTLS fingerprints (Phase 1 security spine).
/// Only a peer whose STABLE, pinned DTLS fingerprint is in this store may stream or inject input.
///
/// Fingerprints are NOT secret (they are advertised in the SDP <c>a=fingerprint</c> line), so a
/// plain JSON file under the per-user <c>%APPDATA%\RemoteScreen</c> directory (already isolated per
/// Windows user) is sufficient. The actual secret — the host's DTLS private key — is protected
/// separately by <see cref="HostDtlsCertificate"/>.
///
/// Thread-safe. Saves are atomic (temp file + overwrite move). Fail-closed: a missing or corrupt
/// file yields an EMPTY allowlist (nothing trusted), never an "allow all".
/// </summary>
public sealed class PairingStore
{
    /// <summary>One paired peer: its DTLS fingerprint, a user label, and when it was paired.</summary>
    public sealed record PairingEntry(string Fingerprint, string Label, DateTimeOffset PairedAt);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly string _path;
    // Keyed by fingerprint, case-insensitive (hash-name/hex casing varies between WebRTC stacks).
    private Dictionary<string, PairingEntry> _byFingerprint = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="path">Override the store path (tests). Null ⇒ %APPDATA%\RemoteScreen\pairing.json.</param>
    public PairingStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        // Ensure the containing directory exists for atomic saves (DefaultPath already creates it,
        // but a caller-supplied path may point at a not-yet-existing directory).
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Load();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteScreen");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pairing.json");
    }

    private static string Normalize(string fingerprint) => fingerprint.Trim();

    /// <summary>True IFF the fingerprint is a known paired peer. Null/empty never matches.</summary>
    public bool Contains(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        lock (_lock) return _byFingerprint.ContainsKey(Normalize(fingerprint));
    }

    /// <summary>Add (or refresh) a paired peer and persist. No-op arg validation fails closed.</summary>
    public void Add(string fingerprint, string label)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("fingerprint required", nameof(fingerprint));

        var key = Normalize(fingerprint);
        lock (_lock)
        {
            _byFingerprint[key] = new PairingEntry(key, label ?? string.Empty, DateTimeOffset.UtcNow);
            Save();
        }
    }

    /// <summary>Remove a paired peer (unpair) and persist. Returns true if it existed.</summary>
    public bool Remove(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        var key = Normalize(fingerprint);
        lock (_lock)
        {
            if (!_byFingerprint.Remove(key)) return false;
            Save();
            return true;
        }
    }

    /// <summary>Snapshot of all paired peers (for a device-management/unpair UI).</summary>
    public IReadOnlyList<PairingEntry> List()
    {
        lock (_lock) return _byFingerprint.Values.ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<PairingEntry>>(json, JsonOptions);
            if (entries == null) return;
            _byFingerprint = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Fingerprint))
                .ToDictionary(e => Normalize(e.Fingerprint), e => e, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Corrupt/unreadable store ⇒ start empty (fail-closed: nothing trusted). Never throw.
            _byFingerprint = new Dictionary<string, PairingEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Caller holds _lock.
    private void Save()
    {
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(_byFingerprint.Values.ToList(), JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true); // atomic replace on the same volume
    }
}
