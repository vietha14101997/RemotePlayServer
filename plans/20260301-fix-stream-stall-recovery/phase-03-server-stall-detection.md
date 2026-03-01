# Phase 03: Server-Side Stall Detection

## Bug Analysis

**File:** `Application/Protocol/PhaseProtocolHandler.Phase3.cs`

The server has NO proactive monitoring of stream health. It only reacts when the client sends
`quality_feedback` or `fps_feedback`. If the client stalls (frozen decoder, WiFi congestion
preventing feedback messages from arriving), the server continues pumping frames at full bitrate,
worsening congestion.

The existing keep-alive (line 523-578) detects WebSocket disconnection (no pong for 6s) but does
NOT detect stream stalls where the WebSocket is alive but no feedback arrives.

## Root Cause

No mechanism to detect "client is alive (pongs arrive) but has stopped sending feedback."

Client sends `quality_feedback` every ~2s and `fps_feedback` every ~2s. If neither arrives
for >3s during active streaming, the client is likely stalled.

## Fix: Track Last Feedback Time, Auto-Reduce on Gap

### New Field in `PhaseProtocolHandler.cs`

Add one field to the partial class:

**File:** `Application/Protocol/PhaseProtocolHandler.cs` (add near line 119, after `_missedPongs`)

```csharp
// Stall detection: track last client feedback for proactive recovery
private DateTime _lastClientFeedback = DateTime.UtcNow;
```

### Change 1: Update Timestamp on Feedback Receipt

**File:** `Application/Protocol/PhaseProtocolHandler.Phase3.cs`

In the `fps_feedback` handler (line 288-315), add timestamp update after parsing:

```csharp
// Line ~293, after: if (feedback != null && _streamer != null)
// ADD:
_lastClientFeedback = DateTime.UtcNow;
```

In the `quality_feedback` handler (line 317-345), add timestamp update after parsing:

```csharp
// Line ~323, after: if (feedback != null && _streamer != null)
// ADD:
_lastClientFeedback = DateTime.UtcNow;
```

### Change 2: Add Stall Detection Timer

Add a new method and integrate it into Phase 3 startup.

**New method** (add after `StopKeepAlive()` at line 592):

```csharp
// Stall detection fields
private System.Timers.Timer? _stallDetectTimer;
private const int STALL_CHECK_INTERVAL_MS = 1000;  // Check every 1s
private const int STALL_THRESHOLD_MS = 3000;        // 3s without feedback = stall

/// <summary>
/// Start server-side stall detection.
/// If no quality_feedback or fps_feedback arrives for 3s during streaming,
/// proactively reduce bitrate and send keyframe burst.
/// </summary>
private void StartStallDetection()
{
    _lastClientFeedback = DateTime.UtcNow;

    _stallDetectTimer = new System.Timers.Timer(STALL_CHECK_INTERVAL_MS);
    _stallDetectTimer.Elapsed += (s, e) =>
    {
        try
        {
            if (_ws.State != WebSocketState.Open || _streamer == null)
            {
                _stallDetectTimer?.Stop();
                return;
            }

            var timeSinceLastFeedback = (DateTime.UtcNow - _lastClientFeedback).TotalMilliseconds;
            if (timeSinceLastFeedback > STALL_THRESHOLD_MS)
            {
                Logger.Info($"[StallDetect] No feedback for {timeSinceLastFeedback / 1000:F1}s → reducing bitrate 30% + keyframe burst");

                // Reduce bitrate by 30% via synthetic critical QualityFeedback
                var (_, currentFps, currentBitrate, _) = _streamer.GetCurrentConfig();
                int reducedBitrate = Math.Max(2000, (int)(currentBitrate * 0.7));

                var stallFeedback = new QualityFeedbackMessage
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    EffectiveFps = 0f,         // worst case assumption
                    TargetFps = currentFps,
                    PacketLossRate = 0.5f,     // assume heavy loss
                    AvgPacketLossRate = 0.5f,
                    BufferStatus = "starving",
                    RttMs = 0,
                    JitterMs = 0,
                    ConnectionHealth = 1,
                    IsWiFi = !_isUsbTransport,
                };

                _streamer.ProcessQualityFeedback(stallFeedback);

                // Keyframe burst on all monitors for visual recovery
                _streamer.RequestKeyframeBurst(-1, 3);

                // Reset timer so we don't spam reductions every 1s
                // Next stall detect fires only after another full STALL_THRESHOLD_MS gap
                _lastClientFeedback = DateTime.UtcNow;

                Logger.Info($"[StallDetect] Applied: bitrate target reduced, keyframe burst sent");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[StallDetect] Error: {ex.Message}");
        }
    };
    _stallDetectTimer.AutoReset = true;
    _stallDetectTimer.Start();
    Logger.Info("[StallDetect] Server-side stall detection started (3s threshold)");
}

/// <summary>
/// Stop stall detection timer.
/// </summary>
private void StopStallDetection()
{
    try
    {
        _stallDetectTimer?.Stop();
        _stallDetectTimer?.Dispose();
        _stallDetectTimer = null;
    }
    catch { }
}
```

