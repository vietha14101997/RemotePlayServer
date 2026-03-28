# Modern WPF Desktop Application UI/UX Design Patterns
**Research Report** | 2026-03-28 | Streaming Server Control Panel

## 1. Modern Dark UI Design Trends 2025

**Current Best Practices for Streaming/Server Apps**

Dark UI dominance continues in 2025 for professional desktop applications. Modern WPF frameworks now prioritize:
- **High contrast ratios** on dark backgrounds (≥4.5:1 WCAG AA compliance)
- **Accent colors** as primary CTA elements—avoid pure whites on black
- **Semantic color system**: success (green), warning (amber), error (red), info (blue)
- **Subtle depth** via layering rather than hard shadows—use Mica/acrylic effects where available

**OBS Studio/Parsec/Moonlight patterns observed:**
- Sidebar navigation with collapsible state (hamburger + icon groups)
- Card-based dashboard with minimal borders (1px, muted colors)
- Real-time gauges/progress rings for bandwidth, FPS, latency
- Toast notifications in bottom-right corner, non-intrusive
- Clean typography hierarchy: minimal font weights, large line spacing (1.5x)

**Available WPF Solutions:** WPF UI library provides modern dark theme with Fluent 2 styling; iNKORE-NET UI.WPF.Modern offers similar Material Design approach; both render native Windows aesthetics without custom rendering.

---

## 2. MahApps.Metro 2.4.10 Advanced Controls for Dashboards

**Key Controls & Dashboard Use Cases:**

| Control | Best For | Notes |
|---------|----------|-------|
| **MetroHeader** | Section headers, panel titles | Combine with icon + label for visual hierarchy |
| **HamburgerMenu** | Main navigation collapse toggle | Built on SplitView; responsive item icons |
| **Tile** | KPI cards (FPS, bitrate, latency) | Supports custom backgrounds, badge overlays |
| **BadgedIcon/Badged** | Status indicators (online/recording count) | Small, prominent notification badges |
| **ProgressRing** | Async operation spinners | Non-linear loaders for file transfers, encoding |
| **Flyout** | Context menus, quick settings panels | Lightweight alternative to modal dialogs |
| **NumericUpDown** | Server port, stream quality selection | Spinbutton control with validation |
| **SplitButton** | Primary action + dropdown fallbacks | E.g., "Start Stream" + preset profiles |

**Practical pattern:** Stack Tiles in a 3-column UniformGrid; bind each Tile's content to ObservableCollection; use Metro's built-in color palette (Accent, Highlight, Gray brushes) for consistency.

---

## 3. WPF Animation/Transition Patterns for View Switching

**VisualStateManager Approach (Recommended for MahApps.Metro)**

Define visual states in XAML control templates:
```xaml
<VisualStateGroup x:Name="ViewStates">
  <VisualState x:Name="ActiveView" />
  <VisualState x:Name="InactiveView" />
  <VisualTransition From="InactiveView" To="ActiveView" Duration="0:0:0.3">
    <Storyboard>
      <DoubleAnimation Storyboard.TargetProperty="Opacity"
                       From="0" To="1" Duration="0:0:0.3" />
    </Storyboard>
  </VisualTransition>
</VisualStateGroup>
```

**Fade Transitions:** Switch Opacity from 0→1 on incoming view (outgoing uses Visibility binding workaround). Duration 250–400ms feels natural without sluggishness.

**Slide Transitions:** Combine TranslateTransform with Duration ~350ms for horizontal panel shifts. Use EasingFunction (QuadraticEase) for organic motion.

**Trigger via:** VisualStateManager.GoToState(control, "ActiveView", true) from View code-behind or behavior converter.

**Alternative (lighter):** Custom Attached Behavior using Storyboard.SetTargetProperty for reusable transition logic across multiple views.

---

## 4. Status Indicator Patterns: Live Pulse Animation

**Live Pulse Animation (Streaming Active)**

Create reusable XAML ResourceDictionary Storyboard:
```xaml
<Storyboard x:Key="PulseStoryboard" RepeatBehavior="Forever">
  <DoubleAnimation Storyboard.TargetProperty="Opacity"
                   From="1" To="0.4" Duration="0:0:1.2"
                   AutoReverse="True" />
</Storyboard>
```
Attach to a Circle/ProgressRing element; works with AccentColor brush.

**Traffic Light Dots (Connection Status)**
- Green circle: connected, streaming
- Amber circle: buffering, warning
- Red circle: disconnected, error
- Gray circle: idle

Use Tile control + custom border background; bind color to Status enum via ValueConverter.

**Progress Rings (Ongoing Operations)**
- MahApps ProgressRing supports IsActive + custom color binding
- 24–32px diameter fits inline dashboards
- Combine with text label ("Connecting...") for clarity
- Replace loading spinners in header during file uploads

---

## 5. Card-Based Dashboard Layouts with Real-Time Data

**Best Practices for Streaming Apps**

**Layout Structure:**
- Grid 3–4 columns with UniformGrid or custom Grid.ColumnDefinition ratios
- Each Tile = 1 card; height 120–160px for readability
- Padding 16–20px internal; margin 12px external for breathing room
- Use Metro's Thumb color palette: AppAccent for primary, AppSurface for background

**Real-Time Metrics Display:**
- FPS gauge: center large number (72pt), sub-label "frames/sec"
- Bitrate card: show current vs. max (e.g., "8.5 / 10 Mbps"), stack with ProgressRing or bar
- Latency: numeric + color code (≤50ms green, 50–150 amber, >150 red)
- CPU/GPU load: horizontal ProgressBar filling Tile
- Connection count: BadgedIcon with count overlay

**Data Binding Pattern (MVVM):**
Use ObservableCollection<StreamMetric> in ViewModel; bind ItemsSource to ItemsControl with Tile DataTemplate. Update via high-frequency dispatcher (50–100ms intervals); WPF handles rendering throttle internally.

**Avoid:** Heavy chart libraries for simple metrics—use native shapes (Rectangle for bars) + binding converters; reserve SciChart/LightningChart for historical graphs only.

---

## Implementation Roadmap

1. **Phase 1:** Apply MahApps.Metro 2.4.10 dark theme globally (Colors, MetroWindow)
2. **Phase 2:** Build navigation HamburgerMenu with icon-based main sections
3. **Phase 3:** Design card dashboard with Tile grid (3 cols: FPS, bitrate, latency)
4. **Phase 4:** Implement fade/slide view transitions via VisualStateManager
5. **Phase 5:** Add pulse animations on status indicators; integrate real-time data binding

**Performance Notes:** WPF throttles rendering to monitor refresh rate (typically 60 Hz); UI updates >100ms intervals won't degrade perception. Use DispatcherTimer or Task.Delay for periodic metric polling.

---

## Unresolved Questions

- Specific target screen resolution (1920x1080 min vs. tablet friendly)?
- Requirement for accessibility (WCAG AA/AAA)?
- Preference for third-party charting library if historical trend graphs needed?
