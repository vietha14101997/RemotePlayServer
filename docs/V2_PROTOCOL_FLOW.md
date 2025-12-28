# V2 Protocol Flow - VRWorkSpace Remote Streaming

## Overview

V2 Protocol là giao thức 3-phase connection giữa VR Client (Unity) và RemotePlayServer (.NET) cho việc streaming desktop screens qua WebRTC.

## Architecture

```
┌─────────────────────┐                    ┌─────────────────────┐
│   VR Client         │                    │   RemotePlayServer  │
│   (Unity WebRTC)    │◄──── WebSocket ───►│   (SIPSorcery)      │
│                     │                    │                     │
│ PhaseProtocolClient │                    │ PhaseProtocolHandler│
│ MultiPCStreamClient │                    │ MultiPCStreamer     │
└─────────────────────┘                    └─────────────────────┘
```

## Connection URL

```
ws://{server_ip}:8288/signal?protocol=v2
```

---

## Phase 1: Hardware Detection & Speed Test

### 1.1 WebSocket Connection

```
Client                              Server
   │                                   │
   │──── WebSocket Connect ───────────►│
   │     ?protocol=v2                  │
   │                                   │
   │◄─── hardware_info ───────────────│
   │     {device, encoder, monitors}   │
   │                                   │
   │──── hardware_info_ack ───────────►│
```

### 1.2 Client-Initiated Speed Test

```
Client                              Server
   │                                   │
   │──── speedtest_request ───────────►│
   │     {direction: "download",       │
   │      durationMs: 2000}            │
   │                                   │
   │◄─── Binary data chunks ──────────│
   │     (1MB chunks for 2 seconds)    │
   │                                   │
   │◄─── speedtest_end ───────────────│
   │     {totalBytes, durationMs}      │
   │                                   │
   │──── speedtest_result ────────────►│
   │     {bandwidthMbps, pingMs,       │
   │      jitterMs}                    │
   │                                   │
   │◄─── suggested_config ────────────│
   │     {monitors, resolution,        │
   │      bitrateKbps, fps}            │
```

### 1.3 Proceed to Phase 2

```
Client                              Server
   │                                   │
   │──── proceed ─────────────────────►│
   │     {phase: 2}                    │
```

---

## Phase 2: Display Configuration & ICE Exchange

### 2.1 Display Configuration

```
Client                              Server
   │                                   │
   │──── display_config ──────────────►│
   │     {monitors: 2,                 │
   │      resolution: {w:1920,h:1080}, │
   │      bitrateKbps: 15000,          │
   │      fps: 60}                     │
   │                                   │
   │     [Server configures displays,  │
   │      creates virtual monitors,    │
   │      initializes encoders]        │
   │                                   │
   │◄─── config_complete ─────────────│
   │     {monitors: [...]}             │
```

### 2.2 WebRTC ICE Exchange (Per Monitor)

```
Client                              Server
   │                                   │
   │ [Create PeerConnection for each monitor]
   │                                   │
   │──── offer ───────────────────────►│ (for monitor 0)
   │     {monitorIndex: 0, sdp: "..."}│
   │                                   │
   │──── candidate ───────────────────►│ (client ICE candidates)
   │     {monitorIndex: 0,             │
   │      candidate: "..."}            │
   │                                   │
   │◄─── answer ──────────────────────│ (CLEAN SDP - no candidates!)
   │     {monitorIndex: 0, sdp: "..."}│
   │                                   │
   │◄─── candidate ───────────────────│ (server ICE candidates - extracted)
   │     {monitorIndex: 0,             │
   │      candidate: "candidate:..."}  │
   │                                   │
   │ [Repeat for monitors 1, 2, ...]  │
```

### 2.3 ICE Candidate Handling (CRITICAL)

**Problem**: SIPSorcery uses Vanilla ICE mode - all ICE candidates are embedded in the SDP.
Unity WebRTC **hangs** when calling `SetRemoteDescription` with embedded candidates.

**Solution**:
1. **Server**: Extract embedded candidates from answer SDP, send clean SDP first, then send candidates separately
2. **Client**: Remove any remaining embedded candidates from answer SDP before calling `SetRemoteDescription`

```csharp
// Server: ExtractIceCandidates()
var (cleanSdp, embeddedCandidates) = ExtractIceCandidates(answerSdp);
await SendTextAsync(answerMsg with Sdp = cleanSdp);
foreach (var candidate in embeddedCandidates)
    await SendMessageAsync(new CandidateMessage { ... });

// Client: FixSdp()
if (line.StartsWith("a=candidate:"))
    continue; // Remove from SDP
```

### 2.4 Proceed to Phase 3

```
Client                              Server
   │                                   │
   │ [All PeerConnections have AnswerSet = true]
   │                                   │
   │──── proceed ─────────────────────►│
   │     {phase: 3}                    │
```

---

## Phase 3: Streaming

### 3.1 Start Streaming

```
Client                              Server
   │                                   │
   │──── start_streaming ─────────────►│
   │                                   │
   │◄─── streaming_started ───────────│
   │                                   │
   │◄═══ RTP Video Streams ═══════════│
   │     (H.264 encoded frames        │
   │      via WebRTC DataChannel)      │
```

