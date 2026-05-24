# Phase 03: Dashboard Redesign

**Files Modified:** `Views/DashboardView.xaml`, `Views/DashboardView.xaml.cs`, `ViewModels/DashboardViewModel.cs`

---

## 1. Layout Structure

Replace vertical StackPanel with structured Grid layout:

```
+--------------------------------------------------+
| [KPI Tile] [KPI Tile] [KPI Tile] [KPI Tile]     |  <- row 0: KPI tiles
+--------------------------------------------------+
| [Server Status Card]        | [QR Code Card]     |  <- row 1: main info
+--------------------------------------------------+
| [Connection Options Card]   | [Monitors Card]     |  <- row 2: details
+--------------------------------------------------+
```

Top-level: `ScrollViewer > Grid` with 3 rows. Padding: 24px.

## 2. KPI Tiles Row

4 tiles in a `UniformGrid Columns="4"` with 12px gap (use Margin on each tile).

### Tile 1: Clients Connected
- Icon: `&#x26A1;` (cyan accent)
- Value: `{Binding ActiveClientCount}` (large 24px bold)
- Label: "Connected" (12px dim)

### Tile 2: Server Uptime
- Icon: `&#x23F1;` (accent)
- Value: `{Binding Uptime}` format `HH:mm:ss` (24px bold)
- Label: "Uptime" (12px dim)

### Tile 3: Encoder
- Icon: `&#x2699;` (accent)
- Value: `{Binding State.Encoder}` (20px bold, auto-size if long)
- Label: "Encoder" (12px dim)

### Tile 4: Network
- Icon: `&#x1F310;` or `&#x2B06;` (cyan accent)
- Value: "Active" or "Idle" based on `State.IsRunning`
- Label: "Network" (12px dim)
- Placeholder note: throughput stats in future update

### KPI Tile XAML pattern:
```xml
<Border Style="{StaticResource KpiTile}" Margin="0,0,12,0">
    <StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="&#x26A1;" FontSize="18"
                       Foreground="{StaticResource AppAccentSecondaryBrush}" />
        </StackPanel>
        <TextBlock Text="{Binding ActiveClientCount}" FontSize="24" FontWeight="Bold"
                   Foreground="{StaticResource AppTextBrush}" />
        <TextBlock Text="Connected" FontSize="12"
                   Foreground="{StaticResource AppTextDimBrush}" />
    </StackPanel>
</Border>
```

## 3. Server Status Card (Redesigned)

Same position as before (left column, row 1) but with visual upgrades:

### Running Indicator with Pulse
```xml
<StackPanel Orientation="Horizontal" Margin="0,4,0,12">
    <Ellipse x:Name="RunDot" Width="12" Height="12" Margin="0,0,10,0"
             Fill="{Binding State.IsRunning, Converter={StaticResource BoolToColor}}" />
    <TextBlock Text="{Binding State.StatusMessage}" FontSize="15" FontWeight="SemiBold"
               Foreground="{StaticResource AppTextBrush}" />
</StackPanel>
```

In `DashboardView.xaml.cs`: start `PulseAnimation` storyboard on `RunDot` when `State.IsRunning` is true. Stop when false. Use `DataContextChanged` + property listener.

### Info Grid
Same structure but wider label column (100px) and add subtle row separators (1px border between rows, `AppBorderBrush`).

### Admin Badge
Redesign as a `StatusBadge`:
```xml
<Border Style="{StaticResource StatusBadge}"
        Background="{Binding State.IsAdmin, Converter={StaticResource BoolToColor}}"
        Opacity="0.8" HorizontalAlignment="Left" Margin="0,12,0,0">
    <TextBlock Text="{Binding State.IsAdmin, Converter=...}" FontSize="11" Foreground="White" />
</Border>
```

## 4. QR Code Card (Redesigned)

Keep right column but improve:
- Header: "Scan to Connect" centered, 14px semibold
- QR image in white rounded border (same as now)
- **New: Copy button** below QR code

```xml
<Button Style="{StaticResource SecondaryButton}" HorizontalAlignment="Center" Margin="0,8,0,0"
        Command="{Binding CopyQrDataCommand}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#x2398;" FontSize="14" Margin="0,0,6,0" />
        <TextBlock Text="Copy Data" FontSize="12" />
    </StackPanel>
</Button>
```

### DashboardViewModel addition:
```csharp
[RelayCommand]
private void CopyQrData()
{
    var data = _serverService.State.QrData;
    if (!string.IsNullOrEmpty(data))
        System.Windows.Clipboard.SetText(data);
}
```

## 5. Connection Options as Status Pills

Replace bullet-point list with horizontal wrap of status pills:

