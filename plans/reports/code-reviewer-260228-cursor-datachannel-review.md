# Code Review Summary

## Scope
- Files reviewed: 4
  - `RemotePlayServer/Application/Streaming/SIPSorceryStreamer.cs`
  - `RemotePlayServer/Application/Protocol/PhaseProtocolHandler.cs`
  - `VRWorkSpace/Assets/VR-Workspace/Scripts/Infrastructure/Streaming/PhaseProtocolClient.WebRTC.cs`
  - `VRWorkSpace/Assets/VR-Workspace/Scripts/Infrastructure/Streaming/PhaseProtocolClient.Cursor.cs`
- Lines of code analyzed: ~3,500 (targeted sections)
- Review focus: Cursor DataChannel implementation, binary format, thread safety, cleanup, UNOBSERVED_TASK_EXCEPTION fixes

---

## Overall Assessment

Implementation is functionally solid. Binary protocol is consistent between server and client. The main concerns are **a race condition on the shared `_cursorBuffer`**, **endianness risk on Android**, and **exception swallowing** in the cursor tracking loop. The UnobservedTaskException fix in `CloseConnection` is a pragmatic workaround but has correctness trade-offs.

---

## Critical Issues

### 1. RACE CONDITION: `_cursorBuffer` is shared, `dc.send()` may be async

**File:** `SIPSorceryStreamer.cs` lines 69, 75-87

```csharp
// Shared instance - NOT thread-safe
private readonly byte[] _cursorBuffer = new byte[19];

public void SendCursorPosition(...)
{
    // Writes to shared buffer...
    _cursorBuffer[0] = 1;
    BitConverter.TryWriteBytes(_cursorBuffer.AsSpan(2, 4), u);
    // ...
    dc.send(_cursorBuffer); // Does SIPSorcery copy this before returning?
}
```

**Problem:** `SendCursorPosition` is called from the cursor tracking `Task.Run` thread. If `dc.send()` internally queues the buffer reference (does not copy immediately), a second call before the first send completes would corrupt in-flight data. Additionally, if `CloseConnection` is called concurrently (nulls `_cursorDc`) while this method executes, the snapshot `var dc = _cursorDc` is safe (volatile read), but the buffer write itself is unprotected from concurrent calls.

**Risk level:** Medium-to-High. Depends on SIPSorcery's `RTCDataChannel.send()` internals. If it copies the span synchronously into its SCTP send buffer before returning, this is safe. If it does not, data corruption occurs at high cursor update rates (~120Hz).

**Fix:**
```csharp
// Option A: stack-allocate per call (zero allocation on stack for 19 bytes)
public void SendCursorPosition(int monitorIndex, float u, float v, bool visible, int cursorType, long cursorId)
{
    var dc = _cursorDc;
    if (dc?.readyState != RTCDataChannelState.open) return;

    Span<byte> buf = stackalloc byte[19];
    buf[0] = 1;
    buf[1] = (byte)monitorIndex;
    BitConverter.TryWriteBytes(buf.Slice(2, 4), u);
    BitConverter.TryWriteBytes(buf.Slice(6, 4), v);
    buf[10] = (byte)((visible ? 1 : 0) | ((cursorType & 0x0F) << 1));
    BitConverter.TryWriteBytes(buf.Slice(11, 8), cursorId);

    dc.send(buf.ToArray()); // or dc.send(buf) if SIPSorcery accepts Span
}
```
If `dc.send()` does not accept `Span<byte>`, `stackalloc` + `ToArray()` still eliminates the shared-state bug at the cost of one 19-byte heap allocation per call (negligible at 120Hz).

---

## High Priority Findings

### 2. ENDIANNESS: `BitConverter` on Android

**Files:** `SIPSorceryStreamer.cs` (server encode) + `PhaseProtocolClient.Cursor.cs` (client decode)

**Server (Windows .NET):**
```csharp
BitConverter.TryWriteBytes(_cursorBuffer.AsSpan(2, 4), u);   // little-endian on Windows
BitConverter.TryWriteBytes(_cursorBuffer.AsSpan(11, 8), cursorId);
```

**Client (Unity Android):**
```csharp
float u = BitConverter.ToSingle(data, 2);   // uses system endianness
long cursorId = BitConverter.ToInt64(data, 11);
```

**Problem:** `BitConverter` uses the **host machine's byte order**. Windows x86/x64 is little-endian. Android ARM is also little-endian in practice, so this works today. However, it is architecturally fragile - there is no explicit endianness contract. If Unity ever runs on a big-endian target (rare, but possible), values will be silently wrong.

**Recommended fix:** Use `System.Buffers.Binary.BinaryPrimitives` with an explicit endianness:
```csharp
// Server (encode):
BinaryPrimitives.WriteSingleLittleEndian(_cursorBuffer.AsSpan(2, 4), u);
BinaryPrimitives.WriteInt64LittleEndian(_cursorBuffer.AsSpan(11, 8), cursorId);

// Client (decode):
float u = BinaryPrimitives.ReadSingleLittleEndian(new ReadOnlySpan<byte>(data, 2, 4));
long cursorId = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(data, 11, 8));
```
`BinaryPrimitives` is available in Unity via `System.Memory` package (already present if UniTask is in use).

