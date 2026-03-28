# Phase 02: Navigation & Shell Redesign

**Files Modified:** `Views/MainWindow.xaml`, `Views/MainWindow.xaml.cs`, `ViewModels/MainViewModel.cs`
**Files Created:** `Converters/InverseBoolConverter.cs`

---

## 1. MainWindow Layout Restructure

Replace current 3-column grid with a more structured layout:

```
+------------------------------------------+
|  [Sidebar]  |  [Content Area]            |
|             |                            |
|  hamburger  |                            |
|  ---------  |                            |
|  > Dashboard|  (view content)            |
|  Settings   |                            |
|  Connections|                            |
|  Logs       |                            |
|             |                            |
|  ---------  |                            |
|  * Running  |                            |
|             |                            |
+------------------------------------------+
|  [Status Bar]                            |
+------------------------------------------+
```

### Grid Structure
```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="*" />      <!-- main area -->
        <RowDefinition Height="32" />      <!-- status bar -->
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition x:Name="SidebarColumn" Width="200" />  <!-- wider for icons -->
        <ColumnDefinition Width="1" />     <!-- separator -->
        <ColumnDefinition Width="*" />     <!-- content -->
    </Grid.ColumnDefinitions>
    <!-- ... -->
</Grid>
```

## 2. Sidebar Redesign

### 2.1 Header Area (DockPanel.Dock="Top")
- Hamburger button (IconButton style, Unicode `\u2630` or `\u2261`): toggles `IsSidebarCollapsed`
- App title "RemotePlay" (hidden when collapsed)
- Separator line below

### 2.2 Navigation Items
Replace hardcoded ListBoxItem strings with icon+text StackPanels:

```xml
<ListBoxItem>
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#x25A6;" FontSize="16" Width="28" TextAlignment="Center" />
        <TextBlock Text="Dashboard" Visibility="{Binding IsSidebarExpanded, Converter=...}" />
    </StackPanel>
</ListBoxItem>
```

**Nav icons (Unicode):**
| Nav Item | Unicode | Symbol |
|----------|---------|--------|
| Dashboard | `&#x25A6;` | grid/squares |
| Settings | `&#x2699;` | gear |
| Connections | `&#x26A1;` | lightning/link |
| Logs | `&#x1F4CB;` | clipboard (or `&#x2263;` triple bar) |

When sidebar collapsed (Width=56): hide text, show only icon centered.

### 2.3 Server Status Badge (DockPanel.Dock="Bottom")
At bottom of sidebar, above nothing:

```xml
<Border Padding="12,8" Background="{StaticResource AppSurfaceElevatedBrush}" CornerRadius="6" Margin="8">
    <StackPanel Orientation="Horizontal">
        <Ellipse x:Name="StatusDot" Width="8" Height="8" Fill="..." Margin="0,0,8,0" />
        <TextBlock Text="Running" FontSize="11" />  <!-- bound to State.IsRunning -->
    </StackPanel>
</Border>
```

Pulse animation on the Ellipse when `IsRunning=true`. Start/stop via DataTrigger or code-behind.

## 3. Collapsible Sidebar

### MainViewModel additions:
```csharp
[ObservableProperty] private bool _isSidebarExpanded = true;

[RelayCommand]
private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;
```

### XAML approach:
- Bind sidebar `Width` via DataTrigger: expanded=200, collapsed=56
- Use `Storyboard` with `DoubleAnimation` on `ColumnDefinition.Width` is NOT supported in WPF
- **Workaround:** Use a `Border` with fixed width inside the column, animate the Border's width. Or simpler: just snap width (no animation) via DataTrigger on a wrapper Border.
- Hide text labels when collapsed via `BooleanToVisibilityConverter` bound to `IsSidebarExpanded`

### New converter: `Converters/InverseBoolConverter.cs`
Simple `IValueConverter` returning `!bool`. Needed for collapsed-state visibility.

## 4. View Transition (Fade)

In `MainWindow.xaml.cs`, handle ContentControl's `DataContextChanged` or use a custom behavior:

```csharp
// In MainWindow.xaml.cs - subscribe to MainViewModel.PropertyChanged
private void OnCurrentViewChanged()
{
    var storyboard = (Storyboard)FindResource("FadeInAnimation");
    storyboard.Begin(ContentArea); // ContentArea = the ContentControl x:Name
}
```

Give the ContentControl `x:Name="ContentArea"`. Listen to ViewModel's `CurrentView` change via `PropertyChanged` event in code-behind (acceptable for pure UI animation).

## 5. Status Bar (Bottom Row)

New bottom strip spanning all columns:

