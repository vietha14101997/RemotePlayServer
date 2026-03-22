# Phase 5: GC Pressure - ArrayPool in C# Callbacks

## Context

All 3 C# wrappers allocate a `new byte[size]` in every callback invocation:
- `AmfNativeWrapper.cs` line 472: `byte[] nalData = new byte[size];`
- `NvencNativeWrapper.cs` line 441: `byte[] nalData = new byte[size];`
- `QsvNativeWrapper.cs` line 328: `byte[] nalData = new byte[size];`

At 30-60fps per track, with 2-3 tracks, this creates 60-180 allocations/second of 10-500KB arrays. These go straight to LOH (Large Object Heap) for frames >85KB, causing Gen2 GC pressure and occasional pauses.

## Key Insights

### Data Lifetime Analysis

**Critical question**: Is `nalData` consumed synchronously or held asynchronously after `OnEncodedData?.Invoke()`?

**Answer: Mixed -- consumed within callback chain but stored temporarily.**

From `SIPSorceryStreamer.FrameSending.cs` line 292-371:
1. `OnEncodedData(TrackInfo track, byte[] nalData, ...)` is called synchronously from within the native callback
2. The method performs checks and then either:
   - Drops the frame (returns early) -- nalData is not held
   - Calls `SendH265IdrViaDataChannel(track, nalData)` -- sends via SCTP DataChannel (synchronous write to buffer)
   - Calls `SendFrameImmediate(...)` -- sends via RTP (synchronous)
3. After the OnEncodedData handler returns, nalData is NOT referenced anywhere

**Conclusion: nalData lifetime is scoped to the callback invocation.** Safe to use ArrayPool rent/return pattern.

### ArrayPool Strategy

Since all consumption is synchronous within the callback:
1. `Rent(size)` from `ArrayPool<byte>.Shared`
2. `Marshal.Copy` into rented buffer
3. Fire `OnEncodedData` with rented buffer + actual size
4. `Return` buffer after event returns

**BUT**: The event signature is `Action<byte[], bool, long>` -- consumers receive `byte[]` and may use `.Length` to determine data size. With ArrayPool, rented buffer `.Length >= size` (may be larger). Need to change event signature or pass size separately.

### Solution Options

**Option A**: Change event signature to `Action<ArraySegment<byte>, bool, long>` or `Action<ReadOnlyMemory<byte>, bool, long>`
- Pros: Clean, communicates exact slice
- Cons: Breaking change to all consumers (SIPSorceryStreamer, any other subscribers)

**Option B**: Keep `byte[]` signature but ensure exact-size buffer (copy to exact-size array)
- Pros: No breaking changes
- Cons: Still allocates, just from pool (but pool reuse reduces GC pressure)

**Option C**: Keep `new byte[size]` but use a pooled approach with explicit return
- Complex, error-prone

**Recommended: Option A with `ReadOnlyMemory<byte>`**.
- `ReadOnlyMemory<byte>` carries length information
- Consumers use `.Span` or `.ToArray()` only if they need to hold data
- All current consumers in FrameSending.cs pass the data synchronously to SIPSorcery SendRtp/DataChannel which accept `byte[]` or spans
- BUT: SIPSorcery API likely takes `byte[]` -> need `.ToArray()` at send point, defeating the purpose

**Revised recommendation: Option B (pragmatic)**
Keep `byte[]` event signature. Use ArrayPool + exact-size allocation:
```csharp
// ArrayPool.Shared.Rent may return larger buffer, but we copy exact size
byte[] nalData = ArrayPool<byte>.Shared.Rent((int)size);
try {
    Marshal.Copy(data, nalData, 0, (int)size);
    OnEncodedData?.Invoke(nalData, isKeyFrame != 0, pts);
} finally {
    ArrayPool<byte>.Shared.Return(nalData);
}
```

Wait -- this is UNSAFE if any consumer stores `nalData` reference beyond the callback. Let me re-verify...

From FrameSending.cs analysis:
- H265 IDR path: `SendH265IdrViaDataChannel(track, nalData)` -- sends data to DataChannel synchronously, SCTP copies internally
- RTP path: `SendFrameImmediate(...)` likely passes to SIPSorcery which copies to RTP packets synchronously
- No `Task.Run`, no `await`, no storing nalData in a field

