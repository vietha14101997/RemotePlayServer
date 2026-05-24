# Streaming App UI/UX Design References

**Date:** 2026-03-28
**Topic:** UI patterns for WPF remote desktop streaming server applications

---

## 1. Server Status & Connection Management UI

### Parsec Desktop Interface
- **Sidebar navigation**: Left-side tab menu (Computers, Settings, Friends, Help, Logout)
- **User roster**: Connected users list at bottom with hover tooltips showing network latency
- **Status indicators**: 3 small icons next to each user (mouse, keyboard, controller usage) that change color based on input activity
- **Real-time feedback**: Visual blinking on state changes for hardware input detection

### Sunshine (Moonlight Server)
- **Web-based dashboard**: Accessed via `https://localhost:47990`
- **App management section**: Add/configure games, manage streaming applications
- **Paired clients display**: List of paired clients with status indicators
- **Configuration UI**: Settings split between simple and advanced sections

### Key Takeaway for WPF
Use sidebar navigation for primary sections. Embed mini status indicators (colored dots, icons) next to connection entries rather than dedicated rows. Tooltip on hover reveals detailed metrics.

---

## 2. Placeholder & Coming-Soon Patterns

### Standard Disabled Element Styling
- **Opacity reduction**: OBS & WPF controls use opacity 0.2-0.4 on disabled state
- **Greyed foreground**: Microsoft pattern is to change text color to grey (#999999 or #666666) rather than opacity
- **Disabled state structure**:
  ```
  Disabled Text Color: #808080 (medium grey)
  Disabled Background: #2D2D2D (darker overlay) in dark theme
  ```

### "Coming Soon" & "Pro" Badges
- **Visual lock icon**: Display padlock beside disabled features
- **Tooltip text**: "This feature is coming in v2.0" or "Pro tier required"
- **Badge placement**: Small label top-right corner of disabled control group
- **Color**: Yellow/amber (#FFB84D) for "Pro", blue (#4A90E2) for "Coming Soon"

### Implementation Pattern
```xml
<!-- WPF Example -->
<StackPanel Opacity="0.5">
  <TextBlock Text="Advanced Recording Options" Foreground="#808080" />
  <Border BorderThickness="1" BorderBrush="#FF6B6B">
    <TextBlock Text="Pro Feature" Foreground="#FFB84D" FontSize="10" />
  </Border>
</StackPanel>
```

---

## 3. System Tray & Notification Patterns

### Windows Notification Area Design
- **Status icon**: Single 16x16 tray icon for application presence
- **Right-click context menu**: Quick actions (Open, Settings, Quit)
- **Hover tooltip**: Shows current server status (e.g., "Server: Online • 2 clients connected")
- **Activity badges**: Small number badge (1-9) on icon corner for pending notifications

### Toast Notifications
- **Trigger events**: Client connected, client disconnected, stream error, server stopped
- **Duration**: 4-5 seconds auto-dismiss
- **Content**: Icon + title + brief message (max 2 lines)
- **Action button**: Optional "View" or "Dismiss" button

### Activity Indicators on Tray Icon
- **Color-coded circle overlay**: Green (online), Yellow (idle), Red (error/offline)
- **Animated pulse**: For incoming connections or activity
- **Positioning**: Bottom-right corner of tray icon (8x8 px on 16x16 base)

---

## 4. Dark Theme Color Palette for Streaming Apps

### Recommended Hex Values
```
Primary Background:  #1E1E1E (OBS, Parsec standard)
Secondary Background: #2D2D2D (panel/sidebar background)
Accent Primary:      #00D9FF (cyan/turquoise - gaming default)
Accent Secondary:    #FF006E (magenta/pink - action buttons)
Tertiary Accent:     #FFB84D (amber - warnings/coming-soon)
Text Primary:        #EBEBEB (near-white)
Text Secondary:      #B0B0B0 (grey for labels)
Disabled Text:       #808080 (medium grey)
Border:              #404040 (subtle divider lines)
Success:             #00D17B (green for online status)
Error:               #FF4A4A (red for failures)
```

### Theme Rationale
- **Cyan (#00D9FF)**: Low-fatigue eye strain, popular in gaming/streaming (OBS default)
- **Magenta (#FF006E)**: High contrast for action buttons, streaming software standard
- **Amber (#FFB84D)**: Warnings without aggression (vs pure red)
- **Background #1E1E1E**: True dark minimizes LCD blooming on HDR displays

---

## 5. Layout Patterns for Server Monitoring

### Recommended Architecture: Hybrid Sidebar + Top Bar

**Top bar** (50-60 px):
- App logo/title left-aligned
- Server status badge center (●Online / ●Offline)
- Help + Settings buttons right-aligned
- Connection counter badge (e.g., "2/8 slots")

**Sidebar** (200-250 px, collapsible):
- Navigation: Dashboard, Clients, Performance, Settings, Logs
- Mini status at bottom: Server uptime, FPS output
- Collapse button (hamburger icon)

**Main content area**:
- Grid of "client connection cards" (200x120 each)
  - Client name, OS icon, latency (ms), bitrate
  - Status indicator (green dot for active)
  - Right-click menu (disconnect, settings, logs)

### Status Bar (Bottom, 30-40 px)
```
[Network ⚡ 85Mbps] [GPU Load 🔲 45%] [CPU Load 🔲 62%] [Uptime ⏱ 12:34:56] [⚙ ]
```
- Real-time resource metrics
- Network activity indicator (animated bar chart)
- Click sections for detailed panel overlay

### Floating Panels (Optional)
- Performance graph overlay: Draggable, semi-transparent (#00000080)
- Connection details: Pop-out at screen edge on client selection
- Chat/notifications: Toast notifications from tray, expandable history panel

### Navigation Tab Structure
```
DASHBOARD          → Home + quick stats grid
  ├─ Clients       → Connected clients + history
  ├─ Performance   → Graphs (CPU, GPU, Network, Frame time)
  ├─ Logs          → Server + connection logs (searchable)
  └─ Settings      → Config grouped: Display, Network, Security, Advanced
```

---

## Design Notes

**Dark theme adoption**: All major streaming apps (OBS, Parsec, Sunshine) default to dark themes. Use #1E1E1E base with #00D9FF accent for credibility with gaming audience.

**Feedback density**: Real-time status indicators (color-coded dots, animated badges) reduce cognitive load vs text-heavy status messages.

**Disabled state clarity**: Combine greyed text + lock icon + tooltip to prevent user confusion between "broken" and "unavailable" features.

**Tray integration**: Essential for server apps. Minimize to tray → balloon tip on important events prevents desktop clutter.

---

## Sources

- [Parsec Remote Desktop UI Configurations](https://support.parsec.app/hc/en-us/articles/32381443626516-All-Advanced-Configuration-Options)
- [Parsec Windows App Documentation](https://support.parsec.app/hc/en-us/articles/32381199341716-Parsec-App-for-Windows)
- [Moonlight GameStream Project](https://moonlight-stream.org/)
- [Sunshine Game Stream Server](https://github.com/LizardByte/Sunshine)
- [OBS Studio Themes Guide](https://obsproject.com/kb/themes-guide)
- [Windows Notification Area Documentation](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
- [Disabled UI Elements in Dark Theme](https://medium.com/@h_locke/is-it-ok-to-grey-out-disabled-buttons-9d974cea889e)
- [WPF Dark Theme Implementation](https://engy.us/blog/2018/10/20/dark-theme-in-wpf/)
