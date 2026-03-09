# Phase 3: Connection Detection & Streaming Optimization

**Parent:** [plan.md](plan.md) | **Dependencies:** Phase 1 (InternetManager) | **Priority:** Medium

## Overview
- **Date:** 2026-03-09
- **Description:** Add LAN/Internet client detection via subnet comparison. Create internet-specific streaming profile in StreamingOptimizer with conservative bitrate/FPS defaults. Existing LAN/WiFi profiles unchanged.
- **Implementation Status:** Planned
- **Review Status:** Pending

## Key Insights
- `SpeedTest.ClassifyConnection()` already returns "Internet" for high-ping/low-bandwidth connections (line 275-282)
- `SuggestedConfigMessage.ConnectionType` already supports "Internet" string (line 224-225)
- StreamingOptimizer has no internet-specific profile -- defaults to minimum 15Mbps which is too high for most internet connections
- Subnet comparison is the most reliable LAN detection method (vs ping heuristics alone)

## Requirements
1. `IsClientOnLAN(IPAddress clientIp)` method in NetUtil
2. Internet streaming profile: 3-8 Mbps base, 30fps default, higher jitter tolerance
3. StreamingOptimizer handles "Internet" connection type explicitly
4. Existing LAN/WiFi behavior unchanged

## Related Code Files
- `Infrastructure/Network/NetUtil.cs` -- add `IsClientOnLAN()`
- `Infrastructure/Network/SpeedTest.cs:275-282` -- `ClassifyConnection()` (already handles Internet)
- `Infrastructure/Network/SpeedTest.cs:296-375` -- `StreamingOptimizer.CalculateSuggestedConfig()`
- `Application/Protocol/PhaseProtocolHandler.Phase1.cs:70-77` -- speed test result handling
- `Core/Models/ProtocolMessages.cs:224-225` -- ConnectionType field

## Implementation Steps

### Step 1: Add IsClientOnLAN to NetUtil
**Modify:** `Infrastructure/Network/NetUtil.cs`

```csharp
/// <summary>
/// Check if client IP is on the same subnet as any local interface.
/// Used to distinguish LAN clients from internet clients.
/// </summary>
public static bool IsClientOnLAN(IPAddress clientIp)
{
    // Private IP ranges that indicate LAN
    var clientBytes = clientIp.GetAddressBytes();
    if (clientIp.AddressFamily != AddressFamily.InterNetwork)
        return false; // IPv6 not supported yet

    // If client IP is not private, it's definitely internet
    if (!IsPrivateIp(clientIp))
        return false;

    // Compare against all local interfaces' subnets
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        var ipProps = ni.GetIPProperties();
        foreach (var ua in ipProps.UnicastAddresses)
        {
            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(ua.Address)) continue;

            // Same subnet check
            var maskBytes = ua.IPv4Mask.GetAddressBytes();
            var localBytes = ua.Address.GetAddressBytes();
            bool sameSubnet = true;
            for (int i = 0; i < 4; i++)
            {
                if ((clientBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i]))
                { sameSubnet = false; break; }
            }
            if (sameSubnet) return true;
        }
    }
    return false;
}

private static bool IsPrivateIp(IPAddress ip)
{
    var bytes = ip.GetAddressBytes();
    return bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168);
}
```

### Step 2: Add Internet Profile to StreamingOptimizer
**Modify:** `Infrastructure/Network/SpeedTest.cs` -- inside `CalculateSuggestedConfig()`

After the existing LAN override block (line ~344-347), add internet handling:

```csharp
// Internet profile: conservative defaults for higher latency connections
string connType = ClassifyConnection(network.PingMs, network.BandwidthMbps);
if (connType == "Internet")
{
    // Internet: cap bitrate to available bandwidth, lower minimum
    int internetBase = 5000; // 5 Mbps base for internet
    double maxForInternet = Math.Min(availableBandwidth * 1000 * 0.6, 10000); // 60% BW, max 10Mbps
    rawBitrate = (int)Math.Clamp(maxForInternet, internetBase, 10000);

    // FPS: prefer 30fps for stability on internet
    if (network.PingMs > 50)
    {
        config.Fps = 30;
        config.RefreshRate = 60;
    }
}
```

Update `RoundToNearestBitrateOption` to include lower options for internet:
```csharp
// Add 5000 and 10000 to dropdown options: [5000, 10000, 15000, 20000, 25000, 30000, 40000]
```

### Step 3: Pass Connection Type to StreamingOptimizer
The connection type from `ClassifyConnection()` is already computed. Ensure it flows into `SuggestedConfigMessage.ConnectionType`. Check `PhaseProtocolHandler.Phase1.cs` to verify the mapping.

## Todo
- [ ] Add `IsClientOnLAN()` and `IsPrivateIp()` to NetUtil.cs
- [ ] Add internet streaming profile to StreamingOptimizer
- [ ] Add 5000/10000 Kbps to bitrate dropdown options
- [ ] Verify ClassifyConnection result flows to ConnectionType field
- [ ] Test: LAN client gets existing high-bitrate profile
- [ ] Test: Internet client gets conservative profile

## Success Criteria
- LAN clients: identical behavior to current (40Mbps max, 60fps)
- Internet clients: 3-10 Mbps, 30fps default, stable streaming
- `IsClientOnLAN()` correctly identifies same-subnet clients
- No regression in WiFi connection profile

## Risk Assessment
| Risk | Impact | Mitigation |
|------|--------|------------|
| False negative on LAN detection (VPN client) | Low | Speed test heuristic still works as backup |
| Internet bitrate too low for text readability | Medium | User can manually increase in client UI |
| IPv6 client not handled | Low | Return false (treat as internet) -- safe default |

## Security Considerations
- Client IP from WebSocket connection is used for LAN detection -- cannot be spoofed over TCP
- No sensitive data exposed in connection type classification

## Next Steps
Phase 4 integrates this with server startup to display internet connection info.
