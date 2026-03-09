# WebRTC Internet Streaming & TURN Integration Research
**Date:** 2026-03-09 | **Status:** Final

---

## 1. SIPSorcery TURN Configuration

### RTCConfiguration Setup
SIPSorcery's `RTCConfiguration` accepts an `iceServers` list of `RTCIceServer` objects:

```csharp
var iceServers = new List<RTCIceServer>
{
    new RTCIceServer { urls = "stun:stun.sipsorcery.com" },
    new RTCIceServer
    {
        urls = "turn:turn.example.com:3478",
        username = "username1",
        credential = "password1"
    }
};

var config = new RTCConfiguration { iceServers = iceServers };
var peerConn = new RTCPeerConnection(config);
```

### ICE Candidate Gathering
- TURN relays active automatically when direct peer connection fails
- STUN + TURN redundancy: combine both for fallback
- Candidates emitted via `onicecandidate` event

---

## 2. Free/Self-Hosted TURN Options

| Service | Credentials Format | Cost | Notes |
|---------|-------------------|------|-------|
| **Coturn (self-hosted)** | `user=username:password` in config | Free OSS | Most popular, full control |
| **Metered.ca** | REST API: API_KEY + generated username/password | Free: 500MB–20GB/month | No credit card, standard.relay.metered.ca |
| **Cloudflare TURN** | OAuth2 via Cloudflare account | Free with Realtime SFU; $0.05/GB otherwise | Global anycast, 330 cities, 95% within 50ms |

### Coturn Setup (Docker Example)
```bash
docker run -d --name coturn coturn/coturn:latest
# Config: /etc/coturn/turnserver.conf
# user=testuser:testpass
# realm=example.com
```

### Metered Integration
```csharp
// REST API endpoint: https://api.metered.ca/api/v1/turn/credential
// Returns: { username, password, urls: ["turn:standard.relay.metered.ca:..."] }
```

---

## 3. Internet Streaming Optimization

### Bitrate Adaptation
- **WebRTC ABR:** Automatic, no buffering required
- Adapts in real-time via REMB (Receiver Estimated Max Bitrate)
- Typical range: 500 Kbps (low) → 5 Mbps (HD)
- Codec recommendation: VP9 (scalable), VP8, H.264 (fallback)

### Jitter Buffer Management
- **NetEQ** (audio): Adaptive, default 40ms, grows on poor connection
- **Video:** Max 50 packets (configurable via `setJitterBufferMaxPackets()`)
- Opus codec uses PLC (packet loss concealment) < 120ms loss
- Internet: expect 20–100ms network latency variance

### Connection Quality Monitoring
```csharp
// RTCPeerConnection stats:
peerConn.getStats().then(report => {
    report.forEach(stats => {
        if (stats.type == "inbound-rtp") {
            var latency = stats.jitter; // milliseconds
            var packetLoss = stats.packetsLost / stats.packetsReceived;
            // Adjust stream params if loss > 2% or latency > 80ms
        }
    });
});
```

### Typical Internet Latency
- **Good:** < 50ms
- **Acceptable:** 50–100ms
- **Poor:** > 100ms (may trigger bitrate reduction)

---

## 4. Security for Internet-Exposed Streaming

### DTLS-SRTP Encryption
- **Mandatory in WebRTC**: DTLS key exchange → SRTP media encryption
- All media encrypted per RFC 8827
- Key exchange happens pre-media; transparent to application

### Token-Based WebSocket Signaling
```csharp
// JWT approach: client receives token from auth server
var token = GenerateJWT(userId, expiresIn: TimeSpan.FromMinutes(15));

// WebSocket handshake includes token as query param (browsers can't set headers)
var wsUri = $"wss://signaling.example.com?token={token}";

// Server-side validation:
// Extract token from query, validate signature, check expiration
// Reject if invalid → close WebSocket
```

### TURN Server Security (Time-Limited Credentials)
```csharp
// REST API generates short-lived credentials
// Backend calls: GET /api/turn-credential?user=clientId
// Response: { username, password, expiry }
// Credentials valid only for call duration, auto-revoke after expiry
```

### Best Practices
1. **WSS (WebSocket Secure):** Always encrypt signaling with TLS
2. **Token expiry:** 15–30 min per call
3. **PIN/Token auth:** Simple 4-digit PIN stored server-side, client sends via JWT
4. **TURN credential rotation:** Generate fresh credentials per peer connection

---

## 5. Connection Mode Detection (LAN vs Internet)

### Subnet Comparison Approach
```csharp
public bool IsOnSameLAN(string ip1, string ip2, string subnetMask)
{
    // Parse IPs and subnet mask
    var addr1 = IPAddress.Parse(ip1);
    var addr2 = IPAddress.Parse(ip2);
    var mask = IPAddress.Parse(subnetMask);

    // Bitwise AND: both IPs & mask must equal
    var net1 = BitwiseAnd(addr1, mask);
    var net2 = BitwiseAnd(addr2, mask);

    return net1.Equals(net2); // True = LAN, False = Internet
}

private IPAddress BitwiseAnd(IPAddress ip, IPAddress mask)
{
    var ipBytes = ip.GetAddressBytes();
    var maskBytes = mask.GetAddressBytes();
    var result = new byte[ipBytes.Length];

    for (int i = 0; i < ipBytes.Length; i++)
        result[i] = (byte)(ipBytes[i] & maskBytes[i]);

    return new IPAddress(result);
}
```

### Streaming Parameter Implications
| Mode | Bitrate | RTX | Jitter Buffer | TURN |
|------|---------|-----|---------------|------|
| **LAN** | Full (10+ Mbps) | Aggressive | 15ms | Optional |
| **Internet** | ABR (500K–5M) | Conservative | 40–80ms | Required |

---

## Summary

**Immediate Actions:**
1. Use `RTCConfiguration.iceServers` with TURN array for internet mode
2. Coturn (self-hosted) or Metered.ca (free tier) for production
3. Monitor RTCStats (jitter, packet loss) to trigger adaptive codec switch
4. WSS + JWT tokens for signaling; DTLS-SRTP handles media encryption
5. Detect LAN via subnet match; adjust buffer/bitrate accordingly

**Unresolved Questions:**
- Exact SIPSorcery API for dynamic codec switching on poor connection
- Metered.ca free tier sustainability (500MB vs 20GB discrepancy)
- Cloudflare TURN auth method for non-enterprise accounts

---

## Sources

- [SIPSorcery RTCConfiguration API](https://sipsorcery-org.github.io/sipsorcery/api/SIPSorcery.Net.RTCConfiguration.html)
- [Coturn Configuration Guide](https://www.metered.ca/blog/coturn/)
- [WebRTC Security Guide: DTLS & SRTP](https://antmedia.io/webrtc-security/)
- [WebRTC Jitter Buffer & NetEQ](https://webrtchacks.com/how-webrtcs-neteq-jitter-buffer-provides-smooth-audio/)
- [WebRTC Bitrate Adaptation](https://getstream.io/resources/projects/webrtc/advanced/bitrates-traffic/)
- [Metered TURN Server Free Tier](https://www.metered.ca/tools/openrelay/)
- [Cloudflare Realtime TURN Service](https://developers.cloudflare.com/realtime/turn/)
- [C# WebSocket JWT Authentication](https://medium.com/@rizwan3d/websocket-for-real-time-communication-in-c-and-typescript-part-2-authentication-b719981ba14f)
- [Subnet Detection for LAN/Internet](https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/tcpip-addressing-and-subnetting)
