# Phase 04: Settings, Connections & Placeholder Views

**Files Modified:** `Views/SettingsView.xaml`, `Views/ConnectionManagerView.xaml`, `ViewModels/SettingsViewModel.cs`
**Files Created:** `Views/LogViewerView.xaml`, `ViewModels/LogViewerViewModel.cs`

---

## 1. Settings View Redesign

### 1.1 Layout Structure

Replace cramped form with grouped sections in a wider layout:

```
+--------------------------------------------------+
| SETTINGS                                          |
+--------------------------------------------------+
| [Codec Settings Card]                             |
|   Preferred Codec: [ComboBox]  [Save]             |
|   hint text                                       |
+--------------------------------------------------+
| [Internet Mode Card]                              |
|   Enable: [ToggleSwitch]                          |
|   TURN Server: [TextBox]                          |
|   Username: [TextBox]                             |
|   Password: [PasswordBox]    <- mask password     |
|   Require Token: [ToggleSwitch]                   |
|   [Save Internet Settings]                        |
+--------------------------------------------------+
| [System Card]                                     |
|   Start with Windows: [ToggleSwitch]              |
+--------------------------------------------------+
| [Performance Card] - COMING SOON                  |
| [Display Card] - COMING SOON                      |
| [Audio Card] - COMING SOON                        |
| [Security Card] - COMING SOON                     |
| [Advanced Card] - COMING SOON                     |
+--------------------------------------------------+
```

### 1.2 Form Layout Improvements

- Wider label column: 140px
- Form fields: max-width 320px for TextBox/ComboBox
- Each section has header (SectionHeader style) + description text (InfoLabel, italic)
- Save button: `PrimaryButton` style with ProgressRing overlay during async save

### 1.3 Save Button with Loading State

Add `IsSaving` bool property to SettingsViewModel:

```csharp
[ObservableProperty] private bool _isSavingCodec;
[ObservableProperty] private bool _isSavingInternet;

[RelayCommand]
private async Task SaveCodecAsync()
{
    IsSavingCodec = true;
    try { /* existing logic */ }
    finally { IsSavingCodec = false; }
}
```

In XAML, button content swaps between text and MahApps ProgressRing:
```xml
<Button Style="{StaticResource PrimaryButton}" Command="{Binding SaveCodecCommand}" Padding="16,6">
    <Grid>
        <TextBlock Text="Save" Visibility="{Binding IsSavingCodec, Converter={StaticResource InverseBoolToVis}}" />
        <mah:ProgressRing Width="16" Height="16" IsActive="{Binding IsSavingCodec}"
                          Visibility="{Binding IsSavingCodec, Converter={StaticResource BoolToVis}}" />
    </Grid>
</Button>
```

### 1.4 Password Field

Replace TURN password TextBox with PasswordBox. Since PasswordBox doesn't support binding directly, use MahApps `PasswordBoxBindingBehavior`:

```xml
<PasswordBox mah:PasswordBoxHelper.CapsLockWarningToolTip="Caps Lock On"
             mah:TextBoxHelper.Watermark="Enter password"
             Width="320" />
```

For binding, use attached behavior or keep code-behind event (acceptable for security control).

### 1.5 Placeholder Settings Sections

5 "coming soon" cards using `PlaceholderCard` style from Phase 01. Each has:
- Section header
- 2-3 fake form rows (greyed labels + disabled controls)
- "COMING SOON" badge top-right

```xml
<!-- Performance (Coming Soon) -->
<Grid Margin="0,0,0,12">
    <Border Style="{StaticResource PlaceholderCard}">
        <StackPanel>
            <TextBlock Text="Performance" Style="{StaticResource SectionHeader}" />
            <TextBlock Text="Bitrate limits, FPS caps, resolution scaling"
                       Style="{StaticResource InfoLabel}" Margin="0,4,0,8" FontStyle="Italic" />
            <StackPanel Orientation="Horizontal" Margin="0,4">
                <TextBlock Text="Max Bitrate" Width="140" Style="{StaticResource InfoLabel}" VerticalAlignment="Center" />
                <TextBlock Text="10 Mbps" Style="{StaticResource InfoValue}" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,4">
                <TextBlock Text="FPS Limit" Width="140" Style="{StaticResource InfoLabel}" VerticalAlignment="Center" />
                <TextBlock Text="60" Style="{StaticResource InfoValue}" />
            </StackPanel>
        </StackPanel>
    </Border>
    <!-- Coming Soon badge overlay -->
    <Border Background="{StaticResource AppComingSoonBrush}" CornerRadius="4"
            Padding="8,3" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,-6,8,0">
        <TextBlock Text="COMING SOON" FontSize="9" FontWeight="Bold" Foreground="White" />
    </Border>
</Grid>
```

