# Code Review: Phase 02 WPF GUI Changes

**Date:** 2026-03-28
**Focus:** Threading safety, event leak risks, proper cleanup, Security, YAGNI/KISS/DRY

---

## Code Review Summary

### Scope
- Files reviewed: 11 files (Models/ServerState.cs, Models/ClientConnectionInfo.cs, Models/MonitorInfo.cs, Services/ServerService.cs, Core/Logger.cs, Application/Protocol/PhaseProtocolHandler.cs, Application/Protocol/PhaseProtocolHandler.Messaging.cs, Server/QRCodeUtil.cs, App.xaml.cs, ViewModels/MainViewModel.cs, ViewModels/DashboardViewModel.cs)
- Lines of code analyzed: ~850
- Review focus: Phase 02 WPF GUI integration changes

### Overall Assessment
Solid implementation with good separation of concerns. `Dispatch()` helper is clean, `ServerService` extraction is well-scoped. Several threading and cleanup issues require attention — none are critical but two are High priority.

---

## Critical Issues

None.

---

## High Priority Findings

### H1 — Static events on `PhaseProtocolHandler` never unsubscribed → memory/event leak

**Files:** `PhaseProtocolHandler.cs` L56-58, `DashboardViewModel.cs` L19-23

Static events (`OnClientConnected`, `OnClientDisconnected`, `OnClientPhaseChanged`) hold references to subscribers for the lifetime of the AppDomain. Any subscriber (e.g. `ConnectionManagerViewModel` or future VM) that subscribes without unsubscribing will leak.

`DashboardViewModel` subscribes to `_serverService.State.PropertyChanged` (instance event, lower risk since State lives as long as VM), but the static events are the bigger concern.

**Fix:** Subscribers must unsubscribe in `Dispose()` or implement `IDisposable`. For static events on a handler that is created/destroyed per client, consider replacing statics with a singleton `ClientRegistry` service passed via constructor — removes the static coupling entirely and makes testing viable.

### H2 — `StopAsync().GetAwaiter().GetResult()` on UI thread in `OnExit`

**File:** `App.xaml.cs` L39

`OnExit` runs on the UI thread. `StopAsync` calls `Dispatch()` internally (`State.StatusMessage = "Shutting down..."` etc.), which checks `dispatcher.CheckAccess()`. Since we're already on the UI thread, `Dispatch()` calls `action()` directly — fine. However `StopAsync` also `await`s `_server.StopAsync()` and `SignalServer.ForceCleanupResources()`. Blocking the UI thread on `.GetAwaiter().GetResult()` while those awaits may internally `Invoke` back to the dispatcher risks a deadlock if any internal code uses `dispatcher.Invoke` (blocking) rather than `InvokeAsync`.

**Impact:** Intermittent hang/deadlock on shutdown, depending on what `SignalServer.StopAsync` does internally.

**Fix:**
```csharp
// Option A: Use async void OnExit (acceptable for app lifecycle)
protected override async void OnExit(System.Windows.ExitEventArgs e)
{
    if (_serverService != null)
    {
        await _serverService.StopAsync();
        _serverService.Dispose();
    }
    ...
    base.OnExit(e);
}
```
Or ensure `StopAsync` never calls `dispatcher.Invoke` (blocking) during shutdown — use `InvokeAsync` throughout.

---

## Medium Priority Improvements

### M1 — `BuildQrData()` hand-rolls JSON with string interpolation

**File:** `ServerService.cs` L322-328

Manual JSON construction is fragile if IP strings ever contain characters needing escaping (unlikely for IPs but `TunnelUrl` from Cloudflare is a real string).

```csharp
// Current — fragile
string qrData = $"{{\"ip\":\"{State.LocalIp}\",\"port\":\"{State.Port}\"{usbIPJson}{tunnelJson}}}";

// Fix — use JsonSerializer or anonymous type
var obj = new { ip = State.LocalIp, port = State.Port.ToString(),
                usbIP = State.UsbTetheringIp, tunnelUrl = State.TunnelUrl };
var qrData = JsonSerializer.Serialize(obj, new JsonSerializerOptions
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
```

### M2 — `ServerService.Dispose()` does not call `StopAsync`

**File:** `ServerService.cs` L374-381

`Dispose()` only disposes `_discovery` and `_tunnel`, but not `_server` (SignalServer). If `Dispose()` is called without a prior `StopAsync()`, the HTTP listener leaks. The pattern `StopAsync()` + `Dispose()` is enforced by `App.xaml.cs` today, but `Dispose()` should be safe to call standalone.

**Fix:** Add null-safe server stop in `Dispose()`:
```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _server?.Dispose(); // or a sync Stop if available
    _discovery?.Dispose();
    _tunnel?.Dispose();
}
```

### M3 — `DashboardViewModel` re-implements status sync that `ServerState` already provides

**File:** `DashboardViewModel.cs` L14, L19-23