```xml
<Border Grid.Row="1" Grid.ColumnSpan="3"
        Background="{StaticResource AppSurfaceBrush}"
        BorderBrush="{StaticResource AppBorderBrush}" BorderThickness="0,1,0,0"
        Padding="12,0">
    <DockPanel>
        <!-- Left: Uptime -->
        <TextBlock FontSize="11" Foreground="{StaticResource AppTextDimBrush}" VerticalAlignment="Center">
            <Run Text="Uptime: " /><Run Text="{Binding Uptime}" />
        </TextBlock>

        <!-- Right-aligned items -->
        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
            <TextBlock Text="CPU: --%" FontSize="11" Foreground="{StaticResource AppTextDimBrush}"
                       VerticalAlignment="Center" Margin="16,0" ToolTip="Coming in a future update" />
            <TextBlock Text="GPU: --%" FontSize="11" Foreground="{StaticResource AppTextDimBrush}"
                       VerticalAlignment="Center" Margin="0,0,16,0" ToolTip="Coming in a future update" />
            <TextBlock FontSize="11" Foreground="{StaticResource AppTextDimBrush}" VerticalAlignment="Center">
                <Run Text="Clients: " /><Run Text="{Binding ActiveClientCount}" />
            </TextBlock>
        </StackPanel>

        <TextBlock /> <!-- spacer -->
    </DockPanel>
</Border>
```

### MainViewModel additions for status bar:
```csharp
[ObservableProperty] private string _uptime = "00:00:00";
[ObservableProperty] private int _activeClientCount;
```

Use a `DispatcherTimer` (1s interval) in MainViewModel to update uptime from `_serverService.State`. Bind `ActiveClientCount` from `ConnectionManagerViewModel.Clients.Count` or track via `PhaseProtocolHandler` events.

## 6. System Tray Enhancement

Update tray icon in MainWindow.xaml:

```xml
<tb:TaskbarIcon x:Name="TrayIcon"
                ToolTipText="{Binding TrayTooltip}"
                Visibility="Visible"
                DoubleClickCommand="{Binding ShowWindowCommand}">
    <tb:TaskbarIcon.ContextMenu>
        <ContextMenu>
            <MenuItem Header="Show" Command="{Binding ShowWindowCommand}" />
            <Separator />
            <MenuItem Header="Restart Server" IsEnabled="False"
                      ToolTip="Coming in a future update" />
            <Separator />
            <MenuItem Header="Exit" Command="{Binding ExitAppCommand}" />
        </ContextMenu>
    </tb:TaskbarIcon.ContextMenu>
</tb:TaskbarIcon>
```

### MainViewModel:
```csharp
// Computed from State.IsRunning + ActiveClientCount
public string TrayTooltip => $"RemotePlay Server {(State.IsRunning ? "Running" : "Stopped")} - {ActiveClientCount} client(s)";
```

Update `TrayTooltip` when `IsRunning` or `ActiveClientCount` changes.

## 7. Navigation Routing Update

Add LogViewer as 4th nav item (index 3):

```csharp
// MainViewModel
private readonly LogViewerViewModel _logViewer = new();

partial void OnSelectedNavIndexChanged(int value)
{
    CurrentView = value switch
    {
        0 => _dashboard,
        1 => _settings,
        2 => _connections,
        3 => _logViewer,
        _ => _dashboard
    };
}
```

## 8. Window Size Bump

Increase default size for new layout:
- Default: `Width="1000" Height="650"`
- MinWidth: `750`, MinHeight: `500`

## Task Checklist

- [ ] Restructure MainWindow.xaml grid (2 rows: content + statusbar)
- [ ] Redesign sidebar: hamburger button, icon+text nav items, status badge at bottom
- [ ] Add nav items: Dashboard (&#x25A6;), Settings (&#x2699;), Connections (&#x26A1;), Logs (&#x2263;)
- [ ] Implement sidebar collapse toggle (IsSidebarExpanded in MainViewModel)
- [ ] Create `InverseBoolConverter.cs`
- [ ] Add server status badge at sidebar bottom with pulse animation on running dot
- [ ] Implement status bar (uptime, CPU/GPU placeholders, client count)
- [ ] Add view fade transition in MainWindow.xaml.cs
- [ ] Update tray tooltip to show dynamic status
- [ ] Add "Restart Server" disabled menu item to tray context menu
- [ ] Add LogViewer routing (index 3) in MainViewModel
- [ ] Add uptime DispatcherTimer + ActiveClientCount tracking in MainViewModel
- [ ] Add TrayTooltip computed property
- [ ] Update window default/min sizes
- [ ] Register LogViewerViewModel DataTemplate in ContentControl.Resources
