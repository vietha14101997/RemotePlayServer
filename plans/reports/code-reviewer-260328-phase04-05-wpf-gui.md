# Code Review: Phase 04+05 WPF GUI Changes

**Date:** 2026-03-28
**Focus:** Threading safety, event leak/cleanup, XAML binding correctness, schtasks security

---

## Code Review Summary

### Scope
- Files reviewed: 7 (SettingsViewModel.cs, SettingsView.xaml, SettingsView.xaml.cs, InternetManager.cs, ConnectionManagerViewModel.cs, ConnectionManagerView.xaml, ConnectionManagerView.xaml.cs, PhaseToColorConverter.cs, ClientConnectionInfo.cs, PhaseProtocolHandler.cs — constructor/HandleAsync portion)
- Review focus: Phase 04 (Settings) + Phase 05 (Connection Manager)

### Overall Assessment
Code is generally clean and well-structured. Two critical issues found (PasswordBox not bound + schtasks injection). Several high/medium items around threading and cleanup.

---

## Critical Issues

### 1. PasswordBox Not Bound to ViewModel — Silent Data Loss
**File:** `Views/SettingsView.xaml` line 63–65

```xml
<PasswordBox Width="280"
             mah:TextBoxHelper.Watermark="Enter password" />
```

`PasswordBox.Password` cannot be bound via standard WPF binding (security restriction). The control has **no binding at all** — `TurnPassword` is never read from the UI. User types a password, clicks Save, old/empty value is persisted.

**Fix options (pick one):**
- Use `mah:TextBoxHelper.IsMonitoring="True"` + `mah:TextBoxHelper.Password` attached property (MahApps pattern) which does support binding:
  ```xml
  <PasswordBox mah:TextBoxHelper.Password="{Binding TurnPassword, UpdateSourceTrigger=PropertyChanged}"
               mah:TextBoxHelper.IsMonitoring="True"
               Width="280" />
  ```
- Or use a `PasswordBoxHelper` behavior in code-behind.

### 2. schtasks Command Injection via `exePath`
**File:** `ViewModels/SettingsViewModel.cs` lines 112–113

```csharp
var args = $"/Create /TN \"RemotePlayServer\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F";
RunSchtasks(args);
```

`exePath` comes from `Process.GetCurrentProcess().MainModule?.FileName` — normally safe (own process). However `/RL HIGHEST` requests elevated privileges via Task Scheduler silently. If the exe path ever contains characters like `"` or `&` (network share paths, unusual install dirs), the argument string breaks. More importantly, **`/RL HIGHEST` causes the task to run as SYSTEM-level elevated** without UAC prompt on subsequent logins — this is an escalation vector if the binary is replaceable.

**Recommendations:**
- Validate `exePath` does not contain unexpected quote chars before embedding.
- Consider `/RL LIMITED` unless elevation is genuinely required.
- Or use registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no elevation needed, less attack surface).

---

## High Priority Findings

### 3. Static Events Never Cleaned Up — Potential Memory Leak in Long-Running Scenarios
**File:** `ViewModels/ConnectionManagerViewModel.cs` lines 22–23

`ConnectionManagerViewModel` subscribes to static events on `PhaseProtocolHandler`. `Dispose()` correctly unsubscribes (lines 84–85). However, `IDisposable` is only useful if the caller actually calls `Dispose()`. No evidence of `using` or explicit disposal at the view level.

**File:** `Views/ConnectionManagerView.xaml.cs` — code-behind has no `Unloaded` handler to call `vm.Dispose()`.

If the view is ever unloaded/reloaded (tab switch, navigation), the old ViewModel stays alive (held by static event), `_refreshTimer` keeps ticking, and duplicate entries accumulate.

**Fix:** In `ConnectionManagerView.xaml.cs`:
```csharp
public ConnectionManagerView()
{
    InitializeComponent();
    Unloaded += (_, _) => (DataContext as IDisposable)?.Dispose();
}
```

### 4. `SaveConfigAsync` Does Not Update `_instance.Config` — In-Memory / On-Disk Divergence
**File:** `Infrastructure/Network/InternetManager.cs` lines 72–81

`SaveConfigAsync` writes JSON to disk but `InternetManager._instance.Config` is readonly (`public InternetConfig Config { get; }`) and remains stale. Any code that reads `InternetManager.Instance?.Config` after saving will get the pre-save values until next restart.

`SettingsViewModel.LoadCurrentSettings()` reads `Instance.Config` — on re-open of the Settings view the old (pre-save) TURN credentials will appear.

**Fix:** Update the singleton instance after saving:
```csharp
public static async Task SaveConfigAsync(InternetConfig config)
{
    // ... write to disk ...
    _instance = new InternetManager(config); // refresh in-memory state
}
```

### 5. `_clientCts` Linked CTS Not Disposed on Normal Disconnect
**File:** `Application/Protocol/PhaseProtocolHandler.cs` lines 234–235, 270–271