```xml
<WrapPanel Margin="0,8,0,0">
    <!-- USB Pill -->
    <Border Style="{StaticResource StatusBadge}" Margin="0,0,8,8">
        <StackPanel Orientation="Horizontal">
            <Ellipse Width="6" Height="6" Margin="0,0,6,0"
                     Fill="{Binding State.AdbReverseActive, Converter={StaticResource BoolToColor}}" />
            <TextBlock Text="USB" FontSize="11" Foreground="{StaticResource AppTextBrush}" />
        </StackPanel>
    </Border>
    <!-- LAN Pill -->
    <Border Style="{StaticResource StatusBadge}" Margin="0,0,8,8">
        <StackPanel Orientation="Horizontal">
            <Ellipse Width="6" Height="6" Fill="{StaticResource AppSuccessBrush}" Margin="0,0,6,0" />
            <TextBlock Text="LAN" FontSize="11" Foreground="{StaticResource AppTextBrush}" />
        </StackPanel>
    </Border>
    <!-- WiFi Pill -->
    <Border Style="{StaticResource StatusBadge}" Margin="0,0,8,8">
        <StackPanel Orientation="Horizontal">
            <Ellipse Width="6" Height="6" Margin="0,0,6,0"
                     Fill="{Binding State.IsRunning, Converter={StaticResource BoolToColor}}" />
            <TextBlock FontSize="11" Foreground="{StaticResource AppTextBrush}">
                <Run Text="WiFi " /><Run Text="{Binding State.LocalIp}" FontSize="10"
                     Foreground="{StaticResource AppTextDimBrush}" />
            </TextBlock>
        </StackPanel>
    </Border>
    <!-- Internet Pill (conditional) -->
    <!-- Token display (conditional) -->
</WrapPanel>
```

## 6. Monitors as Horizontal Cards

Replace vertical ItemsControl list with horizontal `WrapPanel` of mini cards:

```xml
<ItemsControl ItemsSource="{Binding State.Monitors}">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <WrapPanel />
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Border Background="{StaticResource AppSurfaceHoverBrush}" CornerRadius="6"
                    Padding="12,8" Margin="0,0,8,8" MinWidth="160">
                <StackPanel>
                    <TextBlock Text="{Binding Name}" FontSize="13" FontWeight="SemiBold"
                               Foreground="{StaticResource AppTextBrush}" />
                    <TextBlock FontSize="12" Foreground="{StaticResource AppTextDimBrush}" Margin="0,2,0,0">
                        <Run Text="{Binding Width}" /><Run Text=" x " /><Run Text="{Binding Height}" />
                    </TextBlock>
                    <Border CornerRadius="3" Padding="4,1" Margin="0,4,0,0"
                            Background="{StaticResource AppSurfaceBrush}" HorizontalAlignment="Left">
                        <TextBlock FontSize="10" Foreground="{StaticResource AppTextDimBrush}"
                                   Text="{Binding IsVirtual, Converter=...}" />
                    </Border>
                </StackPanel>
            </Border>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

## 7. DashboardViewModel Updates

```csharp
// New properties
[ObservableProperty] private int _activeClientCount;
[ObservableProperty] private string _uptime = "00:00:00";

private readonly DispatcherTimer _uptimeTimer;
private DateTime _startTime;

// In constructor:
_startTime = DateTime.UtcNow;
_uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
_uptimeTimer.Tick += (_, _) => {
    Uptime = (DateTime.UtcNow - _startTime).ToString(@"hh\:mm\:ss");
};
_uptimeTimer.Start();

// Track client count from PhaseProtocolHandler events
PhaseProtocolHandler.OnClientConnected += _ => Dispatch(() => ActiveClientCount++);
PhaseProtocolHandler.OnClientDisconnected += _ => Dispatch(() => ActiveClientCount--);
// Initialize from current count
ActiveClientCount = PhaseProtocolHandler.ActiveClients.Count;

[RelayCommand]
private void CopyQrData() { ... }
```

**Note:** Uptime and ActiveClientCount are also shown in status bar (Phase 02). Consider sharing these from MainViewModel or a shared service to avoid duplication (DRY). Best approach: MainViewModel owns uptime/clientCount, DashboardViewModel reads from it via reference. Or both read from ServerState if we add `StartedAt` to ServerState.

**Recommended DRY approach:** Add `StartedAt` (DateTime) property to `ServerState`. Both MainViewModel and DashboardViewModel compute uptime from it. `ActiveClientCount` can be a computed property from `PhaseProtocolHandler.ActiveClients.Count` refreshed by timer.

## Task Checklist

- [ ] Restructure DashboardView.xaml: Grid layout with 3 rows
- [ ] Add KPI tiles row (4 tiles: Clients, Uptime, Encoder, Network)
- [ ] Redesign server status card with pulse animation on running dot
- [ ] Add pulse animation trigger in DashboardView.xaml.cs
- [ ] Redesign QR code card with Copy button
- [ ] Add CopyQrDataCommand in DashboardViewModel
- [ ] Replace connection options list with status pills (WrapPanel)
- [ ] Replace monitors vertical list with horizontal mini-cards (WrapPanel)
- [ ] Add `StartedAt` to ServerState (set on server start)
- [ ] Add ActiveClientCount + Uptime to DashboardViewModel
- [ ] Add uptime DispatcherTimer
- [ ] Wire client count from PhaseProtocolHandler events
