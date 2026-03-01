# Phase 01: Route FPS Feedback to Bitrate Controller

## Bug Analysis

**File:** `Application/Streaming/SIPSorceryStreamer.Bitrate.cs`
**Lines:** 12-31

`ProcessFpsFeedback()` computes pipeline loss percentage and logs it, but never feeds this data
into `AdaptiveBitrateController`. The FPS feedback path is effectively dead code for bitrate decisions.

Meanwhile, `ProcessQualityFeedback()` (line 40-89) correctly routes through the controller and
applies bitrate changes to all track encoders. The fix reuses this existing path.

## Root Cause

```csharp
// Line 12-31: computes lossPercent and effectiveFps, then ONLY logs
Logger.Info($"[Pipeline] Mon{monitorIndex}: Server sent {serverSentFrames}, Client received {clientTotalFrames} (loss={lossPercent:F1}%)");
Logger.Info($"[SIPSorcery] FPS feedback m{monitorIndex}: {effectiveFps:F1}fps, dropped={droppedFrames}");
// <-- nothing else happens
```

## Fix: Add Bitrate Action After Logging

### Thresholds
- **FPS critical:** `effectiveFps < 0.5 * _fps` (FPS ratio below 50% of target)
- **Pipeline loss critical:** `lossPercent > 30f`
- **Severe (keyframe burst):** `effectiveFps < 0.3 * _fps` OR `lossPercent > 50f`

### Implementation

Replace `ProcessFpsFeedback` method body (lines 12-31) with:

```csharp
public void ProcessFpsFeedback(int monitorIndex, float effectiveFps, int droppedFrames, long clientTotalFrames)
{
    // Get server's sent frame count for this monitor
    long serverSentFrames = 0;
    lock (_lock)
    {
        if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
        {
            serverSentFrames = Interlocked.Read(ref _tracks[monitorIndex].SentFrames);
        }
    }

    // Calculate loss percentage
    float lossPercent = serverSentFrames > 0
        ? (1f - (float)clientTotalFrames / serverSentFrames) * 100f
        : 0f;

    Logger.Info($"[Pipeline] Mon{monitorIndex}: Server sent {serverSentFrames}, Client received {clientTotalFrames} (loss={lossPercent:F1}%)");
    Logger.Info($"[SIPSorcery] FPS feedback m{monitorIndex}: {effectiveFps:F1}fps, dropped={droppedFrames}");

    // --- NEW: Route to bitrate controller when FPS or pipeline loss is critical ---
    float fpsRatio = _fps > 0 ? effectiveFps / _fps : 1f;
    bool fpsCritical = fpsRatio < 0.5f;
    bool lossCritical = lossPercent > 30f;

    if (fpsCritical || lossCritical)
    {
        Logger.Info($"[FpsBitrateAction] Triggered: fpsRatio={fpsRatio:F2}, loss={lossPercent:F1}% → creating synthetic QualityFeedback");

        // Build synthetic QualityFeedback from FPS data
        var syntheticFeedback = new QualityFeedbackMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EffectiveFps = effectiveFps,
            TargetFps = _fps,
            PacketLossRate = lossPercent / 100f,       // convert % to 0-1 range
            AvgPacketLossRate = lossPercent / 100f,
            BufferStatus = fpsRatio < 0.3f ? "starving" : "lossy",
            RttMs = 0,       // unknown from FPS feedback
            JitterMs = 0,
            ConnectionHealth = fpsRatio < 0.3f ? 1 : 3,
            IsWiFi = _bitrateController.IsWiFiMode,
            Monitors = new System.Collections.Generic.List<MonitorFeedback>
            {
                new MonitorFeedback
                {
                    Index = monitorIndex,
                    DroppedFrames = droppedFrames,
                    RenderedFrames = Math.Max(0, (int)(clientTotalFrames - droppedFrames)),
                }
            }
        };

        ProcessQualityFeedback(syntheticFeedback);

        // Force keyframe burst on severe conditions
        bool severe = fpsRatio < 0.3f || lossPercent > 50f;
        if (severe)
        {
            Logger.Info($"[FpsBitrateAction] Severe condition → keyframe burst mon={monitorIndex}");
            RequestKeyframeBurst(monitorIndex, 3);
        }
    }
}
```

### Key Design Decisions

1. **Synthetic QualityFeedback** -- reuses the existing `ProcessQualityFeedback()` pipeline
   instead of duplicating bitrate logic. This means all EWMA smoothing, cooldown, and
   warmup rules automatically apply.

2. **Conservative thresholds** -- 50% FPS ratio and 30% pipeline loss are high bars to avoid
   false positives from static content or brief hiccups.

3. **Keyframe burst only on severe** -- avoids keyframe storms on minor degradation.
   Only fires when FPS < 30% target OR loss > 50%.

4. **Return value of ProcessQualityFeedback ignored** -- the `BitrateAdjustedMessage` return
   is discarded because the FPS feedback handler in `PhaseProtocolHandler.Phase3.cs` (line 288-315)
   already sends its own `FpsAdjustedMessage` ack. The bitrate change still applies to encoders
   internally. If we want the client notified of the bitrate change, the Phase3 handler needs a
   small update (see "Optional Enhancement" below).

### Optional Enhancement (Phase3 handler)

In `PhaseProtocolHandler.Phase3.cs` line 294-307, after calling `ProcessFpsFeedback`, check
if a `BitrateAdjustedMessage` was produced and send it. This requires changing the return type
of `ProcessFpsFeedback` to `BitrateAdjustedMessage?`, but this is NOT required for the fix to
work -- the server-side bitrate reduction happens regardless.

**Recommendation:** Skip this for now. The client will learn about the new bitrate on its next
`quality_feedback` cycle (every 2s).

### Affected Methods
- `ProcessFpsFeedback()` in `SIPSorceryStreamer.Bitrate.cs` -- modified
- `ProcessQualityFeedback()` -- unchanged (reused as-is)
- `RequestKeyframeBurst()` -- unchanged (called as-is)

### Risk Assessment
- **Low risk.** Only adds code after existing logging. Does not modify any existing path.
- Multi-monitor safe: synthetic feedback uses per-monitor `monitorIndex` for keyframe burst,
  and `ProcessQualityFeedback` applies bitrate to all tracks (existing behavior).
