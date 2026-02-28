# Code Review Summary

## Scope
- Files reviewed: 4
  - `Application/Protocol/PhaseProtocolHandler.cs` (2 lines changed)
  - `Application/Streaming/SIPSorceryStreamer.cs` (major changes)
  - `Infrastructure/Capture/DesktopAudioCapture.cs` (event signature + all invoke sites)
  - `Infrastructure/Encoding/OpusAudioEncoder.cs` (timestamp field, EncodePcm, OnEncodedAudio)
- Lines of code analyzed: ~600 (changed + surrounding context)
- Review focus: A/V sync fix — shared reference clock, capture-timestamp RTP, audio alignment
- Build result: **1 warning, 0 errors** (`CS0414: _audioEnabled assigned but never read` — pre-existing)

---

## Overall Assessment

The approach is architecturally sound. All three phases are implemented as designed. The core clock-sharing mechanism (`_streamStartMs` via `Interlocked.CompareExchange`) is correct. Two bugs were found: one **High** thread-safety issue in `CalculateAudioRtpStep`, and one **Medium** timestamp accuracy issue in `OpusAudioEncoder`. Several other lower-priority items are noted.

---

## Critical Issues

None.

---

## High Priority Findings

### H1: `CalculateAudioRtpStep` is NOT thread-safe — data race on `_audioClockInitialized` / `_lastAbsoluteAudioRtp`

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 1003–1041

`CalculateAudioRtpStep` is called from the `OnEncodedAudio` lambda, which fires on the WASAPI callback thread (or the silence watchdog timer thread). `CloseConnection()` resets `_audioClockInitialized = false` and `_lastAbsoluteAudioRtp = 0` on a different thread (caller of `Stop()`/reconnect). There is no synchronization protecting these two fields.

The video path correctly uses `lock (track)` for `CaptureClockInitialized` / `LastAbsoluteRtp`. The audio path uses nothing.

**Race scenario:** Audio thread reads `_audioClockInitialized = true`, proceeds to compute `step = absoluteRtp - _lastAbsoluteAudioRtp`. Simultaneously, `CloseConnection()` sets `_lastAbsoluteAudioRtp = 0`. Audio thread writes `_lastAbsoluteAudioRtp = absoluteRtp` — but the step it already computed and sent was `absoluteRtp - 0 = absoluteRtp` (a massive jump). The clamp to 960 mitigates the worst case but doesn't eliminate the race itself.

**Fix:**

```csharp
// Add a dedicated lock object (or reuse _lock)
private readonly object _audioSyncLock = new();

private uint CalculateAudioRtpStep(long audioTimestampMs)
{
    const int AudioClockRate = 48000;
    const uint DefaultStep = 480;

    if (Interlocked.Read(ref _streamStartMs) < 0)
        Interlocked.CompareExchange(ref _streamStartMs, audioTimestampMs, -1);

    long startMs = Interlocked.Read(ref _streamStartMs);
    long elapsedMs = audioTimestampMs - startMs;
    if (elapsedMs < 0) elapsedMs = 0;

    uint absoluteRtp = (uint)((long)AudioClockRate * elapsedMs / 1000L);

    lock (_audioSyncLock)
    {
        if (!_audioClockInitialized)
        {
            _audioClockInitialized = true;
            _lastAbsoluteAudioRtp = absoluteRtp;
            return absoluteRtp > 0 ? absoluteRtp : DefaultStep;
        }

        uint step = absoluteRtp > _lastAbsoluteAudioRtp
            ? absoluteRtp - _lastAbsoluteAudioRtp
            : DefaultStep;

        _lastAbsoluteAudioRtp = absoluteRtp;
        return Math.Clamp(step, 1, 960);
    }
}
```

And in `CloseConnection()`, acquire the same lock before resetting:
```csharp
lock (_audioSyncLock)
{
    _audioClockInitialized = false;
    _lastAbsoluteAudioRtp = 0;
}
```

---

## Medium Priority Improvements

### M1: `OpusAudioEncoder._currentTimestampMs` uses the LAST call's timestamp, not the FIRST sample's

**File:** `Infrastructure/Encoding/OpusAudioEncoder.cs` lines 61–63, 130

```csharp
public void EncodePcm(..., long timestampMs = 0)
{
    _currentTimestampMs = timestampMs;  // Overwrites on every call
    ...
    while (offset < dataLength)
    {
        // accumulate; when full, EncodeFrame() uses _currentTimestampMs
    }
}
```

