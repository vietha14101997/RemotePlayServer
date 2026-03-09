# NAT Traversal & UPnP/NAT-PMP Research for .NET 9.0

## 1. Open.NAT NuGet Package Status

**Current Version**: 2.1.0 (latest on NuGet as of 2026-03)
**Framework Target**: .NET Framework 3.5+ (backward compatible, works with .NET 9)
**Protocols**: UPnP + NAT-PMP support
**Status**: Maintained; forked from deprecated Mono.Nat
**Dependencies**: None

### API Usage Examples

```csharp
// Discover NAT device
var discoverer = new NatDiscoverer();
var cts = new CancellationTokenSource(10000);
IUpnpNatDevice device = await discoverer.DiscoverDeviceAsync(
    PortMapper.Upnp, cts.Token);

// Create port mapping (TCP + UDP)
var mapping = new Mapping(Protocol.Tcp, 8080, 8080, "RemotePlayServer");
await device.CreatePortMapAsync(mapping);

var udpMapping = new Mapping(Protocol.Udp, 8081, 8081, "RemotePlayServer-UDP");
await device.CreatePortMapAsync(udpMapping);

// Get external IP
var externalIp = await device.GetExternalIPAsync();

// Delete on shutdown
try {
    await device.DeletePortMapAsync(mapping);
} catch (MappingException ex) when (ex.ErrorCode == 714) {
    // NoSuchEntryInArray - mapping already gone, acceptable
}
```

### Error Handling for No UPnP

```csharp
try {
    var device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, timeout);
} catch (NatDeviceNotFoundException) {
    // Fallback: use STUN for IP detection, require port forwarding manual setup
    Logger.Warn("UPnP unavailable, using STUN fallback");
    // See section 3 for STUN approach
}
```

## 2. Alternative Libraries Comparison

| Library | .NET 6+ | NAT-PMP | Status | Maintenance |
|---------|---------|---------|--------|-------------|
| **Open.NAT** | ✓ (v2.1.0) | ✓ | Active | Actively maintained |
| **Mono.Nat** | ✓ (v3.0.4) | ✓ | Deprecated | No recent updates |
| **STUN-only** | ✓ | ✗ | Light | Use for IP detection only |

**Recommendation**: Use **Open.NAT 2.1.0** as primary. It's the modern successor to Mono.Nat with better .NET support and no dependencies.

## 3. Public IP Detection Methods

### Method A: STUN (RFC 8489)
```csharp
// Using custom STUN client (Google's free server)
var stunServer = "stun.l.google.com";
var stunPort = 19302;

using (var socket = new Socket(AddressFamily.InterNetwork,
    SocketType.Dgram, ProtocolType.Udp)) {

    var request = BuildStunBindingRequest();
    await socket.SendToAsync(request, stunServer, stunPort);
    var response = new byte[512];
    var received = await socket.ReceiveAsync(response);

    // Parse response for XOR-MAPPED-ADDRESS (RFC 8489)
    var publicIp = ParseStunResponse(response);
}
```

**Pros**: Works behind any NAT, lightweight
**Cons**: Depends on external server, no mapping creation

### Method B: HTTP APIs (Fallback)
```csharp
// Fallback chain if UPnP + STUN unavailable
var ipDetectionUrls = new[] {
    "https://api.ipify.org/",           // JSON response
    "https://icanhazip.com/",           // Plain text
    "https://ifconfig.me/"              // Plain text
};

var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
foreach (var url in ipDetectionUrls) {
    try {
        var ip = await client.GetStringAsync(url);
        return ip.Trim();
    } catch { /* try next */ }
}
```

**Reliability Fallback Order**:
1. UPnP (best, fast, automatic)
2. STUN (reliable, lightweight)
3. HTTP APIs (simple, widely available)

## 4. UPnP Security & Lifetime Management

### Port Mapping Lifetime
- **Default lease time**: 0 (infinite on many routers, verify via GetSpecificPortMappingEntry)
- **Best practice**: Renew every 24-48 hours to survive reboots
- **Implementation**: Track mapping creation time, refresh periodically

