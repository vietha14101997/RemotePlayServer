# Phase 1: Core Internet Infrastructure

**Parent:** [plan.md](plan.md) | **Dependencies:** None | **Priority:** High

## Overview
- **Date:** 2026-03-09
- **Description:** Create InternetManager that handles public IP detection, UPnP port mapping, and Windows Firewall rules. This is the foundation layer -- all other phases depend on it.
- **Implementation Status:** Planned
- **Review Status:** Pending

## Key Insights
- Open.NAT 2.1.0 supports both UPnP and NAT-PMP with zero dependencies
- Public IP detection uses 3-level fallback: UPnP > STUN > HTTP API
- CGN (Carrier-Grade NAT) detectable by comparing UPnP external IP vs STUN public IP
- Windows Firewall requires admin elevation; use `netsh` for simplicity (no COM interop)
- Port mappings should be cleaned up on shutdown to avoid router rule accumulation

## Requirements
1. Detect public IP address reliably
2. Create UPnP port mappings for signaling (TCP 8288) and media (UDP range)
3. Detect and warn about double NAT / CGN scenarios
4. Add Windows Firewall inbound rules for mapped ports
5. Clean up all mappings/rules on shutdown
6. Graceful degradation when UPnP unavailable

## Architecture

```
InternetManager (singleton)
  ├─ DiscoverPublicIpAsync()     → string (IP)
  ├─ SetupPortMappingsAsync()    → bool (success)
  ├─ DetectDoubleNatAsync()      → bool (is double NAT)
  ├─ AddFirewallRulesAsync()     → void
  ├─ CleanupAsync()              → void (shutdown)
  └─ Properties:
       PublicIp, IsReady, UPnPAvailable, IsDoubleNat
```

## Related Code Files
- `RemotePlayServer.csproj` (add Open.NAT package)
- `Infrastructure/Network/NetUtil.cs` (existing IP utilities)
- `Program.cs:320-342` (shutdown sequence -- add cleanup call)

## Implementation Steps

### Step 1: Add Open.NAT NuGet Package
In `RemotePlayServer.csproj`, add:
```xml
<PackageReference Include="Open.Nat" Version="2.1.0" />
```

### Step 2: Create InternetConfig Model
**New file:** `Core/Models/InternetConfig.cs`

```csharp
namespace RemotePlayServer.Core.Models;

public class InternetConfig
{
    public bool Enabled { get; set; } = false;
    public bool UpnpEnabled { get; set; } = true;
    public int SignalPort { get; set; } = 8288;
    public int UdpPortRangeStart { get; set; } = 49152;
    public int UdpPortRangeEnd { get; set; } = 49252;
    public string? TurnServerUrl { get; set; }
    public string? TurnUsername { get; set; }
    public string? TurnPassword { get; set; }
}
```

### Step 3: Create internet-settings.json
**New file:** `Configuration/internet-settings.json`

```json
{
  "enabled": false,
  "upnpEnabled": true,
  "signalPort": 8288,
  "udpPortRangeStart": 49152,
  "udpPortRangeEnd": 49252,
  "turnServerUrl": null,
  "turnUsername": null,
  "turnPassword": null
}
```

### Step 4: Create InternetManager
**New file:** `Infrastructure/Network/InternetManager.cs`

Key methods:
1. `LoadConfigAsync()` -- read `internet-settings.json`, create default if missing
2. `DiscoverPublicIpAsync()` -- UPnP > STUN > HTTP fallback chain
3. `SetupPortMappingsAsync()` -- TCP signal port + UDP range via Open.NAT
4. `DetectDoubleNatAsync()` -- compare UPnP external IP vs STUN/HTTP public IP
5. `AddFirewallRules()` -- `netsh advfirewall` for inbound TCP+UDP
6. `CleanupAsync()` -- delete UPnP mappings + firewall rules

Public IP detection via STUN:
```csharp
// Minimal STUN Binding Request (20 bytes)
// Type=0x0001, Length=0, Magic=0x2112A442, TransactionId=random 12 bytes
// Parse XOR-MAPPED-ADDRESS from response
```

HTTP fallback:
```csharp
var urls = new[] { "https://api.ipify.org/", "https://icanhazip.com/" };
```

Double NAT detection:
```csharp
// If UPnP external IP is also private (10.x, 172.16-31.x, 192.168.x)
// or differs from STUN-detected IP → double NAT / CGN
```

Firewall via netsh (no admin COM dependency):
```csharp
Process.Start("netsh", $"advfirewall firewall add rule name=\"RemotePlayServer-Internet\" " +
    $"dir=in action=allow protocol=tcp localport={port}");
```

### Step 5: Config Loading Helper
Add JSON deserialization with `System.Text.Json` (already used in project). Load from app directory, create default file if missing.

## Todo
- [ ] Add Open.NAT to .csproj
- [ ] Create InternetConfig model
- [ ] Create internet-settings.json template
- [ ] Implement InternetManager with all methods
- [ ] Unit test: public IP detection fallback chain
- [ ] Unit test: double NAT detection logic
- [ ] Integration test: UPnP on real router (manual)

## Success Criteria
- Public IP detected via at least one method on any network
- UPnP port mappings created and deleted correctly
- Double NAT detected and user warned in console
- Firewall rules added/removed without crash (graceful if no admin)
- Clean shutdown with no leftover UPnP mappings

## Risk Assessment
| Risk | Impact | Mitigation |
|------|--------|------------|
| UPnP disabled on router | High | Fallback to manual port forward instructions |
| CGN/double NAT | High | Detect and display clear warning; suggest TURN |
| Admin required for firewall | Medium | Try without admin; log warning if denied |
| STUN server unreachable | Low | HTTP API fallback; multiple providers |

## Security Considerations
- Never expose private network topology in logs shared externally
- UPnP mappings are scoped to this app's description string for cleanup
- Firewall rules use specific port ranges, not "allow all"
- Config file may contain TURN credentials -- warn user not to share

## Next Steps
After Phase 1: Phase 2 uses InternetManager's config to build ICE server list for WebRTC.
