# Phase 3: Audio-Video Clock Alignment

## Context Links
- Parent: [plan.md](./plan.md)
- Depends on: [phase-02-unified-rtp-clock.md](./phase-02-unified-rtp-clock.md)

## Overview

Align the audio RTP clock with the video RTP clock by deriving audio timestamps from the same `_streamStartMs` wallclock reference established in Phase 2. Currently audio uses a fixed `480` RTP increment per 10ms Opus frame with no relationship to video timing.

The approach: add wallclock timestamps to the audio data pipeline (`DesktopAudioCapture` -> `OpusAudioEncoder` -> `SIPSorceryStreamer`), then compute audio RTP step from `(audioTimestampMs - _streamStartMs) * 48000 / 1000`.

## Key Insights

1. **Current audio pipeline**: `DesktopAudioCapture.OnAudioData(pcm, length, sampleRate, channels)` -> `OpusAudioEncoder.EncodePcm(...)` -> `OpusAudioEncoder.OnEncodedAudio(opusData, opusLength, rtpDuration=480)` -> `_pc.SendAudio(480, packet)`.
2. **Fixed increment is correct for pacing** but wrong for sync. The 480 per 10ms is the right *rate* but the *absolute position* has no anchor to video.
3. **SIPSorcery `SendAudio(uint duration, byte[] data)`** adds `duration` to its internal RTP timestamp. To sync with video, we need the cumulative sum of audio steps to track wallclock elapsed time since `_streamStartMs` at the 48kHz clock rate.
4. **Wallclock-based audio RTP**: Instead of always adding 480, compute the step as `currentAbsoluteAudioRtp - lastAbsoluteAudioRtp` where `absoluteAudioRtp = (timestampMs - _streamStartMs) * 48000 / 1000`. Over time this averages 480/frame but allows small corrections.
5. **Silence handling**: The silence watchdog generates fake frames with `Environment.TickCount64` timestamps, which naturally advance the wallclock, keeping audio RTP progressing correctly even during silence.

## Requirements