**Placeholder sections (same pattern):**

| Section | Description | Fake fields |
|---------|-------------|-------------|
| Performance | Bitrate limits, FPS caps, resolution scaling | Max Bitrate: 10 Mbps, FPS Limit: 60 |
| Display | Virtual display management, monitor arrangement | Virtual Display: Off, Resolution: Native |
| Audio | Audio routing, volume, device selection | Device: Default, Volume: 100% |
| Security | Whitelist IPs, session timeout, encryption | Session Timeout: 30 min, Encryption: AES-256 |
| Advanced | Log level, debug mode, experimental features | Log Level: Info, Debug Mode: Off |

### 1.6 Save Status Feedback

Move `SaveStatus` display into a toast-like notification at top of settings:

```xml
<Border Visibility="{Binding SaveStatus, Converter={StaticResource NullToVis}}"
        Background="{StaticResource AppAccentBrush}" CornerRadius="6" Padding="12,8" Margin="0,0,0,12"
        Opacity="0.9">
    <TextBlock Text="{Binding SaveStatus}" Foreground="White" FontSize="12" />
</Border>
```

Auto-clear after 3 seconds via timer in SettingsViewModel.

## 2. Connection Manager Redesign

### 2.1 Enhanced Client Cards

Replace current flat layout with visually richer cards:

```xml
<Border Style="{StaticResource CardBorder}">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />  <!-- disconnect button -->
        </Grid.ColumnDefinitions>

        <StackPanel>
            <!-- Row 1: IP + Transport badge + Phase badge -->
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding RemoteIp}" FontSize="15" FontWeight="SemiBold"
                           Foreground="{StaticResource AppTextBrush}" />
                <!-- Transport badge -->
                <Border Style="{StaticResource StatusBadge}" Margin="10,0,0,0"
                        Background="{Binding IsUsbTransport, Converter=...}">
                    <TextBlock Text="{Binding TransportType}" FontSize="10" Foreground="White" />
                </Border>
                <!-- Phase badge (color-coded) -->
                <Border Style="{StaticResource StatusBadge}" Margin="6,0,0,0">
                    <TextBlock Text="{Binding Phase}" FontSize="10"
                               Foreground="{Binding Phase, Converter={StaticResource PhaseToColor}}" />
                </Border>
            </StackPanel>

            <!-- Row 2: Phase timeline dots -->
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <!-- 5 phase dots: Connected -> Phase1 -> Phase2 -> Phase3 -> Streaming -->
                <!-- Filled dots for completed phases, outlined for pending -->
            </StackPanel>

            <!-- Row 3: Codec + Duration + placeholder stats -->
            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                <TextBlock FontSize="12" Foreground="{StaticResource AppTextDimBrush}">
                    <Run Text="Codec: " /><Run Text="{Binding Codec}" Foreground="{StaticResource AppTextBrush}" />
                </TextBlock>
                <TextBlock FontSize="12" Foreground="{StaticResource AppTextDimBrush}" Margin="16,0">
                    <Run Text="Duration: " /><Run Text="{Binding Duration, StringFormat=hh\\:mm\\:ss}"
                         Foreground="{StaticResource AppTextBrush}" />
                </TextBlock>
            </StackPanel>

            <!-- Row 4: Streaming stats placeholder -->
            <Border Background="{StaticResource AppSurfaceHoverBrush}" CornerRadius="4"
                    Padding="8,4" Margin="0,8,0,0" Opacity="0.5">
                <TextBlock Text="FPS / Bitrate / Latency -- stats available in future update"
                           FontSize="10" Foreground="{StaticResource AppTextDimBrush}" FontStyle="Italic" />
            </Border>
        </StackPanel>

        <!-- Disconnect button (right side, vertically centered) -->
        <Button Grid.Column="1" Style="{StaticResource DangerButton}" VerticalAlignment="Center"
                Command="..." CommandParameter="{Binding ClientId}"
                Content="Disconnect" Padding="12,6" />
    </Grid>
</Border>
```

