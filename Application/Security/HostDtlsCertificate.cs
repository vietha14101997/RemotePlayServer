#nullable enable
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Security;

/// <summary>
/// Supplies the host's STABLE, persisted DTLS certificate so its WebRTC fingerprint stays constant
/// across sessions and restarts — the precondition for the client pinning the host and reconnecting
/// without re-pairing. Generated once (ECDSA P-256, self-signed), then reloaded every run.
///
/// This type is intentionally SIPSorcery-agnostic: it exposes the raw <see cref="X509Certificate2"/>
/// plus the SDP-formatted fingerprint; the transport-specific wiring (wrapping into
/// RTCConfiguration.certificates2) lives at the streamer.
///
/// Persisted as a PFX under the per-user %APPDATA%\RemoteScreen directory. HARDENING FOLLOW-UP:
/// wrap the PFX with DPAPI or store the key in the CurrentUser certificate store for stronger
/// key-at-rest protection (fingerprints themselves are public; the private key is the secret).
/// </summary>
public sealed class HostDtlsCertificate
{
    private const string Subject = "CN=RemoteScreen-Host-DTLS";
    // Local-only PFX password; the file lives in a per-user-isolated directory.
    private const string PfxPassword = "remotescreen-host-dtls";

    private static readonly Lazy<HostDtlsCertificate> _instance = new(() => new HostDtlsCertificate());
    public static HostDtlsCertificate Instance => _instance.Value;

    public X509Certificate2 Certificate { get; }

    /// <summary>SDP-formatted fingerprint, e.g. "sha-256 AA:BB:...:FF" (matches a=fingerprint).</summary>
    public string Fingerprint { get; }

    public HostDtlsCertificate(string? path = null)
    {
        Certificate = LoadOrCreate(path ?? DefaultPath());
        Fingerprint = ComputeSdpFingerprint(Certificate);
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteScreen");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "host-dtls.pfx");
    }

    private static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        const X509KeyStorageFlags flags =
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet;

        if (File.Exists(pfxPath))
        {
            try { return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), PfxPassword, flags); }
            catch { /* corrupt/unreadable — regenerate below */ }
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(Subject, ecdsa, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var created = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        var pfx = created.Export(X509ContentType.Pfx, PfxPassword);
        try
        {
            File.WriteAllBytes(pfxPath, pfx);
            ApplyOwnerOnlyAcl(pfxPath);
        }
        catch { /* best-effort persist */ }
        return X509CertificateLoader.LoadPkcs12(pfx, PfxPassword, flags);
    }

    /// <summary>
    /// Restrict the PFX (contains the private key — the actual secret; fingerprints
    /// themselves are public) to the current Windows user only, per the pairing contract's
    /// "owner-only ACL" requirement. %APPDATA% is already per-user isolated, but this is
    /// defense-in-depth against e.g. a shared/roaming profile or a misconfigured ACL further
    /// up the directory tree. Best-effort: never blocks cert creation if the ACL API fails
    /// (e.g. non-NTFS volume) — the caller already has a usable cert either way.
    /// </summary>
    private static void ApplyOwnerOnlyAcl(string pfxPath)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null) return;

            var fileInfo = new FileInfo(pfxPath);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop inherited rules
            security.SetOwner(currentUser);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[HostDtlsCertificate] Failed to apply owner-only ACL to PFX (best-effort, non-fatal): {ex.Message}");
        }
    }

    private static string ComputeSdpFingerprint(X509Certificate2 cert)
    {
        var hash = cert.GetCertHash(HashAlgorithmName.SHA256);
        var sb = new StringBuilder("sha-256 ");
        for (int i = 0; i < hash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hash[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