**Confirmed safe.** But we must document that `nalData` from `OnEncodedData` must NOT be held after the event handler returns.

**HOWEVER** - there's a subtlety: `nalData` is passed through the `Action<byte[], bool, long>` delegate. If there are multiple subscribers and one stores the reference, we'd have a use-after-return bug. Currently only SIPSorceryStreamer subscribes (Lifecycle.cs line 106). Add a comment warning.

## Requirements

- Replace `new byte[size]` with `ArrayPool<byte>.Shared.Rent/Return` in all 3 callbacks
- Event handlers must NOT hold reference to nalData after returning
- Add XML doc warning on OnEncodedData event
- No signature change (keep `Action<byte[], bool, long>`)

## Architecture

```
NativeCallback(IntPtr data, uint size, ...)
  |
  +-- Rent from ArrayPool<byte>.Shared
  +-- Marshal.Copy(data, buffer, 0, size)
  +-- OnEncodedData?.Invoke(buffer[0..size], isKeyFrame, pts)
      |
      +-- SIPSorceryStreamer.OnEncodedData (synchronous consumption)
  +-- Return to ArrayPool<byte>.Shared
```

**Important**: Since buffer.Length may exceed `size`, consumers must use `size` parameter or we need to pass actual size. Current signature doesn't include size.

**Resolution**: All consumers already use `nalData.Length` for the data length. With ArrayPool, `nalData.Length >= size`. This means extra trailing bytes would be sent!

**Fix**: We MUST either:
1. Create exact-size copy from pool buffer (defeats purpose somewhat, but pool still helps with reuse)
2. Add size to event signature
3. Use `Memory<byte>` / `ArraySegment<byte>`

**Final approach**: Use `ArrayPool<byte>.Shared.Rent` + copy exact size to `new byte[size]` is pointless.

**Better approach**: Use a reusable buffer per encoder instance. Since callbacks are serialized by the encode mutex (one frame at a time per encoder):

```csharp
private byte[] _callbackBuffer = Array.Empty<byte>();

private void NativeCallback(IntPtr data, uint size, long pts, int isKeyFrame, IntPtr userData)
{
    if ((int)size > _callbackBuffer.Length)
        _callbackBuffer = new byte[(int)size * 2]; // grow with 2x headroom

    Marshal.Copy(data, _callbackBuffer, 0, (int)size);

    // Pass exact-size span via ArraySegment
    var segment = new ArraySegment<byte>(_callbackBuffer, 0, (int)size);
    OnEncodedData?.Invoke(segment, isKeyFrame != 0, pts);
}
```

But this requires changing event signature to `Action<ArraySegment<byte>, bool, long>`.

**FINAL DECISION**: Change event signature across the stack.

`IVideoEncoder.OnEncodedData`: `event Action<ArraySegment<byte>, bool, long>?`

Impact: `SIPSorceryStreamer.FrameSending.cs` `OnEncodedData` handler changes parameter type. All `nalData.Length` becomes `nalData.Count`. `nalData` (as ArraySegment) provides `.Array`, `.Offset`, `.Count`. SIPSorcery send APIs that need `byte[]` can use `nalData.Array` with offset/count or `.ToArray()`.

## Related Code Files

| File | Action |
|------|--------|
| `Core/Interfaces/IVideoEncoder.cs` | MODIFY: change event signature |
| `Infrastructure/Encoding/AmfNativeWrapper.cs` | MODIFY: reusable buffer + ArraySegment |
| `Infrastructure/Encoding/NvencNativeWrapper.cs` | MODIFY: reusable buffer + ArraySegment |
| `Infrastructure/Encoding/QsvNativeWrapper.cs` | MODIFY: reusable buffer + ArraySegment |
| `Application/Streaming/SIPSorceryStreamer.FrameSending.cs` | MODIFY: update handler signature |
| `Application/Streaming/SIPSorceryStreamer.Lifecycle.cs` | CHECK: lambda signature at line 106 |

Need to also check any other IVideoEncoder implementations:
- `Infrastructure/Encoding/LibAvEncoderAdapter.cs` (if exists)

## Implementation Steps