### 2.2 Phase Timeline Visualization

5 dots representing major phases. Use a simple horizontal StackPanel with Ellipses connected by lines:

```xml
<!-- Phase timeline: 5 dots connected by 4 lines -->
<StackPanel Orientation="Horizontal">
    <Ellipse Width="8" Height="8" Fill="..." />      <!-- Connected -->
    <Rectangle Width="20" Height="2" Fill="..." />
    <Ellipse Width="8" Height="8" Fill="..." />      <!-- Phase 1 -->
    <Rectangle Width="20" Height="2" Fill="..." />
    <Ellipse Width="8" Height="8" Fill="..." />      <!-- Phase 2 -->
    <Rectangle Width="20" Height="2" Fill="..." />
    <Ellipse Width="8" Height="8" Fill="..." />      <!-- Phase 3 -->
    <Rectangle Width="20" Height="2" Fill="..." />
    <Ellipse Width="8" Height="8" Fill="..." />      <!-- Streaming -->
</StackPanel>
```

Create a new converter `PhaseToTimelineConverter` that takes the current `ConnectionPhase` and a `ConverterParameter` (target phase index) and returns:
- `AppAccentBrush` if phase reached/passed
- `AppBorderBrush` if phase not yet reached

Or simpler: use multi DataTriggers in an ItemsControl with hardcoded 5 items.

**Recommended simpler approach:** Create a `PhaseToProgressConverter` that returns an int (0-4) for how far along the client is. Use DataTrigger on each dot/line to set fill color.

### 2.3 Empty State Enhancement

Add a subtle icon above "No active connections":
```xml
<TextBlock Text="&#x1F50C;" FontSize="48" HorizontalAlignment="Center" Margin="0,0,0,12"
           Foreground="{StaticResource AppTextDimBrush}" Opacity="0.3" />
```

## 3. Log Viewer Placeholder

### 3.1 LogViewerView.xaml (new file)

```xml
<UserControl x:Class="RemotePlayServer.Views.LogViewerView" ...>
    <Grid Margin="24">
        <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
            <TextBlock Text="&#x1F4CB;" FontSize="64" HorizontalAlignment="Center"
                       Foreground="{StaticResource AppTextDimBrush}" Opacity="0.3" />
            <TextBlock Text="Log Viewer" FontSize="22" FontWeight="SemiBold"
                       Foreground="{StaticResource AppTextBrush}" HorizontalAlignment="Center"
                       Margin="0,16,0,8" />
            <TextBlock Text="View server logs, connection events, and diagnostics"
                       FontSize="13" Foreground="{StaticResource AppTextDimBrush}"
                       HorizontalAlignment="Center" />
            <Border Background="{StaticResource AppComingSoonBrush}" CornerRadius="4"
                    Padding="12,6" HorizontalAlignment="Center" Margin="0,20,0,0">
                <TextBlock Text="COMING SOON" FontSize="11" FontWeight="Bold" Foreground="White" />
            </Border>

            <!-- Mockup of what it will look like -->
            <Border Style="{StaticResource PlaceholderCard}" Margin="0,24,0,0"
                    Width="500" HorizontalAlignment="Center">
                <StackPanel>
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                        <TextBlock Text="Filter:" FontSize="11" Foreground="{StaticResource AppTextDimBrush}"
                                   VerticalAlignment="Center" Margin="0,0,8,0" />
                        <Border CornerRadius="4" Background="{StaticResource AppSurfaceHoverBrush}"
                                Padding="8,4" Margin="0,0,4,0">
                            <TextBlock Text="All" FontSize="10" Foreground="{StaticResource AppTextDimBrush}" />
                        </Border>
                        <Border CornerRadius="4" Background="{StaticResource AppSurfaceHoverBrush}"
                                Padding="8,4" Margin="0,0,4,0">
                            <TextBlock Text="Error" FontSize="10" Foreground="{StaticResource AppErrorBrush}" />
                        </Border>
                        <Border CornerRadius="4" Background="{StaticResource AppSurfaceHoverBrush}"
                                Padding="8,4">
                            <TextBlock Text="Warning" FontSize="10" Foreground="{StaticResource AppWarningBrush}" />
                        </Border>
                    </StackPanel>
                    <!-- Fake log lines -->
                    <TextBlock FontSize="11" Foreground="{StaticResource AppTextDimBrush}" FontFamily="Consolas">
                        <Run Text="12:00:01 [INFO]  Server started on port 8288" /><LineBreak />
                        <Run Text="12:00:02 [INFO]  LAN discovery active" /><LineBreak />
                        <Run Text="12:00:15 [INFO]  Client connected: 192.168.1.5" /><LineBreak />
                        <Run Text="12:01:03 [WARN]  Encoder fallback: NVENC -> FFmpeg" Foreground="{StaticResource AppWarningBrush}" />
                    </TextBlock>
                </StackPanel>
            </Border>
        </StackPanel>
    </Grid>
</UserControl>
```

