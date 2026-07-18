#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Security;

/// <summary>
/// Master switch for the Phase 1 pairing/auth security spine (pairing-protocol-contract-v1.md).
///
/// DEFAULT OFF for safe landing: until the full flow (Host + Android) is verified on real
/// hardware, every new pairing behaviour MUST be a no-op so a plain build stays byte-for-byte
/// identical to today's legacy LAN streaming path. Flip <c>requirePairing</c> to true in
/// <c>Configuration/pairing-settings.json</c> (or delete the file to regenerate the default)
/// once E2E pairing has been validated.
///
/// When ON:
///  - the Host's own PeerConnections pin a stable DTLS certificate (see HostDtlsCertificate),
///  - the QR payload carries a one-use pairing secret,
///  - sessions must complete the pairing handshake (or present an already-paired fingerprint)
///    before any media/input is allowed,
///  - the SignalServer binds loopback + the specific LAN address only (never all-interfaces),
///  - UPnP port-mapping is only requested for a paired, bound session.
///
/// Loaded once at process start (mirrors <see cref="RemotePlayServer.Infrastructure.Network.InternetManager"/>'s
/// Configuration/*.json pattern used for <c>RequireToken</c>). Fail-safe: any load error keeps the
/// flag at its safe-landing default (false) rather than throwing or defaulting to "on".
/// </summary>
public static class PairingPolicy
{
    private const string ConfigFileName = "pairing-settings.json";

    private static readonly Lazy<bool> _requirePairing = new(LoadRequirePairing);

    /// <summary>True IFF the full pairing/auth gate is active for this process run.</summary>
    public static bool RequirePairing => _requirePairing.Value;

    private sealed class PairingSettings
    {
        [JsonPropertyName("requirePairing")]
        public bool RequirePairing { get; set; } = false;
    }

    private static bool LoadRequirePairing()
    {
        var path = ConfigPath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<PairingSettings>(json);
                return cfg?.RequirePairing ?? false;
            }

            // No config yet: write the safe-landing default so operators can find and flip it.
            WriteDefaultConfig(path);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Pairing] Failed to load {ConfigFileName}, defaulting to OFF: {ex.Message}");
            return false;
        }
    }

    private static void WriteDefaultConfig(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                new PairingSettings(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            Logger.Info($"[Pairing] Created default config (requirePairing=false): {path}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Pairing] Failed to create default config: {ex.Message}");
        }
    }

    private static string ConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "Configuration", ConfigFileName);
}
