# Code Review Summary — WPF UI Redesign (4 Phases)

**Date:** 2026-03-28
**Reviewer:** code-reviewer

---

## Scope
- Files reviewed: `Resources/DarkTheme.xaml`, `Views/MainWindow.xaml`, `Views/MainWindow.xaml.cs`, `ViewModels/MainViewModel.cs`, `Views/DashboardView.xaml`, `Views/DashboardView.xaml.cs`, `ViewModels/DashboardViewModel.cs`, `Views/SettingsView.xaml`, `ViewModels/SettingsViewModel.cs`, `Views/ConnectionManagerView.xaml`, `ViewModels/ConnectionManagerViewModel.cs`, `Views/LogViewerView.xaml`, `ViewModels/LogViewerViewModel.cs`, `Converters/InverseBoolConverter.cs`, `App.xaml`
- Review focus: XAML binding errors, resource key references, animation issues, memory leaks (critical/high only)

---

## Overall Assessment
Implementation is largely solid. Resource key references are consistent. Two memory leaks are real issues — both `DashboardViewModel` and `MainViewModel` subscribe to static events without ever unsubscribing. One XAML binding binding issue exists in the Storyboard animation (missing `TargetName`). Settings `SaveStatus` toast visibility has an edge-case bug. Everything else is medium/low.

---

## Critical Issues

None.

---

## High Priority Findings

### 1. Memory Leak — `DashboardViewModel` never unsubscribes from `PhaseProtocolHandler` events

**File:** `ViewModels/DashboardViewModel.cs` lines 31–32

```csharp
PhaseProtocolHandler.OnClientConnected += _ => Dispatch(...);
PhaseProtocolHandler.OnClientDisconnected += _ => Dispatch(...);
```

`PhaseProtocolHandler` events are static. `DashboardViewModel` is instantiated once per `MainViewModel` construction and never disposed. The lambda captures `this`, so the static event holds a strong reference to the VM indefinitely. If `MainViewModel` is ever re-created (e.g. DI scope reset), old VM instances will never be GC'd and event handlers will fire on dead objects.

`DashboardViewModel` also holds a `DispatcherTimer` that is started but never stopped — a second leak.

**Fix:** Implement `IDisposable`, unsubscribe events and stop the timer in `Dispose()`. Call `Dispose()` in `MainViewModel` when applicable (mirrors the existing `IDisposable` pattern on `ConnectionManagerViewModel`).

---

### 2. Memory Leak — `MainViewModel` never unsubscribes from `PhaseProtocolHandler` events

**File:** `ViewModels/MainViewModel.cs` lines 38–39

Same pattern as #1. Two additional lambda subscriptions to static events are never removed. The `_statusTimer` is started but never stopped/disposed.

**Fix:** Same as #1 — implement `IDisposable` on `MainViewModel`.

---

### 3. `FadeInAnimation` Storyboard missing `TargetName` — animation is a no-op

**File:** `Resources/DarkTheme.xaml` lines 269–276, `Views/MainWindow.xaml.cs` line 31

```xml
<Storyboard x:Key="FadeInAnimation">
    <DoubleAnimation Storyboard.TargetProperty="Opacity" .../>
</Storyboard>
```

`Storyboard.TargetName` is not set on the `DoubleAnimation`. When called with `storyboard.Begin(ContentArea)`, WPF resolves the target using `TargetName` first; if absent, it falls back to the `containingObject` argument only when `isControllable=true` is passed to `Begin()`. The current call is `storyboard.Begin(ContentArea)` — no `isControllable` arg, so this is `storyboard.Begin(ContentArea, false)`. Without `TargetName` specified in the XAML and without the `HandoffBehavior` overload, the animation targets the Storyboard's name scope root, **not** `ContentArea`. The fade does not animate the content area opacity.

