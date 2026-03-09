#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace RemotePlayServer.Server;

/// <summary>
/// Generates and validates session tokens for internet connections.
/// Token is 6-char alphanumeric, regenerated each server startup.
/// </summary>
public static class AuthTokenManager
{
    private static string? _currentToken;

    // Exclude ambiguous characters: I/O/0/1
    private const string Chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int TokenLength = 6;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(5);

    // Rate limiting: track failed attempts per IP
    private static readonly ConcurrentDictionary<string, (int count, DateTime firstAttempt)> _failedAttempts = new();

    /// <summary>
    /// Generate a new token. Called once at startup.
    /// </summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenLength);
        var chars = new char[TokenLength];
        for (int i = 0; i < TokenLength; i++)
            chars[i] = Chars[bytes[i] % Chars.Length];

        _currentToken = new string(chars);
        return _currentToken;
    }

    /// <summary>
    /// Validate token from client. Case-insensitive comparison.
    /// </summary>
    public static bool ValidateToken(string? token)
    {
        if (_currentToken == null) return false;
        return string.Equals(token?.Trim(), _currentToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if token validation is required (internet mode active).
    /// </summary>
    public static bool IsActive => _currentToken != null;

    /// <summary>
    /// Check if an IP is rate-limited due to failed auth attempts.
    /// </summary>
    public static bool IsRateLimited(IPAddress ip)
    {
        var key = ip.ToString();
        if (_failedAttempts.TryGetValue(key, out var entry))
        {
            if ((DateTime.UtcNow - entry.firstAttempt) > RateLimitWindow)
            {
                _failedAttempts.TryRemove(key, out _);
                return false;
            }
            return entry.count >= MaxFailedAttempts;
        }
        return false;
    }

    /// <summary>
    /// Record a failed authentication attempt for rate limiting.
    /// </summary>
    public static void RecordFailedAttempt(IPAddress ip)
    {
        var key = ip.ToString();
        _failedAttempts.AddOrUpdate(key,
            _ => (1, DateTime.UtcNow),
            (_, existing) => (existing.count + 1, existing.firstAttempt));
    }
}
