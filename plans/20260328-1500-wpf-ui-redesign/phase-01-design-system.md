# Phase 01: Design System & Theme Overhaul

**File:** `Resources/DarkTheme.xaml` (complete rewrite, split into logical sections)
**Goal:** Establish reusable design tokens, component styles, and animations.

---

## 1. Extended Color Palette

Add these new colors/brushes to existing palette:

```xml
<!-- Secondary Accent (cyan for streaming) -->
<Color x:Key="AppAccentSecondary">#00D9FF</Color>
<SolidColorBrush x:Key="AppAccentSecondaryBrush" Color="{StaticResource AppAccentSecondary}" />

<!-- Coming Soon badge -->
<Color x:Key="AppComingSoon">#4A90E2</Color>
<SolidColorBrush x:Key="AppComingSoonBrush" Color="{StaticResource AppComingSoon}" />

<!-- Surface Elevated (for hover/elevation effects on cards) -->
<Color x:Key="AppSurfaceElevated">#32324A</Color>
<SolidColorBrush x:Key="AppSurfaceElevatedBrush" Color="{StaticResource AppSurfaceElevated}" />

<!-- Disabled -->
<Color x:Key="AppDisabledText">#606070</Color>
<SolidColorBrush x:Key="AppDisabledTextBrush" Color="{StaticResource AppDisabledText}" />
```

## 2. Spacing Tokens

Define as `Thickness` resources for consistent margins/padding:

```xml
<Thickness x:Key="SpacingXs">4</Thickness>
<Thickness x:Key="SpacingSm">8</Thickness>
<Thickness x:Key="SpacingMd">12</Thickness>
<Thickness x:Key="SpacingLg">16</Thickness>
<Thickness x:Key="SpacingXl">24</Thickness>
<Thickness x:Key="SpacingXxl">32</Thickness>
<sys:Double x:Key="CardCornerRadius">8</sys:Double>
<CornerRadius x:Key="CardRadius">8</CornerRadius>
<CornerRadius x:Key="BadgeRadius">4</CornerRadius>
<CornerRadius x:Key="ButtonRadius">6</CornerRadius>
```

## 3. Button Styles

### 3.1 PrimaryButton
- Background: `AppAccentBrush`, Foreground: white
- Hover: `AppAccentLightBrush` background
- Pressed: darken accent slightly
- CornerRadius: 6px, Padding: 16,8
- Transition feel: use Trigger-based instant swap (WPF limitation -- no CSS transitions)

### 3.2 SecondaryButton
- Background: `AppSurfaceHoverBrush`, Foreground: `AppTextBrush`
- Hover: `AppSurfaceElevatedBrush`
- Border: 1px `AppBorderBrush`

### 3.3 DangerButton
- Background: transparent, Foreground: `AppErrorBrush`
- Hover: `AppError` at 15% opacity background
- Border: 1px `AppErrorBrush`

### 3.4 IconButton (for hamburger, copy, etc.)
- Background: transparent, 32x32 size
- Hover: `AppSurfaceHoverBrush` circle/rounded background
- Content: TextBlock with Unicode symbol, 16px

All button styles need a shared `ControlTemplate` with `Border` wrapping `ContentPresenter`, plus `Trigger` for IsMouseOver, IsPressed, IsEnabled.

## 4. Status Badge Style

Reusable for connection status pills, phase badges:

```xml
<Style x:Key="StatusBadge" TargetType="Border">
    <Setter Property="CornerRadius" Value="10" />
    <Setter Property="Padding" Value="8,3" />
    <Setter Property="Background" Value="{StaticResource AppSurfaceHoverBrush}" />
</Style>
```

Inner TextBlock: 11px, centered. Color set per-instance via binding.

## 5. KPI Tile Style

For dashboard metric cards:

```xml
<Style x:Key="KpiTile" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource AppSurfaceBrush}" />
    <Setter Property="BorderBrush" Value="{StaticResource AppBorderBrush}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="8" />
    <Setter Property="Padding" Value="16,12" />
    <Setter Property="MinWidth" Value="140" />
</Style>
```