### 3.2 Keepalive

```
Client                              Server
   │                                   │
   │──── ping ────────────────────────►│
   │◄─── pong ────────────────────────│
   │     (every 5 seconds)             │
```

---

## Message Types

### Client → Server

| Type | Phase | Description |
|------|-------|-------------|
| `hardware_info_ack` | 1 | Acknowledge hardware info received |
| `speedtest_request` | 1 | Request server to send data for download test |
| `speedtest_result` | 1 | Final speed test results |
| `proceed` | 1,2 | Proceed to next phase |
| `display_config` | 2 | User-selected display configuration |
| `offer` | 2 | WebRTC SDP offer (per monitor) |
| `candidate` | 2 | ICE candidate (per monitor) |
| `end_of_candidates` | 2 | ICE gathering complete |
| `start_streaming` | 3 | Begin video streaming |
| `stop_streaming` | 3 | Stop streaming |
| `ping` | * | Keepalive |

### Server → Client

| Type | Phase | Description |
|------|-------|-------------|
| `hardware_info` | 1 | Server hardware, encoder, monitors |
| `speedtest_end` | 1 | Download test complete marker |
| `suggested_config` | 1 | Recommended streaming config |
| `config_progress` | 2 | Display config progress updates |
| `config_complete` | 2 | Display config applied, ready for ICE |
| `answer` | 2 | WebRTC SDP answer (per monitor) |
| `candidate` | 2 | ICE candidate (per monitor) |
| `end_of_candidates` | 2 | ICE gathering complete |
| `streaming_started` | 3 | Streaming has begun |
| `error` | * | Error occurred |
| `pong` | * | Keepalive response |

---

## State Machine

### Client States (ConnectionPhase)

```
Disconnected
    │
    ▼
Connecting ──────────► Error
    │
    ▼
AwaitingHardwareInfo
    │
    ▼
SpeedTesting
    │
    ▼
AwaitingSuggestedConfig
    │
    ▼
ConfiguringSettings (User selects options)
    │
    ▼
SendingDisplayConfig
    │
    ▼
AwaitingSetupComplete
    │
    ▼
ICENegotiating
    │
    ▼
ReadyToStream
    │
    ▼
StartingStream
    │
    ▼
Streaming
```

### Server States (ConnectionPhase)

```
Phase1_HardwareDetect
    │
    ▼
Phase1_SpeedTest
    │
    ▼
Phase1_WaitingProceed
    │
    ▼
Phase2_ApplyConfig
    │
    ▼
Phase2_IceExchange
    │
    ▼
Phase3_WaitingStart
    │
    ▼
Phase3_Streaming
```

---

## Key Files

### Client (Unity)

| File | Description |
|------|-------------|
| `PhaseProtocolClient.cs` | Main V2 protocol handler |
| `MultiPCStreamClient.cs` | V1/V2 wrapper, texture handling |
| `SpeedTestClient.cs` | Client-side speed test |
| `ConnectionStateMachine.cs` | State management |
| `ClusterAutoBinder.cs` | UI ↔ Protocol bridge |

### Server (.NET)

| File | Description |
|------|-------------|
| `PhaseProtocolHandler.cs` | Main V2 protocol handler |
| `MultiPCStreamer.cs` | WebRTC peer connections |
| `ProtocolMessages.cs` | JSON message types |
| `SpeedTest.cs` | Speed test & config optimizer |
| `PerMonitorCapture.cs` | Desktop capture |

---

## Known Issues & Workarounds

### 1. SetRemoteDescription Hangs

**Symptom**: `SetRemoteDescription` never completes (IsDone stays false)

**Cause**: SIPSorcery embeds ICE candidates in SDP, Unity WebRTC can't handle them

**Fix**: Remove `a=candidate:` lines from SDP before calling `SetRemoteDescription`

### 2. Task.Delay in Unity

**Symptom**: Async operations don't progress

**Cause**: Unity's synchronization context may not handle `Task.Delay` correctly

**Fix**: Use `while (!op.IsDone) await Task.Delay(10)` pattern (same as V1)

### 3. ICE Candidate Format

**Symptom**: ICE candidates rejected

**Cause**: Format mismatch between `candidate:xxx` and `xxx`

**Fix**: Normalize format - always add `candidate:` prefix before calling `AddIceCandidate`

---

## Debugging Tips

1. **Check client log** for `SetRemoteDescription` completion:
   ```
   [PhaseProtocol] PC0 ✓ answer set OK, AnswerSet=true
   ```

2. **Check server log** for ICE candidate extraction:
   ```
   [Protocol] Extracted 4 embedded ICE candidates from answer
   ```

3. **Verify ICE connection**:
   ```
   [PhaseProtocol] PC0 ICE: Connected
   [PhaseProtocol] PC0 State: Connected
   ```

4. **Check CheckIceComplete**:
   ```
   [PhaseProtocol] CheckIceComplete: 2/2 PCs have answers
   [PhaseProtocol] All PeerConnections ready, transitioning to ReadyToStream
   ```