### Change 3: Wire Into Phase 3 Lifecycle

**Start stall detection** -- add after `StartKeepAlive()` call (line 80):

```csharp
// Start server-side keep-alive for early disconnect detection
StartKeepAlive();

// Start server-side stall detection for proactive recovery
StartStallDetection();
```

**Stop stall detection** -- add after `StopKeepAlive()` call (line 390):

```csharp
// Stop keep-alive timer
StopKeepAlive();

// Stop stall detection timer
StopStallDetection();
```

### Stall Detection Flow

```
Client stops sending feedback (frozen/congested)
    |
    v  (1s timer tick)
StallDetect checks: DateTime.UtcNow - _lastClientFeedback > 3s?
    |
    v  YES
1. Log "[StallDetect] No feedback for Xs"
2. Create synthetic QualityFeedback(fps=0, loss=50%, starving)
3. Route through ProcessQualityFeedback() → AdaptiveBitrateController
   → controller reduces bitrate (bypasses warmup via Phase 02 emergency)
4. RequestKeyframeBurst(-1, 3) → all monitors get keyframe burst
5. Reset _lastClientFeedback to prevent repeated triggers
    |
    v  (client recovers, sends feedback)
_lastClientFeedback updated → stall timer resets naturally
```

### Why Synthetic QualityFeedback Instead of Direct Bitrate Set

- `ProcessQualityFeedback()` respects cooldown, EWMA, and min/max bounds
- It applies bitrate to ALL track encoders (multi-monitor safe)
- It returns a `BitrateAdjustedMessage` that could be sent to client (optional)
- No need to duplicate encoder iteration logic

### Why 3s Threshold

| Threshold | Behavior |
|-----------|----------|
| 1s | Too aggressive -- normal feedback gaps during brief WiFi hiccups |
| 2s | Marginal -- client sends quality_feedback every ~2s, small jitter causes false positive |
| 3s | Safe -- client should send at least 1 feedback per 2s cycle. Missing 1.5 cycles = real problem |
| 5s | Too slow -- stall is already visible to user for several seconds |

### Why Reset _lastClientFeedback After Action

Without reset, the timer fires every 1s and keeps reducing bitrate every second until feedback
resumes. The reset creates a 3s cooldown between stall actions, giving the reduced bitrate
time to take effect before further reduction.

### Affected Files Summary

| File | Change |
|------|--------|
| `PhaseProtocolHandler.cs` | +1 field: `_lastClientFeedback` |
| `PhaseProtocolHandler.Phase3.cs` | +2 timestamp updates in feedback handlers |
| `PhaseProtocolHandler.Phase3.cs` | +2 method calls: `StartStallDetection()`, `StopStallDetection()` |
| `PhaseProtocolHandler.Phase3.cs` | +2 new methods: `StartStallDetection()`, `StopStallDetection()` |

### Risk Assessment
- **Medium risk.** New timer runs independently from main message loop.
- Thread safety: `_lastClientFeedback` is read/written from timer thread and message loop.
  `DateTime` assignment is atomic on 64-bit runtime (.NET 9 is always 64-bit), so no lock needed.
- The synthetic feedback with `PacketLossRate = 0.5f` will trigger the packet loss bypass
  (line 226-230 in `AdaptiveBitrateController`) which is NOT blocked by warmup. This ensures
  stall detection works even during the first 15 seconds.
- Multi-monitor safe: keyframe burst uses `-1` (all monitors), bitrate change applies to all tracks.

### Edge Case: Client Reconnect Offer

If the client sends a reconnect `offer` (line 199-209), it won't update `_lastClientFeedback`.
This is intentional -- reconnect offers indicate the client is trying to recover but is NOT
successfully streaming yet. Stall detection should remain active during reconnection.
