# Code Review Summary

## Scope
- Files reviewed: 3
  - `Infrastructure/Capture/DesktopAudioCapture.cs` (silence accumulator fix)
  - `Application/Streaming/SIPSorceryStreamer.cs` (audio RTP approach + diagnostics)
  - `Infrastructure/Encoding/OpusAudioEncoder.cs` (_disposed check reorder)
- Lines of code analyzed: ~300 (changed + surrounding context)
- Review focus: Audio slow-mode fix (silence accumulation), video track diagnostics, OpusEncoder defensive reorder
- Build result: **0 errors, 1 pre-existing warning** (CS0414 `_audioEnabled`)
- Updated plans: `plans/20260228-1430-av-sync-fix/phase-03-audio-video-alignment.md`

---

## Overall Assessment

The silence accumulator fix correctly solves the 64 fps → 100 fps regression. The fixed-480 RTP approach is a **deliberate and correct simplification** from the original Phase 3 design (wallclock-derived steps). The diagnostics add value with negligible overhead. Two bugs found: one **High** (accumulator drain/cap ordering) and one **Low** (clock source mismatch). The `_disposed` reorder in OpusEncoder is correct.

---

## Critical Issues

None.

---

## High Priority Findings

### H1: Accumulator drain runs BEFORE cap — discards frames silently

**File:** `Infrastructure/Capture/DesktopAudioCapture.cs` lines 246-254

```csharp
int framesToSend = 0;
while (_silenceAccumulatorMs >= 10)
{
    _silenceAccumulatorMs -= 10;   // accumulator fully drained here
    framesToSend++;
}
// Cap to prevent flooding after unexpected long delays (e.g., system sleep)
framesToSend = Math.Min(framesToSend, 10); // Max 100ms catch-up per tick
```

The `while` loop drains the full accumulator before the cap is applied. For a 150ms stall (e.g., 10 × 15.6ms timer ticks missing in a row): the loop runs 15 times, sets `_silenceAccumulatorMs = 0`, then the cap reduces `framesToSend` to 10. The 50ms represented by the 5 dropped frames is **gone from the accumulator**. The next tick starts from 0, not from the 50ms carry that should persist.

In normal operation (15.6ms ticks) this does not trigger — `framesToSend` is at most 1-2. It only matters after system sleep/resume or process stalls. At that point audio silently under-generates, causing the same slow-mode symptom the fix was meant to cure.

**Fix:** Restore the excess back to the accumulator after capping:

```csharp
int framesToSend = 0;
while (_silenceAccumulatorMs >= 10)
{
    _silenceAccumulatorMs -= 10;
    framesToSend++;
}

if (framesToSend > 10)
{
    // Return the excess ms to the accumulator so it carries forward
    _silenceAccumulatorMs += (framesToSend - 10) * 10L;
    framesToSend = 10;
}
```

Or equivalently, apply the cap before draining:

```csharp
long maxMs = Math.Min(_silenceAccumulatorMs, 100); // 10 frames max
int framesToSend = (int)(maxMs / 10);
_silenceAccumulatorMs -= framesToSend * 10;
```

---

## Medium Priority Improvements

### M1: TOCTOU window on `_audioClockInitialized` check-then-call

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 496-499

```csharp
lock (_audioSyncLock) { needsAnchor = !_audioClockInitialized; }
if (needsAnchor && timestampMs > 0)
    audioStep = CalculateAudioRtpStep(timestampMs);   // acquires _audioSyncLock again
```

The lock is released before the `if` branch executes. Two Opus frames arriving near-simultaneously (e.g., a large WASAPI buffer split across two encoder calls) could both read `needsAnchor = true` and both call `CalculateAudioRtpStep`. The second call overwrites `_lastAbsoluteAudioRtp` and returns the same absolute RTP value, causing SIPSorcery to see the anchor timestamp twice. This is unlikely in practice (WASAPI is single-threaded and Opus frames are processed sequentially in the encoder), but it is a real race if the audio callback thread ever overlaps (e.g., reentrant timer firing).