- `DesktopAudioCapture.OnAudioData` event carries a `long timestampMs` parameter
- `OpusAudioEncoder.EncodePcm` accepts and `OnEncodedAudio` emits a `long timestampMs`
- `SIPSorceryStreamer` computes audio RTP step from `(timestampMs - _streamStartMs)` at 48kHz
- If `_streamStartMs` is not yet set (video hasn't started), audio sets it (whichever pipeline starts first wins)
- Audio RTP step is clamped to avoid degenerate values

## Architecture

```
DesktopAudioCapture                 OpusAudioEncoder                    SIPSorceryStreamer
  OnDataAvailable:                    EncodePcm(pcm,len,sr,ch,tsMs):     OnEncodedAudio handler:
    tsMs = Environment.TickCount64      accumulate frames                   absoluteRtp = (tsMs-startMs)*48000/1000
    OnAudioData(pcm,len,sr,ch,tsMs)     on full frame:                      step = absoluteRtp - lastAudioRtp
                                          OnEncodedAudio(opus,len,dur,tsMs)   SendAudio(step, packet)
  SilenceWatchdog:
    tsMs = Environment.TickCount64
    OnAudioData(silence,len,sr,ch,tsMs)

Both audio and video reference the same _streamStartMs.
```

## Related Code Files

| File | Lines | Role |
|------|-------|------|
| `Infrastructure/Capture/DesktopAudioCapture.cs` | 33,120-167,191-234 | Event, data handler, silence watchdog |
| `Infrastructure/Encoding/OpusAudioEncoder.cs` | 34,59-110,112-133 | Event, encode, frame assembly |
| `Application/Streaming/SIPSorceryStreamer.cs` | 450-477 | Audio pipeline wiring |

## Implementation Steps

### Step 1: Add timestamp to DesktopAudioCapture.OnAudioData

File: `Infrastructure/Capture/DesktopAudioCapture.cs`

**Change event signature (line 33):**

Before:
```csharp
public event Action<byte[], int, int, int>? OnAudioData;
```

After:
```csharp
public event Action<byte[], int, int, int, long>? OnAudioData;
```

**Add timestamp to OnDataAvailable invocations (lines 154, 161):**

Before:
```csharp
OnAudioData?.Invoke(pcm16, pcm16Bytes, _sampleRate, _channels);
```

After:
```csharp
OnAudioData?.Invoke(pcm16, pcm16Bytes, _sampleRate, _channels, Environment.TickCount64);
```

Apply same change to the already-PCM16 path (line 161).

**Add timestamp to SilenceWatchdog invocations (lines 219, 228, 233):**

Before:
```csharp
OnAudioData?.Invoke(silence, bytesPerFrame, _sampleRate, _channels);
```

After:
```csharp
OnAudioData?.Invoke(silence, bytesPerFrame, _sampleRate, _channels, Environment.TickCount64);
```

Note: Each silence frame gets its own `TickCount64` call. Since the watchdog fires every ~10ms, this naturally advances the timestamp by ~10ms per frame.

### Step 2: Add timestamp to OpusAudioEncoder

File: `Infrastructure/Encoding/OpusAudioEncoder.cs`

**Add timestamp tracking field (near line 28):**
```csharp
private long _currentFrameTimestampMs; // Timestamp of the first PCM sample in the current frame
private bool _frameTimestampSet;       // Whether _currentFrameTimestampMs is set for current frame
```

**Change OnEncodedAudio event signature (line 34):**

Before:
```csharp
public event Action<byte[], int, uint>? OnEncodedAudio;
```

After:
```csharp
public event Action<byte[], int, uint, long>? OnEncodedAudio;
```

**Change EncodePcm signature (line 59):**

Before:
```csharp
public void EncodePcm(byte[] pcm16Data, int length, int inputSampleRate, int inputChannels)
```

After:
```csharp
public void EncodePcm(byte[] pcm16Data, int length, int inputSampleRate, int inputChannels, long timestampMs = 0)
```

**Capture timestamp on first data of each frame accumulation cycle.**

In the accumulation loop (inside `while (offset < dataLength)`, before `Buffer.BlockCopy`), add:
```csharp
// Capture timestamp of the first sample that starts this frame
if (_frameBufferOffset == 0 && !_frameTimestampSet)
{
    _currentFrameTimestampMs = timestampMs;
    _frameTimestampSet = true;
}
```

**After `EncodeFrame()` call (line 107), reset the flag:**
```csharp
_frameTimestampSet = false;
```

**Change EncodeFrame to emit timestamp (line 127):**

Before:
```csharp
OnEncodedAudio?.Invoke(_opusOutputBuffer, encodedBytes, RTP_DURATION_PER_FRAME);
```

After:
```csharp
OnEncodedAudio?.Invoke(_opusOutputBuffer, encodedBytes, RTP_DURATION_PER_FRAME, _currentFrameTimestampMs);
```

### Step 3: Wire timestamps in SIPSorceryStreamer audio pipeline

File: `Application/Streaming/SIPSorceryStreamer.cs`

**Add audio RTP state fields (near the `_streamStartMs` fields from Phase 2):**
```csharp
private uint _lastAbsoluteAudioRtp;
private bool _audioRtpInitialized;
```

**Update capture -> encoder wiring (line 454):**

Before:
```csharp
_audioCapture.OnAudioData += (pcm, length, sampleRate, channels) =>
{
    if (_isPaused || !_connected || !_running) return;
    _opusEncoder.EncodePcm(pcm, length, sampleRate, channels);
};
```

After:
```csharp
_audioCapture.OnAudioData += (pcm, length, sampleRate, channels, timestampMs) =>
{
    if (_isPaused || !_connected || !_running) return;
    _opusEncoder.EncodePcm(pcm, length, sampleRate, channels, timestampMs);
};
```

**Update encoder -> RTP send wiring (line 460):**

Before:
```csharp
_opusEncoder.OnEncodedAudio += (opusData, opusLength, rtpDuration) =>
{
    if (!_connected || !_running || _pc == null) return;
    try
    {
        var packet = new byte[opusLength];
        Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);
        _pc.SendAudio(rtpDuration, packet);
        Interlocked.Increment(ref _audioPacketsSent);
    }
    ...
};
```

After:
```csharp
_opusEncoder.OnEncodedAudio += (opusData, opusLength, rtpDuration, timestampMs) =>
{
    if (!_connected || !_running || _pc == null) return;
    try
    {
        var packet = new byte[opusLength];
        Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);

        uint audioStep = CalculateAudioRtpStep(timestampMs, rtpDuration);
        _pc.SendAudio(audioStep, packet);
        Interlocked.Increment(ref _audioPacketsSent);
    }
    ...
};
```

### Step 4: Implement CalculateAudioRtpStep

Add new method to `SIPSorceryStreamer`:

```csharp
private uint CalculateAudioRtpStep(long timestampMs, uint fallbackDuration)
{
    const int AudioClockRate = 48000;

    if (timestampMs <= 0)
        return fallbackDuration; // No timestamp, use fixed 480

    // Set or read shared stream start (same lock as video)
    lock (_clockLock)
    {
        if (_streamStartMs < 0)
            _streamStartMs = timestampMs;
    }

    long elapsedMs = timestampMs - _streamStartMs;
    if (elapsedMs < 0) elapsedMs = 0;

    uint absoluteAudioRtp = (uint)((long)AudioClockRate * elapsedMs / 1000L);

    if (!_audioRtpInitialized)
    {
        _lastAbsoluteAudioRtp = absoluteAudioRtp;
        _audioRtpInitialized = true;
        return Math.Max(1, absoluteAudioRtp);
    }

    uint step = absoluteAudioRtp - _lastAbsoluteAudioRtp;
    _lastAbsoluteAudioRtp = absoluteAudioRtp;

    // Clamp: avoid 0-step and huge jumps (>500ms = 24000 samples)
    if (step == 0) step = 1;
    if (step > 24000) step = fallbackDuration; // >500ms gap = anomaly

    return step;
}
```

### Step 5: Reset audio RTP state on stream stop/reconnect

Wherever `_streamStartMs` is reset (Phase 2, Step 4), also reset:
```csharp
_audioRtpInitialized = false;
_lastAbsoluteAudioRtp = 0;
```

## Todo List

- [x] Add `long timestampMs` parameter to `DesktopAudioCapture.OnAudioData` event
- [x] Pass `Environment.TickCount64` from `OnDataAvailable` (2 invocation sites: float32 and PCM16 paths)
- [x] Pass `Environment.TickCount64` from `SilenceWatchdog` (3 invocation sites: catch-up loop, continuous, drift-compensation)
- [x] Add `long timestampMs` parameter to `OpusAudioEncoder.EncodePcm`
- [x] Add `long timestampMs` to `OpusAudioEncoder.OnEncodedAudio` event
- [ ] Track timestamp of FIRST sample per frame in OpusAudioEncoder — NOT implemented; simpler `_currentTimestampMs = timestampMs` used instead (see review M1: causes last-call-wins rather than first-sample semantics; fix recommended)
- [x] Add `_lastAbsoluteAudioRtp` and `_audioClockInitialized` fields to SIPSorceryStreamer (field name differs from plan: `_audioRtpInitialized` vs `_audioClockInitialized`)
- [x] Update audio pipeline wiring in `InitializeAudio` to pass timestamps
- [x] Implement `CalculateAudioRtpStep` method
- [x] Reset audio state alongside video state on `CloseConnection()`
- [x] Build and verify no compilation errors (0 errors)
- [ ] Test: audio plays without crackling or gaps — runtime test pending
- [ ] Test: A/V lip sync on a video with speech — runtime test pending
- [ ] Test: silence -> sound transition maintains sync — runtime test pending

## Known Issues (from code review 260228)

### Resolved
- **[RESOLVED]** `CalculateAudioRtpStep` thread-safety: `_audioSyncLock` added; `CloseConnection()` and `ResetSyncState()` acquire lock before resetting `_audioClockInitialized` / `_lastAbsoluteAudioRtp`.
- **[RESOLVED]** `OpusAudioEncoder._currentTimestampMs` last-call-wins: fixed — `_currentTimestampMs` is now only assigned when `_frameBufferOffset == 0` (first sample of new frame).
- **[RESOLVED]** `SilenceWatchdog` unbounded `missedFrames`: capped at `Math.Min((int)(elapsed / 10), 50)` (500ms max on entry to silence mode).

### Open (from code review 260228 — silence-slowmode-diagnostics)
- **[HIGH]** `SilenceWatchdog` Phase 3 accumulator drain order: the `while` loop fully drains `_silenceAccumulatorMs` before the 10-frame cap is applied. Frames beyond the cap are silently lost from the accumulator, causing under-generation after long stalls. Fix: return excess ms to accumulator after capping.
- **[MEDIUM]** TOCTOU on `_audioClockInitialized` check: `needsAnchor` is read outside the lock, then `CalculateAudioRtpStep` is called which re-acquires the lock. Fix: fold the check into `CalculateAudioRtpStep` under a single `lock (_audioSyncLock)`.
- **[MEDIUM]** `OnAudioData` event comment says `timestampMs is Environment.TickCount64` but actual source is `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`. Fix: update comment.
- **[LOW]** `_lastAbsoluteAudioRtp` is written but never read (dead field since design moved to fixed-480-after-anchor). Remove or add comment explaining it is reserved for future wallclock-step restoration.

### Design Deviation (intentional)
- Phase 3 plan proposed wallclock-derived RTP steps for every Opus frame. Implemented as: **anchor once** (first frame uses `CalculateAudioRtpStep`) then **fixed 480** for all subsequent frames. Rationale: WASAPI 48kHz hardware clock is more accurate than `Environment.TickCount64` (15.6ms resolution); fixed 480 is standard WebRTC Opus pacing.

## Success Criteria

1. Audio and video RTP timestamps both reference the same `_streamStartMs` origin
2. Audio-video sync within 30ms (test with lip-sync video content)
3. Silence periods do not cause audio drift (watchdog timestamps advance correctly)
4. No audio crackling from degenerate step values (clamped to [1, 24000])
5. First audio/video stream to start sets `_streamStartMs` (handles either starting first)

## Risk Assessment

- **Medium risk**: Changing audio RTP step from constant 480 to variable wallclock-derived values. Jitter in `Environment.TickCount64` (15.6ms resolution on Windows) could cause uneven steps.
  - **Mitigation**: Clamp to [1, 24000]. Average step over time still equals 480.
  - **Mitigation**: Fallback to fixed 480 when `timestampMs <= 0`.
  - **Note**: `Environment.TickCount64` resolution on Windows is typically 15.6ms (default timer). For 10ms Opus frames, this means timestamps may quantize to {0, 16, 16, 16, 0, 16, ...} ms intervals. The absolute RTP calculation smooths this because it uses cumulative elapsed time, not per-frame deltas.
- **Low risk**: Event signature changes propagate to all subscribers. Only one subscriber exists per event (the lambda in `SIPSorceryStreamer.InitAudioPipeline`).
- **Low risk**: `_streamStartMs` race between audio and video -- protected by `_clockLock`. Whichever writes first wins; both produce correct relative timestamps thereafter.

## Unresolved Questions

1. **Windows timer resolution**: Should we call `timeBeginPeriod(1)` to get 1ms timer resolution for `Environment.TickCount64`? This improves timestamp granularity but has system-wide power consumption impact. Current 15.6ms resolution is acceptable since we use absolute-time RTP (not per-frame deltas).
2. **WASAPI buffer latency compensation**: WASAPI loopback has inherent latency (10-30ms). Should we subtract an estimated offset from audio timestamps? This is a constant offset, so it shifts sync but doesn't cause drift. Could be a future enhancement with a configurable `_audioLatencyOffsetMs` field.
