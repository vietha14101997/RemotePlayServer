# Internet Remote Mode - Implementation Plan

**Date:** 2026-03-09 | **Status:** Planning | **Priority:** High

## Goal
Enable RemotePlayServer to accept connections from the internet (beyond LAN/USB), using UPnP port mapping + optional TURN relay, with token-based authentication. Fully backward compatible -- LAN/USB modes unchanged.

## Research
- [NAT Traversal & UPnP](research/researcher-01-nat-traversal.md)
- [WebRTC TURN & Security](research/researcher-02-webrtc-internet.md)

## Phase Summary

| # | Phase | Status | File |
|---|-------|--------|------|
| 1 | Core Internet Infrastructure (NAT/UPnP/Firewall) | Planned | [phase-01](phase-01-internet-infrastructure.md) |
| 2 | WebRTC TURN Integration (Centralize ICE config) | Planned | [phase-02](phase-02-webrtc-turn-integration.md) |
| 3 | Connection Detection & Streaming Optimization | Planned | [phase-03](phase-03-connection-optimization.md) |
| 4 | Server Startup & UI Integration | Planned | [phase-04](phase-04-server-startup-ui.md) |
| 5 | Security & Authentication | Planned | [phase-05](phase-05-security-auth.md) |

## Dependency Graph
```
Phase 1 (Infrastructure) ──┐
Phase 5 (Auth Token)     ──┼──> Phase 4 (Startup Integration)
Phase 2 (TURN/ICE)       ──┘
Phase 3 (Optimization)   ──────> Phase 4
```
Phase 1, 2, 3, 5 can be developed in parallel. Phase 4 integrates everything.

## Key Decisions
1. **Open.NAT** over Mono.NAT -- actively maintained, zero deps, UPnP+NAT-PMP
2. **6-char alphanumeric token** -- simple, no JWT overhead, regenerated each startup
3. **UDP port range 49152-49252** -- 100 dynamic ports for WebRTC media via UPnP
4. **Fallback chain**: UPnP auto-map > manual port forward instructions > TURN relay
5. **Internet mode OFF by default** -- explicit opt-in via `internet-settings.json`

## New Dependencies
- `Open.NAT 2.1.0` (NuGet) -- only new package

## Files Changed (Summary)
- **New**: `InternetManager.cs`, `InternetConfig.cs`, `AuthTokenManager.cs`, `internet-settings.json`
- **Modified**: `SIPSorceryStreamer.Setup.cs`, `SpeedTest.cs`, `NetUtil.cs`, `SignalServer.cs`, `Program.cs`, `.csproj`
