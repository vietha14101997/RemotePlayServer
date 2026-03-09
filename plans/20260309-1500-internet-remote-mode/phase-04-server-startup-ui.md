# Phase 4: Server Startup & UI Integration

**Parent:** [plan.md](plan.md) | **Dependencies:** Phase 1, 2, 3, 5 | **Priority:** High

## Overview
- **Date:** 2026-03-09
- **Description:** Integrate InternetManager into Program.cs startup/shutdown sequence. Display public IP, auth token, UPnP status in console. Update QR code data with internet connection info. This is the integration phase.
- **Implementation Status:** Planned
- **Review Status:** Pending

## Key Insights
- `Program.cs:273-274` creates SignalServer on `http://+:8288/`
- `Program.cs:288-296` generates QR code with `{"ip":"...","port":"8288"}`
- `Program.cs:320-342` handles graceful shutdown -- add cleanup here
- Internet mode setup must happen after USB tethering detection but before QR code display
- Console output should clearly distinguish LAN vs Internet connection options

## Requirements
1. Load `internet-settings.json` at startup
2. If internet enabled: run InternetManager setup (public IP, UPnP, firewall)
3. Display internet connection info in console (public IP, token, UPnP status)
4. Update QR code to include public IP when internet mode active
5. Clean up UPnP mappings and firewall rules on shutdown
6. Handle setup failures gracefully (don't block LAN mode)

## Related Code Files
- `Program.cs:260-342` -- main startup and shutdown sequence
- `Infrastructure/Network/InternetManager.cs` (from Phase 1)
- `Server/AuthTokenManager.cs` (from Phase 5)

## Implementation Steps

### Step 1: Internet Mode Initialization in Program.cs
After line 282 (`await server.StartAsync()`) and before QR code generation (line 292):

```csharp
// Internet mode setup
string? publicIP = null;
string? authToken = null;
bool internetReady = false;

var internetConfig = await InternetManager.LoadConfigAsync();
if (internetConfig.Enabled)
{
    Console.WriteLine("[Internet] Internet mode enabled, setting up...");
    var im = InternetManager.Instance;

    try
    {
        publicIP = await im.DiscoverPublicIpAsync();
        Console.WriteLine($"[Internet] Public IP: {publicIP}");

        if (internetConfig.UpnpEnabled)
        {
            bool upnpOk = await im.SetupPortMappingsAsync();
            if (upnpOk)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[Internet] UPnP port mapping: OK");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[Internet] UPnP unavailable. Manual port forward required:");
                Console.WriteLine($"  Forward TCP {internetConfig.SignalPort} to this PC's LAN IP ({preferredIP})");
                Console.ResetColor();
            }

            bool doubleNat = await im.DetectDoubleNatAsync();
            if (doubleNat)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[Internet] WARNING: Double NAT / CGN detected.");
                Console.WriteLine("  Direct P2P may not work. Consider using a TURN server.");
                Console.ResetColor();
            }
        }

        im.AddFirewallRules();

        authToken = AuthTokenManager.GenerateToken();
        internetReady = true;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Internet] Setup failed: {ex.Message}");
        Console.WriteLine("[Internet] LAN/USB modes still available.");
        Console.ResetColor();
    }
}
```

### Step 2: Update QR Code Data
Modify QR code generation to include internet info when available:

```csharp
// Build QR data
string usbIPJson = usbTetheringIP != null ? $",\"usbIP\":\"{usbTetheringIP}\"" : "";
string internetJson = internetReady ? $",\"publicIP\":\"{publicIP}\",\"token\":\"{authToken}\"" : "";
string qrData = $"{{\"ip\":\"{preferredIP}\",\"port\":\"{port}\"{usbIPJson}{internetJson}}}";
```

### Step 3: Update Console Connection Options
After existing USB/WiFi display (line 314), add internet option:

```csharp
if (internetReady)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  [Internet] {publicIP}:{port} (Token: {authToken})");
    Console.ResetColor();
}
else if (internetConfig?.Enabled == true)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  [Internet] Setup failed (see errors above)");
    Console.ResetColor();
}
```

### Step 4: Shutdown Cleanup
Add to shutdown sequence (after line 326 `server.StopAsync()`):

```csharp
// Cleanup internet mode resources
if (InternetManager.Instance != null)
{
    try
    {
        Console.WriteLine("[Shutdown] Cleaning up internet mode...");
        await InternetManager.Instance.CleanupAsync();
        Console.WriteLine("[Shutdown] Internet cleanup done.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Shutdown] Internet cleanup error: {ex.Message}");
    }
}
```

## Todo
- [ ] Add internet mode initialization block after server.StartAsync()
- [ ] Update QR code data format to include publicIP + token
- [ ] Add internet connection option to console display
- [ ] Add InternetManager cleanup to shutdown sequence
- [ ] Test: startup with internet disabled (no change in behavior)
- [ ] Test: startup with internet enabled, UPnP available
- [ ] Test: startup with internet enabled, UPnP unavailable
- [ ] Test: shutdown cleans up mappings

## Success Criteria
- Server starts normally when internet mode disabled (zero behavior change)
- Public IP + token displayed in console when internet mode active
- QR code includes internet connection data
- Graceful handling of UPnP/firewall failures (LAN still works)
- Clean shutdown removes all UPnP mappings and firewall rules

## Risk Assessment
| Risk | Impact | Mitigation |
|------|--------|------------|
| Internet setup slow (UPnP discovery timeout) | Medium | 10s timeout, non-blocking for LAN |
| QR code too large with extra data | Low | JSON stays compact; QR handles ~2K chars |
| Shutdown crash leaves UPnP orphaned | Low | UPnP mappings expire on router reboot |

## Security Considerations
- Auth token displayed in console -- warn user not to screenshot/share publicly
- Public IP logged to console only (not sent to any external service)
- QR code with token should only be scanned by trusted devices

## Next Steps
This phase completes the integration. After all 5 phases, end-to-end internet streaming is functional.