**Mitigation already present:** `CalculateAudioRtpStep` acquires `_audioSyncLock` internally and sets `_audioClockInitialized = true`, so the second call returns the same absolute value and SIPSorcery advances its RTP by 0 + anchor (still bounded). Not harmful but imprecise.

**Simplest fix:** Move the anchor check inside `CalculateAudioRtpStep` under a single lock — which is essentially what the previous review's H1 fix recommended. The current code partially implements this but leaves the check-then-call gap outside the lock.

### M2: Clock source inconsistency — `Environment.TickCount64` for detection vs `DateTimeOffset` for timestamps

**File:** `Infrastructure/Capture/DesktopAudioCapture.cs` lines 127, 202, 227, 258

`_lastDataTimeTicks` and `_lastSilenceFrameTicks` use `Environment.TickCount64` (boot-relative, ms). The wallclock timestamps passed to `OnAudioData` use `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` (Unix epoch, ms). Both are millisecond-resolution and effectively the same rate, but they differ in epoch by years and occasionally diverge by up to 15ms due to different underlying system calls.

This is harmless for A/V sync because `_streamStartMs` is initialized from the first event (either clock) and all subsequent steps are differences — the epoch cancels out. However the doc comment at line 33 says `timestampMs is wallclock time (Environment.TickCount64)` which is incorrect (it is actually `DateTimeOffset.UtcNow`). This creates confusion about which clock the audio timestamps use vs. video.

**Fix:** Update the comment on `OnAudioData` event:
```csharp
/// timestampMs is DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() — same clock as video capture.
```

### M3: `_lastAbsoluteAudioRtp` is written in `CalculateAudioRtpStep` but never read

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 75, 1059

`_lastAbsoluteAudioRtp` is stored inside `CalculateAudioRtpStep` (line 1059) but is never read back anywhere. Since the current design only calls `CalculateAudioRtpStep` once (for the anchor frame), `_lastAbsoluteAudioRtp` has no effect after assignment. It is reset in `CloseConnection()` and `ResetSyncState()` correctly, but the field itself is dead weight.

This is intentional — it is a remnant of the original Phase 3 wallclock-derived-step design. Once the design moved to "anchor once, then fixed 480", the per-step tracking became obsolete. The field and its lock-protected writes add minor overhead but no correctness issue.

**Fix (optional):** Remove `_lastAbsoluteAudioRtp` field and its assignments to reduce noise. Keep `_audioClockInitialized` (used by the needsAnchor check).

---

## Low Priority Suggestions

### L1: `_silenceAccumulatorMs` and `_lastSilenceFrameTicks` are unprotected `long` fields accessed from timer

**File:** `Infrastructure/Capture/DesktopAudioCapture.cs` lines 23-24

On 32-bit CLR, `long` reads/writes are not atomic. On 64-bit .NET 9 (the target here: `net9.0-windows10.0.26100.0`), `long` aligned reads/writes are atomic on x86-64. Not a bug on this platform, but worth a comment confirming the assumption. `Resume()` writes `_silenceAccumulatorMs = 0` from a caller thread while `SilenceWatchdog` may concurrently read/write it. On 64-bit .NET this is safe. A `volatile` marker or explicit `Interlocked` would make the intent explicit and platform-safe.

### L2: `sinceLast < 1` guard is redundant after the Phase 2 catch-up entry

**File:** `Infrastructure/Capture/DesktopAudioCapture.cs` line 241

After `_silenceActive = true` is set (Phase 2), `_lastSilenceFrameTicks = now` is assigned at line 231. Next timer fire: `sinceLast = now2 - now`. On Windows, `Environment.TickCount64` resolution is ~15ms, so `sinceLast >= 15`. The `< 1` guard can only fire if the timer somehow calls the watchdog twice with the same `now`, which requires sub-millisecond timer resolution. Safe but effectively dead code.