```csharp
var disconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
_clientCts[_clientId] = disconnectCts;
```

In `finally`:
```csharp
if (_clientCts.TryRemove(_clientId, out var removedCts))
    removedCts.Dispose();
```

This looks correct — but if `RequestDisconnect` is called and cancels the CTS **before** `HandleAsync` reaches the `finally` block (race during rapid connect/disconnect), the CTS is cancelled and still in the dict. The `finally` removes and disposes it correctly. **However**, if `RequestDisconnect` is called for an `clientId` that no longer exists (stale UI), `cts.Cancel()` silently does nothing — this is fine but worth noting the UI should disable the Disconnect button once the client is removed.

---

## Medium Priority Improvements

### 6. Empty State Panel: Double Visibility Logic (Redundant)
**File:** `Views/ConnectionManagerView.xaml` lines 13–33

The empty-state `StackPanel` has **both** a `Visibility` attribute binding (line 14) with unsupported `ConverterParameter=invert` **and** a `Style` with `DataTrigger`. The attribute binding uses `ConverterParameter=invert` but `BooleanToVisibilityConverter` ignores the parameter — it does not support inversion. The `DataTrigger` in the style overrides it anyway, so the result is correct by accident, but the attribute binding on line 14 is dead/misleading code.

**Fix:** Remove the `Visibility` attribute on line 14 and keep only the style trigger, or use an `InverseBooleanToVisibilityConverter`.

### 7. `ToggleAutoStart` Is Synchronous, Blocks UI Thread for up to 5 Seconds
**File:** `ViewModels/SettingsViewModel.cs` line 159

```csharp
Process.Start(psi)?.WaitForExit(5000);
```

Called from `ToggleAutoStartCommand` which is a synchronous `[RelayCommand]`. If `schtasks.exe` hangs, the UI freezes for 5 seconds.

**Fix:** Make it async with `Process.Start` + `await process.WaitForExitAsync(cts.Token)`.

### 8. `OnClientPhaseChanged` Static Event Is Declared But Never Consumed
**File:** `Application/Protocol/PhaseProtocolHandler.cs` line 59

```csharp
public static event Action<Guid, ConnectionPhase>? OnClientPhaseChanged;
```

`ConnectionManagerViewModel` never subscribes to this event — phase updates only reach the UI if `ClientConnectionInfo.Phase` is updated directly (which requires the PhaseProtocolHandler to call `clientInfo.Phase = newPhase`). Not verified that phase changes are being propagated. If not, the phase column in client cards will always show `Connected`.

Verify that elsewhere in PhaseProtocolHandler (the phase-setting code not reviewed here) `_activeClients[_clientId].Phase = newPhase` is called, or subscribe to `OnClientPhaseChanged` in the ViewModel.

---

## Low Priority Suggestions

### 9. `CheckAutoStartState` Called in Constructor — Spawns Process on App Startup
`LoadCurrentSettings()` → `CheckAutoStartState()` runs synchronously in `SettingsViewModel` constructor, spawning `schtasks.exe /Query`. Defer to first view activation or make async.

### 10. `SaveStatus` Has No Auto-Clear
Status messages like "Codec saved: H264" persist indefinitely. Standard UX: clear after 3–5 seconds.

---

## Positive Observations

- `PhaseToColorConverter` — frozen brushes correctly, clean switch expression.
- `ConnectionManagerViewModel.Dispatch()` pattern handles cross-thread UI updates properly with `CheckAccess()` guard.
- `_clientCts` using linked CTS is the right pattern for GUI-initiated disconnect without coupling to internal token.
- `ClientConnectionInfo` as `ObservableObject` with `[ObservableProperty]` is clean — Duration updates via timer will propagate correctly.
- `InternetManager` volatile singleton pattern is acceptable for this use case.
- XAML disconnect button command binding via `RelativeSource AncestorType=ItemsControl` is correct.

---

## Recommended Actions

1. **[Critical]** Fix `PasswordBox` — add `mah:TextBoxHelper` binding so `TurnPassword` is actually read from UI.
2. **[Critical]** Review `schtasks /RL HIGHEST` — use `/RL LIMITED` or registry autorun unless elevation is required; validate `exePath`.
3. **[High]** Add `Unloaded` dispose hook in `ConnectionManagerView.xaml.cs`.
4. **[High]** Update `_instance` in `InternetManager.SaveConfigAsync` to keep in-memory config in sync.
5. **[Medium]** Remove redundant `Visibility` attribute on empty-state panel (line 14, ConnectionManagerView.xaml).
6. **[Medium]** Make `RunSchtasks` async to avoid blocking UI thread.
7. **[Medium]** Verify `OnClientPhaseChanged` is fired and `ClientConnectionInfo.Phase` is updated during protocol execution.

---

## Metrics
- Linting Issues: 0 (not run — no build available)
- Critical: 2 | High: 3 | Medium: 3 | Low: 2