**Fix:** Either set `Storyboard.TargetName="ContentArea"` in the XAML (but then it can't be reused), or use the controllable overload in code-behind:
```csharp
var storyboard = (Storyboard)FindResource("FadeInAnimation");
storyboard.Begin(ContentArea, HandoffBehavior.SnapshotAndReplace, isControllable: true);
```
Or more simply, create the animation in code-behind directly to avoid the name-scope ambiguity entirely.

---

### 4. `PulseAnimation` missing `TargetName` — `storyboard.Begin(RunDot, true)` works but is fragile

**File:** `Resources/DarkTheme.xaml` lines 260–267, `Views/DashboardView.xaml.cs` line 43

`storyboard.Begin(RunDot, true)` does work when `TargetName` is absent (WPF uses `RunDot` as the containing object). This is technically correct but only because the second overload is used. However, the `PulseAnimation` resource is shared across all potential callers. If ever retrieved from a different name scope (e.g. after a refactor), it will silently break. Low-risk now but worth noting alongside issue #3.

---

### 5. `SaveStatus` toast visibility: empty string not treated as null by `NullToVisibilityConverter`

**File:** `Views/SettingsView.xaml` line 15, `ViewModels/SettingsViewModel.cs` line 156

The toast visibility binding uses `NullToVisibilityConverter`, but `SetStatusWithAutoClear` clears the toast by setting `SaveStatus = ""` (empty string, not null). If `NullToVisibilityConverter` checks only for `null`, the toast will remain visible with empty content after auto-clear.

**Fix:** Either set `SaveStatus = null` on clear, or check both null and empty in `NullToVisibilityConverter`. Depends on converter implementation (not reviewed — converter file not included in scope). Verify the converter handles `string.IsNullOrEmpty`.

---

## Medium Priority Improvements

### 6. `DashboardView.xaml.cs` — `OnDataContextChanged` leaks old VM subscription

**File:** `Views/DashboardView.xaml.cs` lines 20–26

When `DataContext` changes, the handler subscribes to `vm.State.PropertyChanged` but **never unsubscribes the old VM's state**. There is no `e.OldValue` cleanup. In practice this view is permanent, but it's a correctness gap.

---

### 7. `ConnectionManagerViewModel` — `Dispose()` is never called by owner

**File:** `ViewModels/MainViewModel.cs` line 18, `ViewModels/ConnectionManagerViewModel.cs` line 81

`ConnectionManagerViewModel` correctly implements `IDisposable`, but `MainViewModel` holds it as a field and never calls `Dispose()`. The timer and event subscriptions are not cleaned up on app shutdown.

---

### 8. `SettingsView.xaml` — `AutoStartToggled` event handler referenced but not reviewed

**File:** `Views/SettingsView.xaml` line 114

```xml
<mah:ToggleSwitch IsOn="{Binding AutoStartEnabled}" Toggled="AutoStartToggled" />
```

The code-behind handler `AutoStartToggled` is referenced but the `SettingsView.xaml.cs` file was not part of the review scope. If it re-invokes `ToggleAutoStart()` directly (instead of relying solely on the binding), the auto-start command will be double-triggered on initial load (toggle binding sets `AutoStartEnabled`, which fires `Toggled`, which calls the command again).

---

## Low Priority

- `MainViewModel` uptime timer and `DashboardViewModel` uptime timer both independently format `(DateTime.UtcNow - started)` identically. Uptime is duplicated state — the Dashboard could bind to `MainViewModel.Uptime` through the parent DataContext instead.
- `PlaceholderCard` style sets `IsHitTestVisible = True` then `Opacity = 0.5` — hit-test-visible but visually dimmed. Users can still tab-focus controls inside. Consider `IsEnabled=False` on the placeholder Grid wrapper instead.
- `ConnectionManagerView` phase timeline is hardcoded with 5 dots but Phase strings suggest up to Phase3 sub-states — the dot count may not accurately represent all real states.

---

## Positive Observations

- Resource key naming is consistent and complete. All `StaticResource`/`DynamicResource` references in XAML match keys defined in `DarkTheme.xaml` — no missing key errors.
- `ConnectionManagerViewModel` correctly uses `IDisposable` pattern with full event cleanup — the right approach, just not followed consistently in other VMs.
- `MainWindow.xaml.cs` properly handles `DataContextChanged` old/new cleanup for `MainViewModel` subscription (lines 18–24).
- `DispatcherTimer` + `Dispatch()` pattern correctly marshals cross-thread updates from socket callbacks.
- Sidebar collapse via `DataTrigger` width change is correct for WPF — avoids animation complexity while being fully bindable.
- `App.xaml` merge order is correct (MahApps first, then custom overrides).

---

## Recommended Actions

1. **[HIGH]** Implement `IDisposable` on `DashboardViewModel` — unsubscribe `PhaseProtocolHandler` events, stop `_uptimeTimer`.
2. **[HIGH]** Implement `IDisposable` on `MainViewModel` — unsubscribe `PhaseProtocolHandler` events, stop `_statusTimer`. Call `_connections.Dispose()` from here.
3. **[HIGH]** Fix `FadeInAnimation` — add `Storyboard.TargetName` or use controllable `Begin()` overload so the content area actually fades on navigation.
4. **[HIGH]** Verify `NullToVisibilityConverter` handles empty string, or change `SaveStatus = ""` to `SaveStatus = null` in `SetStatusWithAutoClear`.
5. **[MED]** Add old-VM unsubscribe in `DashboardView.xaml.cs` `OnDataContextChanged`.
6. **[MED]** Audit `SettingsView.xaml.cs` `AutoStartToggled` handler for double-trigger on load.

---

## Metrics
- Type Coverage: N/A (C# with `#nullable enable` throughout — good)
- Test Coverage: N/A
- Linting Issues: 0 compiler-visible (resource keys all resolve)
- Memory Leak Risk: 2 confirmed (VMs #1/#2), 1 minor (DashboardView old-VM)

---

## Task Completeness

All 4 phases are implemented per the plan. Plan file `plan.md` still shows all phases as `TODO` — statuses not updated.