### L3: Keyframe diagnostic log logs `_lifetime_ avgEncLatency`, not interval

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 894-897

```csharp
long encCount = Interlocked.Read(ref track.EncodeLatencyCount);
long avgEncUs = encCount > 0 ? Interlocked.Read(ref track.EncodeLatencySum) / encCount : 0;
Logger.Info($"... avgEncLatency={avgEncUs}us ...");
```

`EncodeLatencySum` and `EncodeLatencyCount` are never reset, so `avgEncUs` is a lifetime average from stream start, not the recent interval. The stats log every 10s also uses the lifetime average. For trending diagnostics (e.g., "is encode getting slower over time?") a windowed average would be more useful, but for initial debugging the lifetime average is acceptable.

**Note:** `EncodeLatencySum` is an unbounded accumulating `long`. At 30fps × 1000ms frames, `EncodeLatencySum` grows by up to `30 × 1000000 = 30,000,000` per second. Overflow occurs at `long.MaxValue / 30,000,000 ≈ 307,000 seconds ≈ 85 hours`. Not a practical issue for streaming sessions, but worth noting.

### L4: `LastEncodeStartTicks` is a plain `long` written from `PushTexture`/`PushBgraTexture` (under `_lock`) and read from `OnEncodedData` (also under `_lock` for NV12, but NOT for BGRA path)

**File:** `Application/Streaming/SIPSorceryStreamer.cs` lines 793-794 (BGRA: no lock), 850-851 (NV12: inside `_lock`)

`PushBgraTexture` (line 793) writes `track.LastEncodeStartTicks` and calls `track.Encoder.EncodeBgraTexture` synchronously. `OnEncodedData` fires within that synchronous call — so the read in `OnEncodedData` happens on the same thread in the same call stack. No race. This is correct. Worth confirming via a comment to avoid future confusion.

---

## Specific Verification of Requested Concerns

### 1. Thread safety of silence accumulator

`_silenceAccumulatorMs` is accessed from:
- `SilenceWatchdog` (System.Threading.Timer callback) — **single-threaded for a given timer instance** on .NET: the timer does not refire if the previous callback is still running. So `SilenceWatchdog` itself cannot race with itself.
- `Resume()` — called from application thread. Writes `_silenceAccumulatorMs = 0` with no lock.

Race scenario: `Resume()` writes 0 while `SilenceWatchdog` reads and increments `_silenceAccumulatorMs`. On 64-bit .NET 9, this is an atomic write/read. The worst case: `Resume()` zeros the accumulator mid-calculation, causing `SilenceWatchdog` to use a stale value once. One spurious silence frame or one missed frame. **Acceptable for this use case** given the ~10ms granularity, but a `volatile` qualifier on `_silenceAccumulatorMs` would make the intent explicit.

**Verdict: Safe in practice on 64-bit .NET 9. Not safe on 32-bit CLR.**

### 2. Correctness of accumulation math

The accumulation is correct with one exception (H1 above). For normal 15.6ms timer ticks:
- Tick 1: sinceLast=15, accumulator=15 → 1 frame, carry=5
- Tick 2: sinceLast=16, accumulator=21 → 2 frames, carry=1
- Tick 3: sinceLast=16, accumulator=17 → 1 frame, carry=7
- etc.

Average: ~100 frames/sec. Math is sound. The timestamp spreading (`wallclockNow - ((framesToSend - 1 - i) * 10L)`) correctly interpolates past timestamps in chronological order.

### 3. Fixed 480 RTP increment approach

The current implementation is: **first frame uses wallclock anchor, all subsequent frames use fixed 480**. This is a **correct deviation from the Phase 3 plan** (which proposed wallclock-derived steps for every frame). The rationale is sound: WASAPI delivers audio in hardware-clocked batches at 48kHz. The sample counter is more accurate than `Environment.TickCount64` (15.6ms resolution). Fixed 480 per 10ms frame is the standard WebRTC Opus approach.

