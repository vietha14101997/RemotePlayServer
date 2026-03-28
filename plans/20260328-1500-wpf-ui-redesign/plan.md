# WPF UI Redesign - RemotePlayServer

**Date:** 2026-03-28
**Goal:** Professional dark gaming/streaming aesthetic with modern affordances, placeholder features for future expansion.
**Constraints:** No new NuGet packages. Keep MVVM, keep existing models. Files <200 lines.

## Color System
- Primary accent: `#7C3AED` (purple, brand)
- Secondary accent: `#00D9FF` (cyan, streaming indicators)
- Coming Soon badge: `#4A90E2` (blue)
- Existing semantic colors unchanged (success/warning/error)

## Phases

| # | Phase | Status | File |
|---|-------|--------|------|
| 1 | Design System & Theme Overhaul | DONE | [phase-01-design-system.md](phase-01-design-system.md) |
| 2 | Navigation & Shell Redesign | DONE | [phase-02-navigation-shell.md](phase-02-navigation-shell.md) |
| 3 | Dashboard Redesign | DONE | [phase-03-dashboard-redesign.md](phase-03-dashboard-redesign.md) |
| 4 | Settings, Connections & Placeholders | DONE | [phase-04-settings-connections-placeholders.md](phase-04-settings-connections-placeholders.md) |

## File Impact Summary

### Modified Files
- `Resources/DarkTheme.xaml` - complete rewrite (Phase 1)
- `Views/MainWindow.xaml` + `.cs` - shell redesign (Phase 2)
- `Views/DashboardView.xaml` + `.cs` - dashboard redesign (Phase 3)
- `Views/SettingsView.xaml` - settings form redesign (Phase 4)
- `Views/ConnectionManagerView.xaml` - client cards redesign (Phase 4)
- `ViewModels/MainViewModel.cs` - add LogViewer nav, sidebar collapse, status bar props (Phase 2)
- `ViewModels/DashboardViewModel.cs` - add uptime timer, KPI computed props (Phase 3)
- `App.xaml` - no changes needed

### New Files
- `Views/LogViewerView.xaml` - placeholder log viewer (Phase 4)
- `ViewModels/LogViewerViewModel.cs` - minimal placeholder VM (Phase 4)
- `Converters/InverseBoolConverter.cs` - for sidebar collapse (Phase 2)

## Post-Implementation Issues (from code review 2026-03-28)
- **[HIGH]** Memory leak: `DashboardViewModel` + `MainViewModel` never unsubscribe from `PhaseProtocolHandler` static events; timers never stopped
- **[HIGH]** `FadeInAnimation` storyboard missing `TargetName` — fade transition is a no-op
- **[HIGH]** `NullToVisibilityConverter` may not handle empty string for `SaveStatus` toast auto-clear
- **[MED]** `ConnectionManagerViewModel.Dispose()` never called by `MainViewModel`
- **[MED]** `DashboardView` old-VM `PropertyChanged` not unsubscribed on DataContext change

## Architecture Notes
- Unicode symbols for nav icons (no IconPacks NuGet needed -- MahApps.Metro.IconPacks is NOT bundled in MahApps.Metro 2.4.10)
- Animations defined in DarkTheme.xaml as Storyboard resources
- Sidebar collapse via ColumnDefinition width animation + bool toggle in MainViewModel
- Status bar as bottom Grid row in MainWindow
- View fade transition via ContentControl opacity Storyboard in MainWindow.xaml.cs (minimal code-behind)
- "Coming soon" sections use shared `PlaceholderOverlay` style (opacity 0.4 + badge)