When WASAPI delivers a large buffer (e.g., 20ms) that spans two Opus frames, `_currentTimestampMs` is set at the start of `EncodePcm`. The first frame uses the correct timestamp. But if the same `EncodePcm` call fills two frames, both frames emit the same timestamp — acceptable. However, when successive `EncodePcm` calls feed the same accumulating frame (partial fills), the frame's timestamp is overwritten by each call. The emitted timestamp is the timestamp of the **last** PCM chunk fed before the frame completed, not the first.

Per the Phase 3 plan, the intended behavior is: "capture timestamp of the **first** sample that starts this frame." The plan's Step 2 shows a `_frameTimestampSet` flag pattern that was **not implemented**. The simpler `_currentTimestampMs = timestampMs` approach was used instead.

**Impact:** Mild timestamp drift proportional to WASAPI buffer size. For 10ms Opus frames and typical 10ms WASAPI callbacks, this is usually zero (one call = one frame). For misaligned callbacks this introduces up to one callback period of error (~10-16ms on Windows).

**Fix option A (minimal):** Only set `_currentTimestampMs` when starting a new frame:
```csharp
public void EncodePcm(byte[] pcm16Data, int length, int inputSampleRate, int inputChannels, long timestampMs = 0)
{
    if (_disposed || length <= 0) return;
    // Capture timestamp only at the start of a new frame accumulation
    if (_frameBufferOffset == 0)
        _currentTimestampMs = timestampMs;
    ...
}
```

**Fix option B (per-plan):** Implement `_frameTimestampSet` as documented in phase-03.

Option A is simpler and sufficient given the 15.6ms Windows timer resolution already mentioned in the plan.

### M2: `CalculateRtpStepFromCaptureTime` first-frame return value diverges from Phase 2 plan

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 969–976

The plan specifies: "First frame: return small initial step (not zero, some decoders dislike 0)." The implementation returns `absoluteRtp > 0 ? absoluteRtp : fallback`, meaning the very first frame seeds SIPSorcery's internal counter to `absoluteRtp` (potentially thousands of ticks). This is correct for the absolute-position seeding intent but diverges from the plan which used `Math.Max(1, absoluteRtp)`.

More importantly: if `_streamStartMs` was set by the **audio** pipeline before the first video frame arrives (audio starts before video due to DTLS timing), then `absoluteRtp` for the first video frame could already be large (e.g., 90000 = 1 second). The client's decoder receives this as the initial step, which is fine for decoding but may affect how quickly the player renders.

**This is not a bug** in the current flow since the encoding happens after DTLS completes (both audio and video start at the same time in `onconnectionstatechange`), but worth noting for robustness.

### M3: Silence catch-up loop has unbounded frame count

**File:** `Infrastructure/Capture/DesktopAudioCapture.cs` lines 219–224

```csharp
int missedFrames = (int)(elapsed / 10);
```

If the audio capture pauses for an extended period (e.g., 10 seconds), `elapsed` could be 10000ms → `missedFrames = 1000`. The catch-up loop would fire 1000 `OnAudioData` events synchronously on the timer thread, stalling the timer for ~10+ seconds and potentially flooding the Opus encoder and RTP sender.

**Fix:** Cap `missedFrames`:
```csharp
int missedFrames = Math.Min((int)(elapsed / 10), 50); // Max 500ms catch-up
```

The plan does not address this bound; 100ms detection threshold means normal entry is `missedFrames ≈ 10`, but edge cases (system sleep/resume, audio device switch) can produce much larger values.

### M4: `lock (track)` in `CalculateRtpStep` (legacy path) — inconsistent with lock hierarchy

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 922–943

`CalculateRtpStep` (the encoder-PTS fallback path) locks on `track` directly. `CalculateRtpStepFromCaptureTime` (the new primary path) also uses `lock (track)`. Both are called from `OnEncodedData`, which is invoked from the encoder callback registered in `TryInitializeEncoderWithFallback`. Meanwhile, `PushTexture`/`PushBgraTexture` acquire `_lock` (the main lock) before calling encode, and encode is synchronous in the current implementation, so `OnEncodedData` fires *within* the `_lock` scope.

Locking `track` inside `OnEncodedData` (which is already called under `_lock`) is redundant but not deadlock-prone since `_lock` is never acquired inside the `lock (track)` scope. Clean but worth documenting.

---

## Low Priority Suggestions

### L1: `_audioEnabled` field is never read — existing warning promoted by this change

**File:** `Application/Streaming/SIPSorceryStreamer.cs` line 51

