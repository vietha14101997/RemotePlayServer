# Phase 1: Capture Timestamp Passthrough

## Context Links
- Parent: [plan.md](./plan.md)
- Next: [phase-02-unified-rtp-clock.md](./phase-02-unified-rtp-clock.md)

## Overview

Wire the `captureTimestamp` (already produced by `PerMonitorCapture` from the barrier sync or `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`) through the call chain: `PhaseProtocolHandler` -> `SIPSorceryStreamer.PushTexture` / `PushBgraTexture`. Currently `PhaseProtocolHandler` receives `timestamp` in the lambda but discards it.

## Key Insights

1. `PerMonitorCapture` already emits `long captureTimestamp` as the 5th param in both `OnMonitorFrame` and `OnMonitorFrameBgra` events (lines 135, 142).
2. The barrier sets `_syncedTimestamp` via `Interlocked.Exchange` (line 418) so all monitors in a single frame share the same timestamp.
3. `PhaseProtocolHandler.StartCaptureThread()` binds these events (lines 1796-1838) but drops the timestamp when calling `_streamer?.PushTexture(...)` and `_streamer?.PushBgraTexture(...)`.
4. This is pure plumbing. No behavioral change until Phase 2 consumes the timestamp.

## Requirements

- `PushTexture` and `PushBgraTexture` accept a `long captureTimestampMs` parameter
- `PhaseProtocolHandler` passes `timestamp` from capture events to these methods
- Existing callers (if any outside PhaseProtocolHandler) pass `0` or `Environment.TickCount64` as default

## Architecture

```
PerMonitorCapture                PhaseProtocolHandler              SIPSorceryStreamer
  OnMonitorFrame(idx,tex,w,h,ts) --> lambda(idx,tex,w,h,ts) --> PushTexture(idx,tex,w,h,ts)
  OnMonitorFrameBgra(idx,tex,w,h,ts) --> lambda(idx,tex,w,h,ts) --> PushBgraTexture(idx,tex,w,h,ts)
```

No change to encoder interfaces. The timestamp is stored on `TrackInfo` for Phase 2 consumption.

## Related Code Files

| File | Lines | Role |
|------|-------|------|
| `Infrastructure/Capture/PerMonitorCapture.cs` | 135,142,418,506-525,682,699 | Produces `captureTimestamp` |
| `Application/Protocol/PhaseProtocolHandler.cs` | 1796-1838 | Event handlers that call Push methods |
| `Application/Streaming/SIPSorceryStreamer.cs` | 734-817 | `PushBgraTexture` and `PushTexture` methods |

## Implementation Steps

### Step 1: Add `captureTimestampMs` parameter to `PushBgraTexture`

File: `Application/Streaming/SIPSorceryStreamer.cs`, line 734.

**Before:**
```csharp
public void PushBgraTexture(int monitorIndex, ID3D11Texture2D bgraTexture, int width, int height)
```

**After:**
```csharp
public void PushBgraTexture(int monitorIndex, ID3D11Texture2D bgraTexture, int width, int height, long captureTimestampMs = 0)
```

No other changes inside the method body yet. The parameter is stored but unused until Phase 2.

### Step 2: Add `captureTimestampMs` parameter to `PushTexture`

File: `Application/Streaming/SIPSorceryStreamer.cs`, line 766.

**Before:**
```csharp
public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
```

**After:**
```csharp
public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height, long captureTimestampMs = 0)
```

### Step 3: Store `captureTimestampMs` on TrackInfo for encoder callback

Inside both `PushBgraTexture` and `PushTexture`, store the timestamp on the track before calling encode, so `OnEncodedData` can access it.

Add field to `TrackInfo` (line ~92):
```csharp
public long LastCaptureTimestampMs; // Set before encoding, read in OnEncodedData
```

In `PushBgraTexture` (before `track.Encoder.EncodeBgraTexture`):
```csharp
track.LastCaptureTimestampMs = captureTimestampMs;
```

In `PushTexture` (before `track.Encoder.EncodeTexture`):
```csharp
track.LastCaptureTimestampMs = captureTimestampMs;
```

This is safe because encoding is serialized under `lock (_lock)`.

### Step 4: Pass timestamp in PhaseProtocolHandler

File: `Application/Protocol/PhaseProtocolHandler.cs`, lines 1798 and 1830.

**NV12 handler (line 1798):**

Before:
```csharp
_streamer?.PushTexture(monitorIndex, nv12Texture, w, h);
```

After:
```csharp
_streamer?.PushTexture(monitorIndex, nv12Texture, w, h, timestamp);
```

**BGRA handler (line 1830):**

Before:
```csharp
_streamer?.PushBgraTexture(monitorIndex, textureToSend!, targetWidth, targetHeight);
```

After:
```csharp
_streamer?.PushBgraTexture(monitorIndex, textureToSend!, targetWidth, targetHeight, timestamp);
```

### Step 5: Pass timestamp into OnEncodedData

Modify the `OnEncodedData` method signature and the encoder callback wiring to include capture timestamp.

In `TryInitializeEncoderWithFallback` (line 509), the encoder callback is:
```csharp
encoder.OnEncodedData += (nal, keyframe, pts) => OnEncodedData(track, nal, keyframe, pts);
```

Change `OnEncodedData` to also read `track.LastCaptureTimestampMs`:

**Before (line 819):**
```csharp
private void OnEncodedData(TrackInfo track, byte[] nalData, bool isKeyframe, long pts100ns)
```

**After:**
```csharp
private void OnEncodedData(TrackInfo track, byte[] nalData, bool isKeyframe, long pts100ns)
```

No signature change needed -- `OnEncodedData` already has access to `track.LastCaptureTimestampMs` since `track` is passed by reference. Phase 2 will use it.

## Todo List

- [x] Add `captureTimestampMs` param to `PushBgraTexture` (default 0)
- [x] Add `captureTimestampMs` param to `PushTexture` (default 0)
- [x] Add `PendingCaptureTimestampMs` field to `TrackInfo` (named differently from plan; semantically equivalent)
- [x] Store capture timestamp on track before encoding in both Push methods
- [x] Pass `timestamp` in PhaseProtocolHandler NV12 handler (line 1798)
- [x] Pass `timestamp` in PhaseProtocolHandler BGRA handler (line 1830)
- [x] Verify build compiles with no errors (0 errors, 1 pre-existing warning)
- [ ] Verify streaming still works (runtime test pending)

## Success Criteria

1. Build succeeds with zero warnings related to the change
2. `captureTimestampMs` reaches `track.LastCaptureTimestampMs` -- verify with a one-time log in `OnEncodedData` showing the value is non-zero
3. No change in streaming behavior (this phase is plumbing only)

## Risk Assessment

- **Risk**: Very low. Default parameter value `= 0` means any missed call site compiles fine.
- **Rollback**: Revert the 4 parameter additions. No data structures or interfaces changed.
- **Testing**: Run single-monitor and multi-monitor streams, confirm frame delivery unchanged.
