# Phase 02: Allow Emergency Bitrate Reduction During Warmup

## Bug Analysis

**File:** `Application/Streaming/AdaptiveBitrateController.cs`
**Lines:** 207, 247-251

The warmup guard at line 247-251 returns `current` (no change) for ALL non-packet-loss conditions
during the first 15 seconds. The only bypass is high packet loss (line 226-230) which checks
`PacketLossRate > 0.02f`. But critical FPS drop with actual dropped frames (line 234-244) is
checked BEFORE warmup and correctly bypasses it already.

**However**, there is a gap: the critical FPS check (line 234) requires `hasActualProblems`
(dropped frames > 0 OR packet loss > 1%), which excludes scenarios where:
- Pipeline loss is high (server sent >> client received) but client reports 0 dropped frames
  because it never received the frames to begin with
- The `PacketLossRate` in QualityFeedback is based on RTP RTCP reports which may lag behind

The 15s warmup is too long for WiFi environments where congestion can cause immediate stalls.

## Root Cause

```csharp
// Line 247-251: blanket warmup guard
if (inWarmup)
{
    // Only log once every few seconds to avoid spam
    return current;  // <-- blocks ALL reductions except packet loss and critical FPS w/ drops
}
```

## Fix: Add Emergency Bypass Inside Warmup Guard

### Thresholds for Emergency
- **FPS emergency:** `effectiveFps / targetFps < 0.3` (same as `FPS_CRITICAL_THRESHOLD`)
- **Loss emergency:** `packetLossRate > 0.5` (50% loss -- catastrophic)
- **Emergency action:** cut bitrate by 50%

### Implementation

Replace the warmup guard block (lines 247-251) with:

```csharp
// During warmup, skip normal buffer/FPS-based decreases (these are normal during startup)
if (inWarmup)
{
    // Emergency bypass: allow drastic reduction even during warmup
    // when conditions are catastrophic (stream is clearly broken, not just warming up)
    float warmupFpsRatio = feedback.TargetFps > 0 ? feedback.EffectiveFps / feedback.TargetFps : 1f;
    bool fpsEmergency = warmupFpsRatio < FPS_CRITICAL_THRESHOLD;  // < 30% target
    bool lossEmergency = feedback.PacketLossRate > 0.5f;           // > 50% packet loss

    if (fpsEmergency || lossEmergency)
    {
        int emergencyBitrate = Math.Max(MinBitrateKbps, current / 2);  // 50% cut
        Logger.Info($"[EmergencyWarmup] Bypass warmup: fpsRatio={warmupFpsRatio:F2}, loss={feedback.PacketLossRate:P1} → {current} → {emergencyBitrate} kbps");
        return emergencyBitrate;
    }

    return current;
}
```

### Why These Thresholds

| Condition | Threshold | Rationale |
|-----------|-----------|-----------|
| FPS < 30% | `FPS_CRITICAL_THRESHOLD` (0.3) | Already defined as critical in the codebase (line 66). Reusing existing constant. |
| Loss > 50% | `0.5f` hardcoded | 50% packet loss means half the stream is gone. No warmup can explain this. |
| Action: 50% cut | `current / 2` | Aggressive enough to unstick a stall. `MinBitrateKbps` (2000) floor prevents going too low. |

### Why Not Just Shorten WARMUP_PERIOD_MS

Shortening the warmup would cause bitrate oscillation during normal startup where FPS ramps up
gradually. The 15s warmup is correct for normal conditions. The fix is surgical: only bypass
when conditions are clearly catastrophic, not "still warming up."

### Interaction With Phase 01

Phase 01's synthetic `QualityFeedback` will flow through `ProcessFeedback()` and hit this
emergency bypass if:
- The synthetic feedback has `EffectiveFps / TargetFps < 0.3`
- OR the synthetic `PacketLossRate` (derived from pipeline loss) exceeds 0.5

This creates a two-layer defense:
1. Phase 01 ensures FPS data reaches the controller
2. Phase 02 ensures the controller can act even during warmup

### Affected Lines
- `AdaptiveBitrateController.cs` lines 247-251 -- replaced with emergency bypass block
- No other methods changed

### Test Cases to Add (`AdaptiveBitrateControllerTests.cs`)

```csharp
[Fact]
public void EmergencyBypass_DuringWarmup_CriticalFps_ReducesBitrate()
{
    var controller = new AdaptiveBitrateController();
    controller.Initialize(10000);  // 10 Mbps

    // Immediately send critical FPS feedback (during 15s warmup)
    var feedback = new QualityFeedbackMessage
    {
        EffectiveFps = 5f,    // 8.3% of 60fps target
        TargetFps = 60f,
        PacketLossRate = 0f,
        BufferStatus = "starving",
        Monitors = new List<MonitorFeedback>
        {
            new() { DroppedFrames = 50, RenderedFrames = 10 }
        }
    };

    var decision = controller.ProcessFeedback(feedback);

    Assert.True(decision.Changed);
    Assert.Equal(5000, decision.NewBitrate);  // 50% of 10000
}

[Fact]
public void EmergencyBypass_DuringWarmup_HighLoss_ReducesBitrate()
{
    var controller = new AdaptiveBitrateController();
    controller.Initialize(10000);

    var feedback = new QualityFeedbackMessage
    {
        EffectiveFps = 50f,
        TargetFps = 60f,
        PacketLossRate = 0.6f,  // 60% loss
        BufferStatus = "lossy",
    };

    var decision = controller.ProcessFeedback(feedback);

    // High packet loss (>2%) already bypasses warmup via line 226-230
    // but emergency path would also fire for >50% loss
    Assert.True(decision.Changed);
    Assert.True(decision.NewBitrate < 10000);
}

[Fact]
public void Warmup_NormalConditions_NoReduction()
{
    var controller = new AdaptiveBitrateController();
    controller.Initialize(10000);

    // Normal startup: moderate FPS, no loss
    var feedback = new QualityFeedbackMessage
    {
        EffectiveFps = 25f,   // 42% of target -- below 50% but above 30%
        TargetFps = 60f,
        PacketLossRate = 0f,
        BufferStatus = "healthy",
    };

    var decision = controller.ProcessFeedback(feedback);

    Assert.False(decision.Changed);  // Still in warmup, not emergency
}
```

### Risk Assessment
- **Low risk.** The emergency bypass only fires under extreme conditions that cannot be
  explained by normal warmup behavior.
- The existing critical FPS check (line 234-244) already bypasses warmup for `hasActualProblems`
  cases. This adds coverage for the gap where problems exist but `hasActualProblems` is false.