### 3.2 LogViewerViewModel.cs (new file)

Minimal -- just needs to exist for DataTemplate resolution:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemotePlayServer.ViewModels;

public partial class LogViewerViewModel : ObservableObject { }
```

## 4. New Converter

### PhaseToProgressConverter.cs

Returns int (0-4) based on ConnectionPhase enum value. Used by phase timeline dots to determine fill color via DataTrigger.

```csharp
public class PhaseToProgressConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ConnectionPhase phase && parameter is string indexStr && int.TryParse(indexStr, out var targetIndex))
        {
            int currentProgress = phase switch
            {
                ConnectionPhase.Connected => 0,
                ConnectionPhase.Phase1_HardwareDetect or ConnectionPhase.Phase1_SpeedTest
                    or ConnectionPhase.Phase1_WaitingProceed => 1,
                ConnectionPhase.Phase2_ApplyConfig or ConnectionPhase.Phase2_IceExchange => 2,
                ConnectionPhase.Phase3_WaitingStart => 3,
                ConnectionPhase.Phase3_Streaming => 4,
                _ => 0
            };
            return currentProgress >= targetIndex;  // returns bool: is this dot active?
        }
        return false;
    }
}
```

In XAML, each dot uses: `Fill="{Binding Phase, Converter={StaticResource PhaseToProgress}, ConverterParameter=2}"` with a `BoolToColorConverter` chained or a DataTrigger.

## Task Checklist

### Settings
- [ ] Restructure SettingsView.xaml layout (wider labels, max-width fields)
- [ ] Apply PrimaryButton style to Save buttons
- [ ] Add IsSavingCodec/IsSavingInternet props + ProgressRing on save buttons
- [ ] Replace TURN password TextBox with PasswordBox
- [ ] Add save status toast banner at top (auto-clear 3s)
- [ ] Add 5 placeholder "coming soon" sections (Performance, Display, Audio, Security, Advanced)
- [ ] Each placeholder uses PlaceholderCard style + COMING SOON badge

### Connections
- [ ] Redesign client cards: IP + transport/phase badges, timeline dots, stats placeholder
- [ ] Create `PhaseToProgressConverter.cs`
- [ ] Implement phase timeline visualization (5 dots + connecting lines)
- [ ] Add streaming stats placeholder row per client card
- [ ] Enhance empty state with icon
- [ ] Use DangerButton style for Disconnect

### Log Viewer
- [ ] Create `Views/LogViewerView.xaml` placeholder with mockup
- [ ] Create `ViewModels/LogViewerViewModel.cs` (empty ObservableObject)
- [ ] Register LogViewerView DataTemplate in MainWindow.xaml ContentControl.Resources

### SettingsViewModel
- [ ] Add `IsSavingCodec`, `IsSavingInternet` bool properties
- [ ] Add auto-clear timer for SaveStatus (3 seconds)
- [ ] Wrap save commands in try/finally for IsSaving state
