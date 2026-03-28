# Scout Report: RemotePlayServer Current UI State

**Date:** 2026-03-28  
**Scope:** WPF GUI structure, binding architecture, styling approach  
**Status:** Complete

---

## Executive Summary

RemotePlayServer uses **MahApps.Metro** (WPF theming framework) with a **sidebar navigation + content area** layout. The UI leverages **MVVM Toolkit** for data binding with 4 main ViewModels managing Dashboard, Settings, Connections, and navigation state. A consistent **dark theme palette** provides visual hierarchy, though the design lacks modern affordances (animations, transitions, hover feedback).

---

## 1. XAML Views (Views/)

### 1.1 MainWindow.xaml
**Current State:**
- Layout: MetroWindow with 3-column grid (180px sidebar, 1px separator, * content)
- Navigation: ListBox with hardcoded items (Dashboard, Settings, Connections)
- Tray Icon: H.NotifyIcon integration for system tray
- Window: 900x600 default, 700x450 minimum, centered on screen

**Design Gaps:**
- No window chrome customization beyond MahApps defaults
- Navigation items are text-only (no icons)
- Tray icon context menu is minimal (only Show/Exit)
- No breadcrumb or current route indicator visible

### 1.2 DashboardView.xaml
**Current State:**
- Layout: ScrollViewer + StackPanel (vertical card-based)
- Status Card + QR Code Card (220px fixed width)
- Monitors Card (ItemsControl listing displays)
- Connection Options Card (USB, LAN, WiFi, Internet, Token status)

**Design Gaps:**
- QR code card fixed width breaks responsive design
- Connection Options are status-only, lacks interactivity
- No error states or warning alerts beyond inline text
- Monitor items lack visual distinction or selection

### 1.3 SettingsView.xaml
**Current State:**
- Codec Settings: ComboBox + Save button
- Internet Mode: Toggle + conditional TURN fields (server, username, password)
- System: Auto-start with Windows toggle
- Inline SaveStatus feedback (accent-colored)

**Design Gaps:**
- Form layout cramped (120px label width)
- TURN credentials in plaintext (no password masking in UI)
- No validation UI (required fields unmarked)
- Auto-start toggle uses code-behind event instead of pure MVVM

### 1.4 ConnectionManagerView.xaml
**Current State:**
- Empty State: Centered text when no clients
- Client List: ItemsControl of cards with Phase indicator, IP, Transport, Codec, Duration
- Disconnect button per client

**Design Gaps:**
- No hover effects defined for client cards
- Phase/Codec/Duration hard to scan inline
- No sorting/filtering or connection quality metrics

---

## 2. ViewModels (ViewModels/)

### 2.1 MainViewModel.cs
- CurrentView (switches between Dashboard/Settings/Connections)
- SelectedNavIndex (0-2 routing)
- Commands: ShowWindowCommand, ExitAppCommand

### 2.2 DashboardViewModel.cs
- State (ServerState reference)
- QrImage (ImageSource, auto-generated from QrData)

### 2.3 SettingsViewModel.cs
- CodecOptions (hardcoded: Auto, H264, H265)
- Properties for Codec, Internet, TURN, AutoStart
- Commands: SaveCodecCommand, SaveInternetSettingsCommand (async)
- Gaps: No validation, blocking schtasks.exe at init

### 2.4 ConnectionManagerViewModel.cs
- Clients (ObservableCollection<ClientConnectionInfo>)
- HasClients (bool)
- DisconnectClientCommand(Guid)
- DispatcherTimer updates Duration every 1 second

---

## 3. Models (Models/)

### 3.1 ServerState.cs
**Bindable Properties:**
- IsRunning, LocalIp, Port, Encoder, IsAdmin, AdbReverseActive
- TunnelUrl, AuthToken, InternetEnabled, PreferredCodec
- Monitors (ObservableCollection), QrData, StatusMessage

### 3.2 ClientConnectionInfo.cs
**Bindable Properties:**
- ClientId (Guid), RemoteIp, Phase, Codec, TransportType
- ConnectedAt, IsUsbTransport, Duration

### 3.3 MonitorInfo.cs
**Properties:** Name, Width, Height, IsVirtual

---

## 4. Resources/DarkTheme.xaml

### Color Palette
| Purpose | Hex |
|---------|-----|
| Background | #1E1E2E |
| Surface | #2A2A3C |
| Surface Hover | #353548 |
| Accent | #7C3AED |
| Accent Light | #9F67FF |
| Text | #E0E0E0 |
| Text Dim | #9090A0 |
| Success | #22C55E |
| Warning | #F59E0B |
| Error | #EF4444 |
| Border | #3A3A4C |

### Defined Styles
- CardBorder (8px radius, 16px padding, 1px border)
- SectionHeader (14px semibold)
- InfoLabel (12px dim)
- InfoValue (13px primary)
- NavListBox/NavListBoxItem (custom template with hover/select states)

**Gaps:** No button styles, no input hover/focus states, no animations, spacing hardcoded

---

## 5. Converters (Converters/)

- BoolToColorConverter: true=green, false=gray
- NullToVisibilityConverter: null=Collapsed, else=Visible
- BoolToLabelConverter: Parameterized labels
- PhaseToColorConverter: Enum to color mapping (green/blue/yellow/gray/red)

---

## 6. Design Recommendations Summary

### HIGH PRIORITY
1. Navigation Icons (Fluent/Material)
2. Responsive Layout (replace fixed sidebar)
3. Form Validation (required fields, error messages)
4. Button Styling (consistent primary/secondary/danger)
5. Spacing Design Tokens

### MEDIUM PRIORITY
6. Hover/Focus States
7. Empty State Illustrations
8. Loading Indicators
9. View Transitions
10. Search/Filter

### LOW PRIORITY
11. Dark/Light Theme Toggle
12. Notifications Panel
13. Metrics Dashboard
14. Accessibility Support

---

**Report Generated:** 2026-03-28  
**Scout:** Codebase Scout Agent (WPF UI Redesign Initiative)
