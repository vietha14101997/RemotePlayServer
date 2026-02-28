# Phase 2: Unified RTP Clock for All Video Tracks

## Context Links
- Parent: [plan.md](./plan.md)
- Depends on: [phase-01-capture-timestamp-passthrough.md](./phase-01-capture-timestamp-passthrough.md)
- Next: [phase-03-audio-video-alignment.md](./phase-03-audio-video-alignment.md)

## Overview

Replace the encoder-PTS-based `CalculateRtpStep` with a capture-timestamp-based calculation. Introduce a shared `_streamStartMs` reference clock so all video tracks produce RTP timestamps anchored to the same wallclock origin. This fixes multi-monitor desync AND prepares the foundation for Phase 3 audio alignment.

## Key Insights

1. **Current bug**: `CalculateRtpStep(track, pts100ns)` uses encoder PTS (`_packet->pts` from FFmpeg, or native encoder PTS). These are frame counters or encode-completion timestamps, NOT capture time. Each track's PTS sequence is independent.
2. **Current bug**: First-frame initialization sets `track.RtpTimestamp = (uint)(Environment.TickCount & 0xFFFF)` -- a different random-ish value per track since they start at different milliseconds.
3. **Fix**: Use `track.LastCaptureTimestampMs` (set in Phase 1) to derive RTP timestamp. Formula: `absoluteRtp = (captureTimestampMs - _streamStartMs) * 90000 / 1000`. The step is `absoluteRtp - track.LastAbsoluteRtp`.
4. **Multi-monitor guarantee**: Barrier-synced frames share the same `captureTimestamp` (from `PerMonitorCapture._syncedTimestamp`), so tracks encoding the same frame produce identical `absoluteRtp` values, yielding identical RTP timestamps on the wire.
5. **SIPSorcery API**: `SendVideo(uint duration, byte[] data)` adds `duration` to its internal RTP timestamp counter. We control sync by controlling the step value.

## Requirements

- All video tracks derive RTP step from capture wallclock, not encoder PTS
- Tracks encoding the same barrier-synced frame produce the same RTP timestamp
- First frame step is 0 (or a small constant) rather than a random `Environment.TickCount` fragment
- Graceful fallback when `captureTimestampMs` is 0 (legacy callers)

## Architecture

```
                    _streamStartMs (shared, set once on first frame of any track)
                         |
  Track 0: captureTs=100  -->  elapsed=0   --> absoluteRtp=0      --> step=0
  Track 1: captureTs=100  -->  elapsed=0   --> absoluteRtp=0      --> step=0
  Track 0: captureTs=133  -->  elapsed=33  --> absoluteRtp=2970   --> step=2970
  Track 1: captureTs=133  -->  elapsed=33  --> absoluteRtp=2970   --> step=2970
```

Same capture timestamp = same RTP = perfectly synchronized multi-track playback.

## Related Code Files

| File | Lines | Role |
|------|-------|------|
| `Application/Streaming/SIPSorceryStreamer.cs` | 75-104 | `TrackInfo` class -- add `LastAbsoluteRtp` |
| `Application/Streaming/SIPSorceryStreamer.cs` | 888-913 | `CalculateRtpStep` -- rewrite |
| `Application/Streaming/SIPSorceryStreamer.cs` | 819-886 | `OnEncodedData` -- call new method |

## Implementation Steps

### Step 1: Add shared stream clock fields to SIPSorceryStreamer

Add near line 44 (field declarations):
```csharp
// Shared reference clock for all tracks (set on first video frame)
private long _streamStartMs = -1;
private readonly object _clockLock = new();
```

### Step 2: Add `LastAbsoluteRtp` to TrackInfo

Add to `TrackInfo` class (near line 92):
```csharp
public uint LastAbsoluteRtp; // Last absolute RTP value for step calculation
```

### Step 3: Rewrite CalculateRtpStep

Replace the entire method (lines 888-913):