The one-time anchor correctly aligns the audio RTP timeline with the video timeline via the shared `_streamStartMs`. After that, fixed 480 keeps audio pacing at exactly 100 fps in RTP space, which matches the Opus frame rate.

**Verdict: Correct approach.**

### 4. A/V sync regression risk

No regression introduced. The anchor mechanism is unchanged from the original Phase 3 implementation. The accumulator fix (H1) makes silence periods more accurate, which can only improve sync. The diagnostics are read-only observations.

### 5. Encode latency tracking overhead

`Stopwatch.GetTimestamp()` is a single `RDTSC` or `QueryPerformanceCounter` call. The two calls per frame add ~50-100 ns. Two `Interlocked.Add/Increment` calls add ~10-20 ns each. Total overhead per frame: ~100-200 ns at 30fps = 3-6 µs/sec. **Negligible.**

### 6. Build

Confirmed: **0 errors, 1 pre-existing warning** (`CS0414: _audioEnabled`).

---

## Positive Observations

1. Accumulation-based silence generation is the correct fix for the Windows timer resolution problem. The approach is standard for any rate-limiter needing sub-tick precision.

2. The timestamp spreading in silence frames (`wallclockNow - ((framesToSend - 1 - i) * 10L)`) correctly produces chronological timestamps even when two frames are generated in one tick.

3. Switching to fixed 480 RTP increment for all-but-first audio frame is a better design than the original wallclock-derived-steps plan. The comment at lines 491-494 correctly explains the rationale.

4. Keyframe diagnostic logging is gated on `isKeyframe` so it does not spam at 30fps.

5. Phase 2 catch-up cap at `Math.Min(..., 50)` (500ms) in `SilenceWatchdog` correctly bounds the catch-up burst on silence entry.

---

## Recommended Actions

1. **[High]** Fix accumulator drain/cap ordering in `SilenceWatchdog` Phase 3 (H1). The excess ms must be returned to `_silenceAccumulatorMs` after capping, or the cap must be applied before draining.

2. **[Medium]** Resolve TOCTOU on `_audioClockInitialized` check (M1). Simplest fix: fold the `needsAnchor` check into `CalculateAudioRtpStep` under a single `lock (_audioSyncLock)`.

3. **[Medium]** Fix comment on `OnAudioData` event (M2): `timestampMs` source is `DateTimeOffset.UtcNow`, not `Environment.TickCount64`.

4. **[Low]** Remove dead `_lastAbsoluteAudioRtp` field and its writes (M3), or keep and add a comment explaining it is reserved for future wallclock-step restoration.

5. **[Low]** Add `volatile` to `_silenceAccumulatorMs` for explicit memory visibility semantics (L1).

---

## Metrics
- Type Coverage: N/A (C# with `#nullable enable`)
- Test Coverage: No automated tests in scope
- Build: 0 errors, 1 warning (pre-existing CS0414)
- Linting Issues: 0 critical, 1 high (accumulator drain), 3 medium, 4 low

---

## Unresolved Questions

1. **`Environment.TickCount64` vs `DateTimeOffset.UtcNow` divergence:** `_lastDataTimeTicks` uses `TickCount64` to measure elapsed silence threshold (100ms). The emitted wallclock timestamps use `DateTimeOffset.UtcNow`. These two clocks can diverge by up to 15ms on a given call. This means the "100ms silence threshold" measured in `TickCount64` could correspond to 85-115ms of `DateTimeOffset` wallclock. Functionally unimportant, but the threshold comment should note this.

2. **Phase 3 plan compliance:** The plan's `CalculateAudioRtpStep` was designed to produce a wallclock-derived step for every frame (not just the anchor). The implementation uses fixed 480 after anchor. The plan file's known issues section (lines 294-298) references H1/M1 from the prior review but does not reflect the deliberate design decision to use fixed 480. The plan should be updated to document this as an intentional deviation.