`_statusText` duplicates `State.StatusMessage`. The binding could point directly to `State.StatusMessage`. The `PropertyChanged` subscription is extra wiring that can go stale.

```csharp
// YAGNI/DRY: remove StatusText, bind XAML to State.StatusMessage directly
public ServerState State => _serverService.State;
// Done — no extra property needed
```

### M4 — `Logger.OnLogEntry` swallows all exceptions silently

**File:** `Logger.cs` L104

```csharp
try { OnLogEntry?.Invoke(now, message, level); } catch { }
```

Silent `catch {}` hides subscriber bugs permanently (e.g., a UI handler that throws a `NullReferenceException` after partial teardown). At minimum log to `Console` or `Debug.WriteLine` inside the catch during DEBUG builds.

### M5 — `DetectEncoder()` loads DLLs via P/Invoke at startup without checking process bitness

**File:** `ServerService.cs` L167-199

`LoadLibrary("amfrt64.dll")` — if the process ever runs as x86 (unlikely but possible), this silently fails. Not blocking, but a comment or guard would clarify intent. Also, loaded handles are freed immediately — pattern is correct, but unusual enough to warrant a comment.

---

## Low Priority Suggestions

### L1 — `ServerState._authToken` observable but sensitive

**File:** `Models/ServerState.cs` L17

`AuthToken` is marked `[ObservableProperty]` — it will appear in XAML bindings and potentially in ViewModel state dumps. Ensure it is never logged or bound to a visible text element without masking. Not a current bug, just a risk surface to track.

### L2 — `MonitorInfo` could be a `record`

**File:** `Models/MonitorInfo.cs`

All properties are `init`-only, no change notification needed. `record` gives value equality and a tidy `ToString()` for free.

```csharp
public record MonitorInfo(string Name, int Width, int Height, bool IsVirtual);
```

### L3 — `PngByteQRCode` not disposed in `QRCodeUtil.GenerateImageSource`

**File:** `Server/QRCodeUtil.cs` L43

`PngByteQRCode` is not `using`-wrapped. Check if it implements `IDisposable`; if so, add `using`.

### L4 — `CheckVpxEncoderAvailable` called twice per negotiation (VP8 + VP9)

**File:** `PhaseProtocolHandler.Messaging.cs` L130-134

Each call invokes `avcodec_find_encoder_by_name` via unsafe FFmpeg interop. Low cost, but could be computed once and cached per instance if codec negotiation is called more than once per client.

---

## Positive Observations

- `Dispatch()` helper (ServerService.cs L365-372) is clean: checks `CheckAccess()` correctly, no `InvokeAsync` vs `Invoke` confusion for sync actions.
- `Logger.OnLogEntry` wrapped in try/catch — correct defensive pattern, just needs minimal logging of errors.
- `ConcurrentDictionary` for `ActiveClients` is the right choice for multi-client concurrency.
- `image.Freeze()` in `QRCodeUtil.GenerateImageSource` — correct WPF cross-thread image handling.
- `BitmapCacheOption.OnLoad` + stream disposed after `EndInit()` — no stream lifetime bug.
- `_sendLock` (SemaphoreSlim) prevents concurrent WebSocket sends — correct.
- Null guard pattern on `Application.Current?.Dispatcher` in `Dispatch()` handles app teardown race.
- Exception handlers in `App.xaml.cs` cover all three channels (AppDomain, TaskScheduler, Dispatcher) — thorough.

---

## Recommended Actions

1. **(H1)** Decide: singleton `ClientRegistry` service OR document that all static event subscribers must unsubscribe. Add `IDisposable` to any subscriber VM that hooks static events.
2. **(H2)** Change `OnExit` to `async void` or audit `StopAsync` to use `InvokeAsync` throughout to eliminate deadlock risk.
3. **(M1)** Replace hand-rolled JSON in `BuildQrData()` with `JsonSerializer`.
4. **(M2)** Add `_server?.Dispose()` (or stop) inside `ServerService.Dispose()`.
5. **(M3)** Remove `_statusText` from `DashboardViewModel`, bind XAML directly to `State.StatusMessage`.
6. **(M4)** In `catch {}` of `OnLogEntry` invocation, add `Console.Error.WriteLine` or `#if DEBUG` output.
7. **(L3)** Wrap `PngByteQRCode` in `using` if `IDisposable`.
8. **(L2)** Convert `MonitorInfo` to a `record`.

---

## Metrics

- Type Coverage: nullable enabled across all reviewed files — good
- Linting Issues: ~0 structural issues; see M3 for minor redundancy
- Security: AuthToken risk surface noted (L1), no active vulnerability

---

## Unresolved Questions

- Does `SignalServer.StopAsync()` or `ForceCleanupResources()` internally call `Dispatcher.Invoke` (blocking)? If yes, H2 is an active deadlock on every clean shutdown.
- Is `ConnectionManagerViewModel` (not reviewed) subscribing to the static events on `PhaseProtocolHandler`? If so, H1 applies immediately.