### Step 1: Update IVideoEncoder.cs
Change event from:
```csharp
event Action<byte[], bool, long>? OnEncodedData;
```
To:
```csharp
/// <summary>
/// Fired when encoded NAL data is available.
/// WARNING: The ArraySegment's backing array is reused between calls.
/// Consumers must NOT hold a reference to .Array after the handler returns.
/// Copy via .ToArray() if async retention is needed.
/// </summary>
event Action<ArraySegment<byte>, bool, long>? OnEncodedData;
```

### Step 2: Update all 3 NativeWrapper callbacks
For each wrapper, add a reusable buffer field and modify NativeCallback:
```csharp
private byte[] _callbackBuffer = Array.Empty<byte>();

private void NativeCallback(IntPtr data, uint size, long pts, int isKeyFrame, IntPtr userData)
{
    if (data == IntPtr.Zero || size == 0) return;
    try
    {
        int len = (int)size;
        if (len > _callbackBuffer.Length)
            _callbackBuffer = new byte[len]; // exact size, will be reused

        Marshal.Copy(data, _callbackBuffer, 0, len);
        OnEncodedData?.Invoke(new ArraySegment<byte>(_callbackBuffer, 0, len), isKeyFrame != 0, pts);
    }
    catch (Exception ex) { ... }
}
```

### Step 3: Update SIPSorceryStreamer.FrameSending.cs
Change `OnEncodedData(TrackInfo track, byte[] nalData, ...)` to `OnEncodedData(TrackInfo track, ArraySegment<byte> nalData, ...)`.
- Replace `nalData.Length` with `nalData.Count`
- Where byte[] is needed for SIPSorcery APIs, use `nalData.ToArray()` (only at actual send points)
- Or better: check if SIPSorcery accepts `ReadOnlySpan<byte>` or offset/length

### Step 4: Update Lifecycle.cs lambda
Line 106: `encoder.OnEncodedData += (nal, keyframe, pts) => OnEncodedData(track, nal, keyframe, pts);`
No change needed -- lambda parameter types are inferred.

### Step 5: Check LibAvEncoderAdapter
Update if it also implements IVideoEncoder.

### Step 6: Search for any other OnEncodedData subscribers
Grep for `OnEncodedData +=` across codebase.

## Todo

- [ ] Update `IVideoEncoder.OnEncodedData` signature to `Action<ArraySegment<byte>, bool, long>`
- [ ] Add reusable `_callbackBuffer` field to AmfNativeWrapper
- [ ] Modify AmfNativeWrapper.NativeCallback to use reusable buffer
- [ ] Add reusable `_callbackBuffer` field to NvencNativeWrapper
- [ ] Modify NvencNativeWrapper.NativeCallback to use reusable buffer
- [ ] Add reusable `_callbackBuffer` field to QsvNativeWrapper
- [ ] Modify QsvNativeWrapper.NativeCallback to use reusable buffer
- [ ] Update SIPSorceryStreamer.FrameSending.cs OnEncodedData handler
- [ ] Update any other IVideoEncoder implementations (LibAvEncoderAdapter)
- [ ] Grep for all `OnEncodedData` subscribers and update
- [ ] Verify build and test

## Success Criteria

- Zero per-frame `byte[]` allocations in encode callbacks
- Reusable buffer grows once to max frame size then stabilizes
- All send paths work correctly with ArraySegment
- No data corruption (buffer reuse is safe due to serialized callbacks)

## Risk Assessment

**Medium risk**. Breaking change to event signature affects all consumers. Mitigation: grep for all `OnEncodedData` references before implementation. The semantic change (buffer reuse) means any consumer that stores the `byte[]` reference will see corrupted data on next frame. Currently only SIPSorceryStreamer subscribes and consumes synchronously.

**Rollback**: If issues found, revert signature change. The old `new byte[size]` pattern works correctly, just with more GC pressure.

## Unresolved Questions

1. Does SIPSorcery `SendRtp` / DataChannel `Send` accept `ReadOnlySpan<byte>` or require `byte[]`? If it requires `byte[]`, we may need `.ToArray()` at send point, which re-allocates. Check SIPSorcery API.
2. Are there any other `OnEncodedData` subscribers beyond SIPSorceryStreamer? Need to grep.
3. LibAvEncoderAdapter -- does it also implement IVideoEncoder with same event?
