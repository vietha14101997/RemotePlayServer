# Phase 5: Security & Authentication

**Parent:** [plan.md](plan.md) | **Dependencies:** None | **Priority:** High

## Overview
- **Date:** 2026-03-09
- **Description:** Token-based authentication for internet WebSocket connections. Simple 6-char alphanumeric token generated on startup, validated during WebSocket handshake. Prevents unauthorized access when server is exposed to internet.
- **Implementation Status:** Planned
- **Review Status:** Pending

## Key Insights
- WebSocket clients cannot set custom HTTP headers (browser limitation) -- token must go in query param
- `SignalServer.cs:209-211` already parses query params (`transport=usb`)
- LAN connections should NOT require token (backward compatible)
- Token is per-session (regenerated each startup), not persistent
- DTLS-SRTP handles media encryption automatically in WebRTC

## Requirements
1. Generate cryptographically random 6-char alphanumeric token on startup
2. Validate token in WebSocket handshake query param (`?token=ABC123`)
3. Skip token check for LAN clients (backward compatible)
4. Reject unauthorized internet connections with clear error
5. Display token in console and QR code data

## Related Code Files
- `Server/SignalServer.cs:203-221` -- WebSocket `/signal` endpoint with query param parsing
- `Infrastructure/Network/NetUtil.cs` -- `IsClientOnLAN()` (from Phase 3)

## Implementation Steps

### Step 1: Create AuthTokenManager
**New file:** `Server/AuthTokenManager.cs`

```csharp
using System;
using System.Security.Cryptography;

namespace RemotePlayServer.Server;

/// <summary>
/// Generates and validates session tokens for internet connections.
/// Token is 6-char alphanumeric, regenerated each server startup.
/// </summary>
public static class AuthTokenManager
{
    private static string? _currentToken;
    private const string Chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No I/O/0/1 (ambiguity)
    private const int TokenLength = 6;

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
        return string.Equals(token?.Trim(), _currentToken,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if token validation is required (internet mode active).
    /// </summary>
    public static bool IsActive => _currentToken != null;
}
```

### Step 2: Enforce Token in SignalServer
**Modify:** `Server/SignalServer.cs` around line 203-221

```csharp
if (ctx.Request.IsWebSocketRequest && path == "/signal")
{
    var remoteIp = ctx.Request.RemoteEndPoint?.Address;
    var query = ctx.Request.Url?.Query ?? "";
    var queryParams = HttpUtility.ParseQueryString(query);

    // Token authentication for internet clients
    bool isLanClient = remoteIp != null && NetUtil.IsClientOnLAN(remoteIp);
    if (!isLanClient && AuthTokenManager.IsActive)
    {
        var token = queryParams["token"];
        if (!AuthTokenManager.ValidateToken(token))
        {
            Console.WriteLine($"[Signal] Rejected unauthorized internet client: {remoteIp}");
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            continue;
        }
    }

    var wsCtx = await ctx.AcceptWebSocketAsync(null);
    // ... rest of existing code
}
```

### Step 3: Add Rate Limiting (Optional, Recommended)
Simple in-memory rate limiter to prevent token brute-force:

```csharp
// Track failed attempts per IP
private static readonly ConcurrentDictionary<string, (int count, DateTime firstAttempt)> _failedAttempts = new();

private static bool IsRateLimited(IPAddress ip)
{
    var key = ip.ToString();
    if (_failedAttempts.TryGetValue(key, out var entry))
    {
        // Reset after 5 minutes
        if ((DateTime.UtcNow - entry.firstAttempt).TotalMinutes > 5)
        {
            _failedAttempts.TryRemove(key, out _);
            return false;
        }
        return entry.count >= 5; // Block after 5 failures
    }
    return false;
}
```

## Todo
- [ ] Create AuthTokenManager with generate/validate methods
- [ ] Add token check to SignalServer WebSocket handshake
- [ ] Add rate limiting for failed attempts
- [ ] Test: LAN client connects without token (unchanged)
- [ ] Test: Internet client rejected without token
- [ ] Test: Internet client accepted with valid token
- [ ] Test: Rate limiting blocks after 5 failed attempts

## Success Criteria
- LAN clients connect without any token (100% backward compatible)
- Internet clients must provide valid token in query param
- Invalid token returns 403 and logs rejection
- Token displayed in console for user to share with trusted client
- No ambiguous characters in token (easy to read/type)

## Risk Assessment
| Risk | Impact | Mitigation |
|------|--------|------------|
| Token brute-force (6 chars = ~900M combos) | Low | Rate limiting + 28-char alphabet = sufficient |
| Token leaked via QR code photo | Medium | Token changes on restart; warn user |
| LAN detection false positive | Low | Conservative IsClientOnLAN -- public IPs never match |

## Security Considerations
- 6-char token with 28-char alphabet = 28^6 = ~481M combinations -- adequate for rate-limited scenario
- `RandomNumberGenerator` used for cryptographic randomness (not `Random`)
- Case-insensitive comparison to reduce user typing errors
- Excluded ambiguous chars (I/O/0/1) from alphabet
- Rate limiting prevents brute-force: 5 attempts per 5 minutes per IP
- Token not logged to files, only displayed in console stdout

## Next Steps
Phase 4 integrates token generation into startup and displays it in console output.