```csharp
// Graceful shutdown cleanup
public async Task ShutdownMappingsAsync(IUpnpNatDevice device) {
    foreach (var mapping in _activeMappings) {
        try {
            await device.DeletePortMapAsync(mapping);
            Logger.Info($"Deleted mapping: {mapping.Description}");
        } catch (MappingException ex) when (ex.ErrorCode == 714) {
            // Already deleted, not an error
        } catch (Exception ex) {
            Logger.Error($"Failed to delete {mapping.Description}: {ex.Message}");
            // Non-fatal; mapping will expire on router reboot
        }
    }
}
```

### Security Concerns
- **No authentication**: Any internal device can modify UPnP rules
- **Mitigation**: Run on trusted networks only; disable UPnP on public WiFi
- **Double NAT (CGN)**: ISP-level NAT (100.64.0.0/10 range) defeats port mapping
  - Detection: Compare WAN IP vs public IP via STUN; if different, you're behind CGN
  - Workaround: Use relay/VPN; UPnP cannot pierce ISP-level NAT

## 5. Windows Firewall Automation

### NetFwTypeLib COM Approach (Requires Admin)

```csharp
using NetFwTypeLib;

public class FirewallManager {
    public void AllowPort(int port, string protocol = "TCP") {
        var policy = (INetFwPolicy2)new NetFwPolicy2();
        var rule = (INetFwRule)new NetFwRule();

        rule.Name = $"RemotePlayServer {protocol} {port}";
        rule.Description = "Managed by RemotePlayServer";
        rule.Protocol = (int)(protocol == "UDP" ?
            NET_FW_IP_PROTOCOL_.NET_FW_IP_PROTOCOL_UDP :
            NET_FW_IP_PROTOCOL_.NET_FW_IP_PROTOCOL_TCP);
        rule.LocalPorts = port.ToString();
        rule.Direction = NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_IN;
        rule.Action = NET_FW_ACTION_.NET_FW_ACTION_ALLOW;
        rule.Enabled = true;

        policy.Rules.Add(rule);
    }

    public void RemoveRule(string ruleName) {
        var policy = (INetFwPolicy2)new NetFwPolicy2();
        policy.Rules.Remove(ruleName);
    }
}
```

**Requirements**:
- Admin elevation required (check via `new WindowsPrincipal(identity).IsInRole(...)`)
- Add reference: `C:\Windows\System32\FirewallAPI.dll` (COM TypeLib)
- Works across .NET Framework → .NET 9

### netsh Alternative (Process-based)
```csharp
// Simpler but less controllable
var process = new Process {
    StartInfo = new ProcessStartInfo {
        FileName = "netsh",
        Arguments = "advfirewall firewall add rule name=\"RemotePlayServer\" " +
            "dir=in action=allow protocol=tcp localport=8080",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    }
};
process.Start();
await process.WaitForExitAsync();
```

## Unresolved Questions

1. Does Open.NAT 2.1.0 handle IPv6 port mappings (UPnPv6)?
2. Recommended lease renewal interval for maximum router compatibility?
3. Are there .NET 9-specific optimizations leveraging ValueTask in Open.NAT?
4. Does Mono.Nat v3.0.4 still receive security patches?

---

**Sources**:
- [Open.NAT GitHub](https://github.com/lontivero/Open.NAT)
- [NuGet: Open.Nat 2.1.0](https://www.nuget.org/packages/Open.Nat)
- [CodeProject: NAT Traversal with UPnP in C#](https://www.codeproject.com/Articles/27992/NAT-Traversal-with-UPnP-in-C)
- [RFC 8489: STUN Protocol](https://voip-sip-sdk.com/p_7194-rfc-3489-stun-simple-traversal-of-udp-through-nats.html)
- [Cloudflare: Detecting CGN](https://blog.cloudflare.com/detecting-cgn-to-reduce-collateral-damage/)