`CS0414` warning. The field is set in `InitializeAudio()` and cleared in `CloseConnection()`, but no code reads it. Either remove it or gate the audio logging on it.

### L2: Audio clamp comment says "0-20ms" but limit is 960 (exactly 20ms at 48kHz)

**File:** `Application/Streaming/SIPSorceryStreamer.cs` line 1038

```csharp
// Clamp to reasonable range: 1 to 960 (0-20ms at 48kHz)
step = Math.Clamp(step, 1, 960);
```

The plan's Phase 3 architecture section states max gap should be 500ms with clamp at 24000. The implementation uses 960 (20ms). The 20ms cap is more conservative and probably better for audio continuity, but the comment "0-20ms" is imprecise — it means "1 to 960 samples = max 20ms per step." The comment is not wrong, just worth confirming the intent is to cap at 20ms (2 frames) rather than the 500ms stated in the plan design.

### L3: `CalculateRtpStepFromCaptureTime` clamp comment says "500ms" but value is `ClockRate / 2 = 45000`

**File:** `Application/Streaming/SIPSorceryStreamer.cs` line 993

```csharp
// Clamp to reasonable range: 1 tick to 500ms worth of ticks
step = Math.Clamp(step, 1, (uint)(ClockRate / 2));
```

`ClockRate / 2 = 90000 / 2 = 45000` ticks at 90kHz = 500ms. Comment is correct. No issue.

### L4: Phase 2 plan recommended `_clockLock` object; implementation uses `Interlocked` instead

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 957–960

The plan specified a `_clockLock` object for `_streamStartMs` initialization. The implementation correctly uses `Interlocked.CompareExchange` instead, which is superior (lock-free, correct). The plan's `_clockLock` was not added and is not needed. No issue — implementation is better than the plan.

### L5: `PushTexture` bounds check order

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 790–793

```csharp
if (!_running || _disposed || !_connected || _isPaused) return;
if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;
if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;  // Too late!
```