### 3. `CloseConnection` blocks the calling thread with `Thread.Sleep(100)` + double `GC.Collect()`

**File:** `SIPSorceryStreamer.cs` lines 1743-1753

```csharp
Thread.Sleep(100);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
GC.WaitForPendingFinalizers();
```

**Problems:**
- `CloseConnection` is called from `ProcessOfferAsync` (on a caller thread) and from `Stop()`. Both block 100ms+ plus GC pause.
- `GC.Collect()` forces a full GC which can cause multi-frame hitches in a streaming server.
- The comment says this prevents `UnobservedTaskException` from SIPSorcery's internal socket tasks. This is a valid workaround but masks the root cause.

**Better approach:** Register a global `TaskScheduler.UnobservedTaskException` handler once at startup to log-and-swallow these without forcing GC:
```csharp
// In program startup:
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Logger.Warn($"[GC] Unobserved task exception (suppressed): {e.Exception.Message}");
    e.SetObserved(); // Prevents process crash, no GC needed
};
```
This allows removing the `Thread.Sleep` + double `GC.Collect` from `CloseConnection`, making disconnects instant.

### 4. `ondatachannel` handler assigns `_cursorDc` without synchronization

**File:** `SIPSorceryStreamer.cs` lines 336-352

```csharp
_pc.ondatachannel += (dc) =>
{
    if (dc.label == "cursor")
    {
        _cursorDc = dc;  // written from SIPSorcery callback thread
        _cursorDc.onopen += ...;
        _cursorDc.onclose += () => { ... _cursorDc = null; };
    }
};
```

`_cursorDc` is a plain field (not `volatile`). The `SendCursorPosition` caller reads it from a different thread. In .NET the JIT can cache the field read in a register, potentially reading a stale null or stale reference.

Compare: `_running`, `_disposed`, `_connected` are all `volatile` (line 40-44). `_cursorDc` and `_audioDc` are not.

**Fix:**
```csharp
private volatile RTCDataChannel? _cursorDc;
private volatile RTCDataChannel? _audioDc;
```

---

## Medium Priority Improvements

### 5. Exception swallowed silently in cursor tracking loop

**File:** `PhaseProtocolHandler.cs` line 3101

```csharp
catch (Exception) { }  // Silent swallow
```

This hides errors in UV calculation, `ConvertDxgiCursorToRgba`, `SendMessageAsync`, or `_streamer.SendCursorPosition`. A production bug in any of these will produce zero diagnostic output.

**Fix:**
```csharp
catch (OperationCanceledException) { break; }
catch (Exception ex)
{
    Logger.Warn($"[Protocol] Cursor tracking error (non-fatal): {ex.Message}");
    await Task.Delay(POLL_INTERVAL_MS, ct); // throttle on error
}
```

### 6. `HandleDxgiCursorUpdate` stores buffer reference, not a copy

**File:** `PhaseProtocolHandler.cs` lines 2945-2954

```csharp
private void HandleDxgiCursorUpdate(int monitorIndex, byte[] buffer, ...)
{
    lock (_dxgiCursorLock)
    {
        _pendingDxgiCursorBuffer = buffer;  // stores caller's reference
        ...
    }
}
```

If `PerMonitorCapture` reuses the same `buffer` array for the next frame (pool/ring buffer pattern), the cursor tracking task will read corrupted data after the lock is released. Without seeing `PerMonitorCapture`'s implementation, this is uncertain - but worth verifying. If it does reuse buffers, a copy is required:

```csharp
_pendingDxgiCursorBuffer = buffer.ToArray(); // defensive copy
```

### 7. `_sentCursorIds` accessed from both cursor tracking task and `StopCursorTracking` without sync

**File:** `PhaseProtocolHandler.cs` lines 2968, 3034, 3064, 3112

