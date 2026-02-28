# Audio-Video Synchronization Fix

## Problem Statement

Two sync failures in the RemotePlayServer WebRTC streaming pipeline:

1. **Audio lags behind video** -- Video RTP timestamps derive from encoder PTS (encoding-completion time), not capture time. Audio uses fixed 480-sample increments with no shared reference clock. WASAPI loopback adds 10-30ms inherent latency vs DXGI's ~1-8ms.
2. **Multi-video-track desync** -- Each track initializes `RtpTimestamp = (uint)(Environment.TickCount & 0xFFFF)` independently, producing different bases. Encoder PTS is per-encoder (frame counter), not from the shared barrier timestamp.

## Root Causes (Validated)

| Symptom | Root Cause | File | Line(s) |
|---------|-----------|------|---------|
| Video RTP ignores capture time | `PhaseProtocolHandler` receives `timestamp` from capture events but calls `PushTexture`/`PushBgraTexture` WITHOUT passing it | `PhaseProtocolHandler.cs` | 1798, 1830 |
| Encoder PTS != capture time | `CalculateRtpStep` converts encoder `pts100ns` to RTP step. Encoder PTS is frame-counter or encode-completion, not wallclock | `SIPSorceryStreamer.cs` | 888-913 |
| Multi-track different bases | Each track gets `(Environment.TickCount & 0xFFFF)` on first encoded frame | `SIPSorceryStreamer.cs` | 898 |
| Audio-video no shared clock | Audio pipeline: fixed 480 RTP increment per 10ms Opus frame, completely independent of video timestamps | `SIPSorceryStreamer.cs` | 460-468 |

## Solution: Shared Reference Clock + Capture-Time RTP

### Phase 1: Capture Timestamp Passthrough
Pass the barrier's `captureTimestamp` (already emitted by `PerMonitorCapture`) through `PhaseProtocolHandler` into `PushTexture`/`PushBgraTexture`. No encoder interface changes needed.

**File**: `phase-01-capture-timestamp-passthrough.md`

### Phase 2: Unified RTP Clock for All Video Tracks
Replace encoder-PTS-based `CalculateRtpStep` with capture-timestamp-based calculation. All tracks share a single `_streamStartMs` reference, producing identical RTP for simultaneously-captured frames.

**File**: `phase-02-unified-rtp-clock.md`

### Phase 3: Audio-Video Clock Alignment
Add wallclock timestamps to audio pipeline. Derive audio RTP from `(audioTimestamp - _streamStartMs)` at 48kHz clock rate, sharing the same reference as video.

**File**: `phase-03-audio-video-alignment.md`

## Implementation Order

Phases are incremental; each builds on the prior:
```
Phase 1 (plumbing) -> Phase 2 (video sync) -> Phase 3 (A/V sync)
```

## Files Modified

| File | Phase | Change |
|------|-------|--------|
| `Application/Protocol/PhaseProtocolHandler.cs` | 1 | Pass `timestamp` param to Push methods |
| `Application/Streaming/SIPSorceryStreamer.cs` | 1,2,3 | Add timestamp params, new RTP calc, shared clock |
| `Infrastructure/Capture/DesktopAudioCapture.cs` | 3 | Add `long timestampMs` to `OnAudioData` event |
| `Infrastructure/Encoding/OpusAudioEncoder.cs` | 3 | Pass timestamp through `EncodePcm` -> `OnEncodedAudio` |

## Risk Assessment

- **Low**: Phase 1 is pure plumbing (add parameter, wire it). Zero behavioral change until Phase 2.
- **Medium**: Phase 2 changes RTP step calculation. Regression = video stuttering. Mitigate by keeping fallback logic for encoder-PTS when `captureTimestampMs <= 0`.
- **Medium**: Phase 3 changes audio RTP from fixed-increment to wallclock-derived. Regression = audio crackling/gaps. Mitigate by clamping step to `[1, 960]` range (0-20ms).
- **Low**: No interface changes to `IVideoEncoder`/`IBgraEncoder`/`ITextureEncoder`. Encoder implementations untouched.

## Success Criteria

1. Audio and video arrive within 30ms of each other on the client
2. Multiple video tracks (monitors) display the same content at the same wall-clock moment
3. No audio crackling, gaps, or timestamp jumps after silence periods
4. No regression in single-monitor streaming quality
