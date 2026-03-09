# Phase 2: WebRTC TURN Integration

**Parent:** [plan.md](plan.md) | **Dependencies:** Phase 1 (InternetConfig) | **Priority:** High

## Overview
- **Date:** 2026-03-09
- **Description:** Centralize RTCConfiguration creation across all 4 PeerConnection locations. Add TURN server support when internet mode is active. Eliminates DRY violation of hardcoded STUN configs.
- **Implementation Status:** Planned
- **Review Status:** Pending

## Key Insights
- 4 separate locations create RTCPeerConnection with identical hardcoded STUN config
- SIPSorcery's `RTCIceServer` supports `urls`, `username`, `credential` fields
- TURN is user-provided (optional) -- no external service dependency
- STUN remains as baseline; TURN added only when configured

## Requirements
1. Single `GetIceConfiguration()` method returning `RTCConfiguration`
2. All 4 PeerConnection creation sites use this method
3. TURN servers included when internet mode enabled and configured
4. Backward compatible -- LAN mode unchanged (STUN only)

## Related Code Files (with line numbers)
- `Application/Streaming/SIPSorceryStreamer.Setup.cs:51-58` -- Main PC creation
- `Application/Streaming/SIPSorceryStreamer.Setup.cs:380-386` -- SetupVideoPeerConnections (already receives `RTCConfiguration cfg` param)
- `Application/Streaming/SIPSorceryStreamer.Setup.cs:509-516` -- Reconnect video PC (hardcoded inline)
- `Application/Streaming/SIPSorceryStreamer.Setup.cs:633-640` -- Audio PC creation
- `Application/Streaming/SIPSorceryStreamer.cs:1-50` -- Class definition

## Implementation Steps

### Step 1: Add ICE Configuration Factory Method
Add to `SIPSorceryStreamer.cs` (or a new partial file `SIPSorceryStreamer.IceConfig.cs` if Setup.cs is large):

```csharp
/// <summary>
/// Build RTCConfiguration with STUN + optional TURN servers.
/// Centralized to avoid DRY violation across 4 PC creation sites.
/// </summary>
private static RTCConfiguration BuildIceConfiguration()
{
    var servers = new List<RTCIceServer>
    {
        new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
    };

    var config = InternetManager.Instance?.Config;
    if (config is { Enabled: true, TurnServerUrl: not null })
    {
        servers.Add(new RTCIceServer
        {
            urls = config.TurnServerUrl,
            username = config.TurnUsername ?? "",
            credential = config.TurnPassword ?? ""
        });
    }

    return new RTCConfiguration { iceServers = servers };
}
```

### Step 2: Replace All 4 Hardcoded STUN Configs

**Location 1** (`Setup.cs:51-58`) -- Main PC:
```csharp
// Before:
var cfg = new RTCConfiguration { iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } } };
// After:
var cfg = BuildIceConfiguration();
```

**Location 2** (`Setup.cs:380`) -- SetupVideoPeerConnections already receives `cfg` param. Ensure caller passes `BuildIceConfiguration()`.

**Location 3** (`Setup.cs:509-516`) -- Reconnect video PC:
```csharp
// Before:
var cfg = new RTCConfiguration { iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } } };
// After:
var cfg = BuildIceConfiguration();
```

**Location 4** (`Setup.cs:633-640`) -- Audio PC:
```csharp
// Before:
var config = new RTCConfiguration { iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } } };
// After:
var config = BuildIceConfiguration();
```

### Step 3: Add Using Statement
Ensure `SIPSorceryStreamer.Setup.cs` has access to `InternetManager`:
```csharp
using RemotePlayServer.Infrastructure.Network;
```

## Todo
- [ ] Create `BuildIceConfiguration()` method in SIPSorceryStreamer
- [ ] Replace location 1 (Main PC, line 51-58)
- [ ] Replace location 2 (verify caller passes centralized cfg)
- [ ] Replace location 3 (Reconnect video PC, line 509-516)
- [ ] Replace location 4 (Audio PC, line 633-640)
- [ ] Verify TURN credentials flow through correctly
- [ ] Test: LAN mode still works (STUN only, no TURN)
- [ ] Test: Internet mode adds TURN to ICE servers

## Success Criteria
- Zero hardcoded STUN configs remain in codebase
- `BuildIceConfiguration()` is single source of truth for ICE servers
- LAN connections work identically to before (regression-free)
- TURN relay works when configured (manual test with coturn)

## Risk Assessment
| Risk | Impact | Mitigation |
|------|--------|------------|
| TURN credentials invalid | Medium | Log warning, fall back to STUN-only |
| SIPSorcery TURN candidate format | Low | Tested format: `turn:host:port` works |
| InternetManager not initialized | Low | Null-check with fallback to STUN-only |

## Security Considerations
- TURN credentials stored in `internet-settings.json` (local file, user responsibility)
- DTLS-SRTP encryption is mandatory in WebRTC -- media always encrypted regardless
- TURN relay sees encrypted traffic only (cannot decrypt SRTP)

## Next Steps
Phase 3 uses connection type detection to adjust streaming parameters for internet connections.