```csharp
private uint CalculateRtpStep(TrackInfo track, long pts100ns)
{
    const int ClockRate = 90000;
    uint fallback = (uint)Math.Max(1, ClockRate / Math.Max(1, _fps));

    // Use capture timestamp if available (Phase 1 wiring)
    long captureMs = track.LastCaptureTimestampMs;
    if (captureMs <= 0)
    {
        // Fallback to encoder PTS when capture timestamp not available
        lock (track)
        {
            if (!track.TimestampInitialized)
            {
                track.LastPts100ns = pts100ns;
                track.TimestampInitialized = true;
                return fallback; // Clean initial step instead of random TickCount
            }
            long delta = pts100ns - track.LastPts100ns;
            if (delta <= 0 || delta > 5_000_000)
            {
                track.LastPts100ns = pts100ns;
                return fallback;
            }
            track.LastPts100ns = pts100ns;
            return (uint)Math.Max(1, ClockRate * delta / 10_000_000L);
        }
    }

    // Capture-timestamp-based RTP (primary path)
    lock (_clockLock)
    {
        if (_streamStartMs < 0)
            _streamStartMs = captureMs;
    }

    long elapsedMs = captureMs - _streamStartMs;
    if (elapsedMs < 0) elapsedMs = 0; // Guard against clock skew

    uint absoluteRtp = (uint)((long)ClockRate * elapsedMs / 1000L);

    lock (track)
    {
        if (!track.TimestampInitialized)
        {
            track.LastAbsoluteRtp = absoluteRtp;
            track.TimestampInitialized = true;
            // First frame: return small initial step (not zero, some decoders dislike 0)
            return Math.Max(1, absoluteRtp);
        }

        uint step = absoluteRtp - track.LastAbsoluteRtp;
        track.LastAbsoluteRtp = absoluteRtp;

        // Clamp: avoid 0-step (some decoders drop frames) and huge jumps (>2 seconds)
        if (step == 0) step = 1;
        if (step > 180000) step = fallback; // >2s gap = anomaly, use fallback

        return step;
    }
}
```

### Step 4: Reset clock state on stream restart

In the existing cleanup/restart code, reset `_streamStartMs`. Find the method that handles reconnection or stream stop.

Add to the cleanup path (wherever tracks are cleared/disposed):
```csharp
_streamStartMs = -1;
```

Search for existing reset points -- likely in `Dispose()` or a `Reset()`/`Stop()` method.

### Step 5: Verify first-frame initialization no longer uses Environment.TickCount

The old code:
```csharp
track.RtpTimestamp = (uint)(Environment.TickCount & 0xFFFF);
return track.RtpTimestamp;
```

This is entirely replaced by the new logic. `track.RtpTimestamp` field can be removed if not used elsewhere, or left as dead code for now. The fallback path uses `fallback` (fps-based step) instead of random TickCount.

## Todo List

- [x] Add `_streamStartMs` field to `SIPSorceryStreamer` (uses `Interlocked` instead of `_clockLock` — superior approach)
- [x] Add `LastAbsoluteRtp` and `CaptureClockInitialized` fields to `TrackInfo`
- [x] Implement `CalculateRtpStepFromCaptureTime` (new method); original `CalculateRtpStep` preserved as encoder-PTS fallback
- [x] Reset `_streamStartMs = -1` on `CloseConnection()`
- [ ] Add debug log on first frame showing `captureMs`, `_streamStartMs`, `absoluteRtp` — not implemented
- [ ] Test single-monitor: verify smooth playback, no stuttering — runtime test pending
- [ ] Test multi-monitor: verify both tracks show same content at same time — runtime test pending
- [ ] Test with variable frame rate — runtime test pending

## Success Criteria

1. Multi-monitor tracks produce identical RTP timestamps for barrier-synced frames
2. No `Environment.TickCount` randomization in initial RTP timestamp
3. Single-monitor playback is smooth (no regression)
4. Debug logs confirm capture-timestamp path is active (not fallback)
5. Reconnection works (clock resets properly)

## Risk Assessment

- **Medium risk**: This changes the core RTP timing calculation. Wrong math = video stuttering or freezing.
  - **Mitigation**: Fallback path preserves old encoder-PTS logic when `captureTimestampMs <= 0`. Can be triggered by temporarily removing Phase 1 wiring.
  - **Mitigation**: Clamp step to `[1, 180000]` prevents degenerate values.
- **Low risk**: `uint` overflow at ~13.25 hours of streaming (2^32 / 90000 / 3600). This is inherent to 32-bit RTP and acceptable per RFC 3550 (wraps naturally).
- **Low risk**: `_streamStartMs` race -- protected by `_clockLock`. Only written once.
