#nullable enable
using System;

namespace RemotePlayServer.Application.Protocol
{
    /// <summary>
    /// Pure SDP-parsing helpers for DTLS fingerprint extraction/comparison.
    /// Used by the ICE-restart MITM guard (Phase 5, F11): a mid-session
    /// <c>ice_restart_offer</c> must carry the SAME DTLS fingerprint as the offer that
    /// established the current live session, otherwise it is rejected as a possible
    /// peer-swap/hijack attempt.
    ///
    /// Deliberately has NO dependency on SIPSorcery/WPF/native platform code so it can be
    /// sanity-compiled and unit-tested cross-platform (see RemotePlayServer.Tests and the
    /// throwaway macOS spike used to verify this file before Windows-VM verification).
    /// </summary>
    public static class SdpFingerprintExtractor
    {
        private const string FingerprintPrefix = "a=fingerprint:";

        /// <summary>
        /// Extract the value of the first "a=fingerprint:" line from an SDP blob
        /// (e.g. "sha-256 AB:CD:EF:...:12"). Returns null if absent, or the SDP is
        /// null/empty/whitespace-only.
        /// </summary>
        public static string? ExtractDtlsFingerprint(string? sdp)
        {
            if (string.IsNullOrEmpty(sdp)) return null;

            foreach (var rawLine in sdp.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.StartsWith(FingerprintPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring(FingerprintPrefix.Length).Trim();
                    return value.Length > 0 ? value : null;
                }
            }

            return null;
        }

        /// <summary>
        /// Compare two DTLS fingerprint values for equality. Case-insensitive because the
        /// hash-name/hex-digit casing can vary slightly between WebRTC stacks (e.g.
        /// "SHA-256" vs "sha-256", or hex digit case); whitespace-insensitive after trim.
        /// A null/empty value on EITHER side never matches — an absent fingerprint must
        /// never be treated as "trusted by default".
        /// </summary>
        public static bool FingerprintsMatch(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