Layout inside: icon (Unicode, 20px, accent-colored) top-left, large value (24px bold), label below (12px dim).

## 6. Placeholder Overlay Style

For "coming soon" sections:

```xml
<Style x:Key="PlaceholderCard" TargetType="Border">
    <!-- Same as CardBorder but with reduced opacity content -->
    <Setter Property="Background" Value="{StaticResource AppSurfaceBrush}" />
    <Setter Property="BorderBrush" Value="{StaticResource AppBorderBrush}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="8" />
    <Setter Property="Padding" Value="16" />
    <Setter Property="Opacity" Value="0.5" />
    <Setter Property="IsHitTestVisible" Value="True" />
    <Setter Property="ToolTip" Value="Coming in a future update" />
</Style>
```

"Coming Soon" badge: absolute-positioned Border top-right with `AppComingSoonBrush` background, white text "COMING SOON" 9px bold.

## 7. Card Hover Effect

Update existing `CardBorder` style to include hover state:

```xml
<Style x:Key="CardBorder" TargetType="Border">
    <!-- existing setters... -->
    <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
            <Setter Property="BorderBrush" Value="{StaticResource AppSurfaceHoverBrush}" />
            <Setter Property="Background" Value="{StaticResource AppSurfaceElevatedBrush}" />
        </Trigger>
    </Style.Triggers>
</Style>
```

## 8. Animation Resources

### 8.1 Pulse Animation (for running indicator)
```xml
<Storyboard x:Key="PulseAnimation" RepeatBehavior="Forever">
    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                     From="1" To="0.3" Duration="0:0:1.2" AutoReverse="True">
        <DoubleAnimation.EasingFunction>
            <SineEase EasingMode="EaseInOut" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
</Storyboard>
```

### 8.2 Fade-In Animation (for view transitions)
```xml
<Storyboard x:Key="FadeInAnimation">
    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                     From="0" To="1" Duration="0:0:0.25">
        <DoubleAnimation.EasingFunction>
            <QuadraticEase EasingMode="EaseOut" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
</Storyboard>
```

## 9. Updated NavListBoxItem

Add left accent bar on selected state + icon support:

```xml
<Style x:Key="NavListBoxItem" TargetType="ListBoxItem">
    <!-- Template: Grid with 3px left accent border (visible on selected) + ContentPresenter -->
    <!-- Selected: left bar = AppAccentBrush, text = AppAccentLightBrush -->
    <!-- Hover: background = AppSurfaceHoverBrush -->
    <!-- Padding: 12,12,16,12 for icon+text layout -->
</Style>
```

Each nav item content will be a StackPanel(Horizontal) with TextBlock(icon, 18px, width=28) + TextBlock(label, 13px).

## 10. Input/TextBox Focus Style

Override MahApps TextBox to show accent border on focus:
- Default border: `AppBorderBrush`
- Focus border: `AppAccentBrush` (1px)
- Background: slightly darker than surface (`#252536`)

## 11. ToggleSwitch Color Override

Override MahApps ToggleSwitch On-state color to use `AppAccentBrush` instead of default blue.

## Task Checklist

- [ ] Add new colors: `AppAccentSecondary`, `AppComingSoon`, `AppSurfaceElevated`, `AppDisabledText`
- [ ] Add spacing tokens (Thickness resources)
- [ ] Create `PrimaryButton` style with hover/press triggers
- [ ] Create `SecondaryButton` style
- [ ] Create `DangerButton` style
- [ ] Create `IconButton` style (transparent, 32x32)
- [ ] Create `StatusBadge` style
- [ ] Create `KpiTile` style
- [ ] Create `PlaceholderCard` style + "Coming Soon" badge pattern
- [ ] Update `CardBorder` with hover effect
- [ ] Add `PulseAnimation` storyboard resource
- [ ] Add `FadeInAnimation` storyboard resource
- [ ] Update `NavListBoxItem` with left accent bar + icon layout support
- [ ] Add TextBox focus border override
- [ ] Override ToggleSwitch on-state to accent color
- [ ] Verify DarkTheme.xaml stays under 200 lines (split if needed into `DarkTheme.Animations.xaml` or similar, merge in App.xaml)