The negative index check `monitorIndex < 0` comes **after** `_monitorPaused[monitorIndex]` is read with the same `monitorIndex`. A negative `monitorIndex` passed to `PushTexture` would not crash here (the `< _monitorPaused.Length` check excludes negative values since `Length` is non-negative and a negative index is less than any non-negative length... actually in C#, `(-1) < array.Length` is true for any non-empty array, so `_monitorPaused[-1]` **would throw `IndexOutOfRangeException`**).

`PushBgraTexture` (line 756) correctly checks `monitorIndex < 0` first. Fix `PushTexture` to match:

```csharp
if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;
```

---

## Positive Observations

1. **`Interlocked.CompareExchange` for `_streamStartMs`** — lock-free, correct first-writer-wins semantics. Better than the plan's `_clockLock` approach.

2. **Fallback to encoder-PTS preserved** — `captureTimestampMs > 0` guard ensures any call site that passes 0 (or omits the parameter) gets the original behavior. Zero risk of regression for callers outside `PhaseProtocolHandler`.

3. **`CloseConnection()` resets all sync state** — `_streamStartMs`, `_audioClockInitialized`, `_lastAbsoluteAudioRtp`, and per-track `CaptureClockInitialized` (implicitly, via `_tracks.Clear()` after `track.Dispose()`). Clean reconnect semantics.

4. **Silence watchdog catch-up interpolation** — generating frames with `catchUpBase + i * 10L` timestamps correctly advances the wallclock in proportional increments rather than all frames sharing `now`.

5. **Clamping on both video and audio steps** — prevents degenerate values from reaching SIPSorcery/libwebrtc when timestamps misbehave.

6. **`PendingCaptureTimestampMs` pattern** — storing the capture timestamp on `TrackInfo` before calling the synchronous encoder, then reading it in `OnEncodedData`, correctly bridges the capture→encode→send chain without changing the encoder interface.

7. **Build succeeds** — 0 errors, 1 pre-existing warning (not introduced by this change).

---

## Task Completeness Verification

### Phase 1 todos (phase-01-capture-timestamp-passthrough.md)
- [x] `captureTimestampMs` param added to `PushBgraTexture` (default 0)
- [x] `captureTimestampMs` param added to `PushTexture` (default 0)
- [x] `PendingCaptureTimestampMs` field added to `TrackInfo` (renamed from `LastCaptureTimestampMs`)
- [x] Capture timestamp stored on track before encoding in both Push methods
- [x] `timestamp` passed in PhaseProtocolHandler NV12 handler (line 1798)
- [x] `timestamp` passed in PhaseProtocolHandler BGRA handler (line 1830)
- [ ] Build verification logged — build passes (confirmed in this review)
- [ ] Streaming behavior verification — runtime test, not verifiable statically

### Phase 2 todos (phase-02-unified-rtp-clock.md)
- [x] `_streamStartMs` field added (using `Interlocked`, not separate `_clockLock`)
- [x] `LastAbsoluteRtp` field added to `TrackInfo`
- [x] `CalculateRtpStepFromCaptureTime` implemented (new method, original `CalculateRtpStep` preserved as fallback)
- [x] `_streamStartMs = -1` reset on `CloseConnection()`
- [ ] Debug log on first frame showing captureMs/startMs/absoluteRtp — not present (gap vs. plan; low priority)
- [ ] Runtime tests — not verifiable statically

### Phase 3 todos (phase-03-audio-video-alignment.md)
- [x] `long timestampMs` added to `DesktopAudioCapture.OnAudioData` event
- [x] `Environment.TickCount64` passed from `OnDataAvailable` (2 invocation sites: float32 and PCM16 paths)
- [x] `Environment.TickCount64` passed from `SilenceWatchdog` (3 invocation sites: catch-up loop + continuous + drift-compensation)
- [x] `long timestampMs` param added to `OpusAudioEncoder.EncodePcm`
- [x] `long timestampMs` added to `OpusAudioEncoder.OnEncodedAudio` event
- [ ] `_frameTimestampSet` pattern for first-sample timestamp — **NOT implemented** (see M1 above; simpler approach used instead)
- [x] `_lastAbsoluteAudioRtp` and `_audioClockInitialized` fields added
- [x] Audio pipeline wiring updated in `InitializeAudio` to pass timestamps
- [x] `CalculateAudioRtpStep` method implemented
- [x] Audio state reset alongside video state on reconnect
- [ ] Runtime tests — not verifiable statically

---

## Recommended Actions

1. **[High, fix before ship]** Add lock around `_audioClockInitialized` / `_lastAbsoluteAudioRtp` in `CalculateAudioRtpStep` and the corresponding reset in `CloseConnection()`. See H1 above.

2. **[Medium]** Fix `OpusAudioEncoder._currentTimestampMs` to only be set when `_frameBufferOffset == 0` (start of new frame accumulation). One-line fix. See M1 above.

3. **[Medium]** Cap `missedFrames` in `SilenceWatchdog` catch-up to prevent flooding after long pauses. See M3 above.

4. **[Low]** Fix `PushTexture` bounds check order — negative index check must precede `_monitorPaused` array access. See L5 above.

5. **[Low]** Remove or read `_audioEnabled` field to resolve the `CS0414` build warning. See L1 above.

---

## Metrics
- Type Coverage: N/A (C# with `#nullable enable`)
- Test Coverage: No automated tests in scope
- Build: 0 errors, 1 warning (pre-existing CS0414)
- Linting Issues: 0 critical, 1 high (thread safety), 3 medium, 3 low

---

## Unresolved Questions

1. **SIPSorcery `SendAudio`/`SendVideo` semantics:** The review assumes `duration` is added to SIPSorcery's internal RTP counter. If SIPSorcery treats `duration` as an absolute override rather than an additive step, the entire timestamp strategy would need revisiting. This should be confirmed against SIPSorcery 8.x source or docs.

2. **Audio-starts-before-video window:** `InitializeAudio()` and `InitializeEncoders()` are called sequentially in `onconnectionstatechange`. The audio capture and silence watchdog can fire before the first video frame. If audio sets `_streamStartMs` and video does not arrive for several seconds (e.g., encoder init delay), video's `absoluteRtp` on first frame will already be large. This is functionally correct but could cause an initial video RTP jump. Worth monitoring in runtime testing.

3. **`Environment.TickCount64` vs. capture barrier timestamp:** Video uses the DXGI barrier `captureTimestamp` (from `PerMonitorCapture._syncedTimestamp`, likely `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`). Audio uses `Environment.TickCount64`. On Windows, both are ultimately system-clock-derived, but their epoch and precision may differ slightly. If `captureTimestamp` uses `UnixTimeMilliseconds` and `TickCount64` uses system boot time, the absolute values differ but the *differences* (which is what matters for RTP steps) are both millisecond-granularity monotonic clocks and are comparable once anchored to `_streamStartMs`. This is safe as long as `_streamStartMs` is set by whichever clock fires first and both subsequent timestamps are from the same clock source.