- Line 2968: `_sentCursorIds.Clear()` — called from startup code (presumably main thread)
- Line 3034: `.Contains(shapeId)` — called from cursor tracking Task.Run
- Line 3064: `.Add(shapeId)` — called from cursor tracking Task.Run
- Line 3112: `.Clear()` — called from `StopCursorTracking()` (caller's thread)

`HashSet<long>` is not thread-safe. Concurrent `Clear()` + `Contains()` is undefined behavior.

**Fix:** Use `ConcurrentDictionary<long, byte>` as a set, or guard all accesses with the existing `_dxgiCursorLock`, or since the cursor tracking task is the only reader/writer during operation, ensure `StopCursorTracking` cancels and awaits the task before calling `Clear()`.

### 8. `HandleCursorFromDataChannel` called on WebRTC thread - captures `data` reference in closure

**File:** `PhaseProtocolClient.Cursor.cs` lines 13-31

```csharp
private void HandleCursorFromDataChannel(byte[] data)
{
    // data is parsed immediately (safe)
    float u = BitConverter.ToSingle(data, 2);
    ...
    VRWorkspace.Core.MainThreadDispatcher.Enqueue(() =>
    {
        OnCursorPosition?.Invoke(monitorIndex, u, v, visible, cursorType, cursorId);
    });
}
```

The closure captures `monitorIndex`, `u`, `v`, `visible`, `cursorType`, `cursorId` - all value types. This is correct - no reference to `data` escapes. No issue here.

### 9. `PreWarmDtls` forces GC + Thread.Sleep on static call

**File:** `SIPSorceryStreamer.cs` lines 188-207

Same pattern as `CloseConnection` - `Thread.Sleep(50)` + `GC.Collect` + `WaitForPendingFinalizers`. If the global `UnobservedTaskException` handler (recommendation #3) is added, the warmup GC can also be removed, making startup faster.

---

## Low Priority Suggestions

### 10. `_cursorBuffer` write is non-atomic: stale read between writes

Even if `dc.send()` copies synchronously, the sequence of writes to `_cursorBuffer` (lines 80-85) followed by `dc.send()` are not atomic. A theoretical scenario: cursor tracking task is preempted between writing bytes 0-9 and bytes 10-18 by another caller of `SendCursorPosition`. In practice this cannot happen because only one task calls this method, but the design has implicit single-caller assumption not expressed in code.

### 11. `monitorIndex` not bounds-checked before cast to `byte`

**File:** `SIPSorceryStreamer.cs` line 81

```csharp
_cursorBuffer[1] = (byte)monitorIndex;
```

If `monitorIndex` > 255 (very unlikely but theoretically possible in a future multi-monitor config), this silently truncates. Add a guard or `Debug.Assert(monitorIndex <= 255)`.

### 12. Duplicate code: `HandleCursorImage` and `HandleCursorImageRaw` share ~80% logic

**File:** `PhaseProtocolClient.Cursor.cs` lines 139-247 vs 253-364

Both functions: decode base64, validate size, create Texture2D, flip rows, zero transparent pixels, invoke event. The difference is JSON parsing approach. This could be refactored into a shared `CreateCursorTexture(long cursorId, CursorType cursorType, int width, int height, int hotspotX, int hotspotY, string imageBase64)` method. Not urgent but increases maintenance surface.

---

## Positive Observations

- Binary format is well-documented in comments (both sides agree on layout).
- `var dc = _cursorDc` snapshot before use (line 77) correctly avoids null reference after concurrent null assignment.
- `DataChannel` fallback to WebSocket is clean - `HasCursorChannel` property provides a clear capability check.
- `HandleCursorFromDataChannel` correctly parses all primitive values before the `MainThreadDispatcher.Enqueue` closure, avoiding any buffer capture.
- `CloseConnection` explicitly nulls `_cursorDc` after `close()` to prevent double-close.
- DXGI cursor data is copied under lock before processing - correct producer/consumer pattern.
- Cursor image size mismatch handling is defensive and handles corrupt data gracefully.

---

## Recommended Actions (Prioritized)

1. **[Critical] Add `volatile` to `_cursorDc` and `_audioDc`** - one-word fix, eliminates visibility bug.
2. **[Critical] Eliminate shared `_cursorBuffer`** - use `stackalloc byte[19]` + `ToArray()` per call to remove race.
3. **[High] Replace `BitConverter` with `BinaryPrimitives.{Read,Write}*LittleEndian`** - explicit endianness contract on both server and Unity client.
4. **[High] Add global `TaskScheduler.UnobservedTaskException` handler** - remove `Thread.Sleep` + double `GC.Collect` from `CloseConnection` and `PreWarmDtls`.
5. **[Medium] Fix silent exception swallow in cursor loop** - add `Logger.Warn` minimum.
6. **[Medium] Verify `PerMonitorCapture` buffer ownership** - ensure `HandleDxgiCursorUpdate` receives a fresh array per call or add `.ToArray()` copy.
7. **[Medium] Fix `_sentCursorIds` thread safety** - use `ConcurrentDictionary` or ensure `StopCursorTracking` awaits task completion before `Clear()`.

---

## Metrics
- Type Coverage: N/A (no typecheck run - cross-repo review)
- Test Coverage: Not assessed
- Linting Issues: 0 style, 4 medium, 2 high, 1 critical (race)

---

## Unresolved Questions

1. Does `SIPSorcery RTCDataChannel.send(byte[])` copy the array synchronously before returning, or does it hold the reference? This determines the actual severity of issue #1 and #10.
2. Does `PerMonitorCapture.OnCursorUpdate` pass a freshly allocated `byte[]` per event, or reuse a pooled buffer? This determines whether issue #6 is a real bug.
3. Is `MainThreadDispatcher.Enqueue` in Unity backed by a concurrent queue? If not, the enqueue call from the WebRTC thread could race with the main thread draining the queue.
