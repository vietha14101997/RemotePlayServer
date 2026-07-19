#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Services;

namespace RemotePlayServer.ViewModels;

public record RunningApp(IntPtr Hwnd, string Title, string ProcessName)
{
    public string DisplayName => $"{Title} [{ProcessName}]";
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ServerService _serverService;

    public List<string> CodecOptions { get; } = new() { "Auto", "H264", "H265" };

    // Streaming mode (Gaming = low-latency 60fps; Efficiency = adaptive FPS, desktop/text).
    public List<string> StreamModeOptions { get; } = new() { "Gaming", "Efficiency" };
    [ObservableProperty] private string _selectedStreamMode = "Gaming";
    [ObservableProperty] private bool _isSavingStreamMode;

    [ObservableProperty] private string _preferredCodec = "Auto";
    [ObservableProperty] private bool _internetEnabled;
    [ObservableProperty] private string _turnServerUrl = "";
    [ObservableProperty] private string _turnUsername = "";
    [ObservableProperty] private string _turnPassword = "";
    [ObservableProperty] private bool _requireToken = true;
    [ObservableProperty] private bool _autoStartEnabled;
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private bool _isSavingCodec;
    [ObservableProperty] private bool _isSavingInternet;
    [ObservableProperty] private bool _isSavingVRGames;
    
    [ObservableProperty] private ObservableCollection<RunningApp> _runningApps = new();
    [ObservableProperty] private ObservableCollection<string> _favoriteVRGames = new();
    [ObservableProperty] private RunningApp? _selectedRunningApp;
    [ObservableProperty] private string? _selectedFavoriteVRGame;
    [ObservableProperty] private string _customGameName = "";

    public SettingsViewModel(ServerService serverService)
    {
        _serverService = serverService;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        PreferredCodec = _serverService.State.PreferredCodec;
        InternetEnabled = _serverService.State.InternetEnabled;
        SelectedStreamMode = EfficiencyConfig.Mode.ToString();
        
        FavoriteVRGames = new ObservableCollection<string>(VRGameConfig.VRGames);
        RefreshRunningApps();

        var config = InternetManager.Instance?.Config;
        if (config != null)
        {
            TurnServerUrl = config.TurnServerUrl ?? "";
            TurnUsername = config.TurnUsername ?? "";
            TurnPassword = config.TurnPassword ?? "";
            RequireToken = config.RequireToken;
        }

        CheckAutoStartState();
    }

    [RelayCommand]
    private async Task SaveCodecAsync()
    {
        IsSavingCodec = true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "codec-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new { preferredCodec = PreferredCodec },
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
            DisplayConfig.PreferredCodec = PreferredCodec;
            _serverService.State.PreferredCodec = PreferredCodec;
            SetStatusWithAutoClear($"Codec saved: {PreferredCodec}");
            Logger.Info($"[Settings] Codec changed to {PreferredCodec}");
        }
        catch (Exception ex)
        {
            SetStatusWithAutoClear($"Error: {ex.Message}");
            Logger.Error($"[Settings] Codec save failed: {ex.Message}");
        }
        finally { IsSavingCodec = false; }
    }

    [RelayCommand]
    private async Task SaveStreamModeAsync()
    {
        IsSavingStreamMode = true;
        try
        {
            var mode = Enum.TryParse<StreamMode>(SelectedStreamMode, out var m) ? m : StreamMode.Gaming;
            // Runtime-safe: updates derived flags; a live coordinator picks them up next tick
            // (no PeerConnection teardown). Also persists to efficiency-settings.json.
            await Task.Run(() => EfficiencyConfig.SetMode(mode));
            SetStatusWithAutoClear($"Streaming mode: {mode}");
            Logger.Info($"[Settings] Streaming mode changed to {mode}");
        }
        catch (Exception ex)
        {
            SetStatusWithAutoClear($"Error: {ex.Message}");
            Logger.Error($"[Settings] Streaming mode save failed: {ex.Message}");
        }
        finally { IsSavingStreamMode = false; }
    }

    [RelayCommand]
    private void RefreshRunningApps()
    {
        RunningApps.Clear();
        try
        {
            var apps = Win32.ListRunningApplications();
            var sorted = apps
                .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var app in sorted)
            {
                RunningApps.Add(new RunningApp(app.Hwnd, app.Title, app.ProcessName));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] Failed to refresh running apps: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddFavorite()
    {
        if (SelectedRunningApp == null) return;
        var procName = SelectedRunningApp.ProcessName;
        if (!FavoriteVRGames.Contains(procName))
        {
            FavoriteVRGames.Add(procName);
        }
    }

    [RelayCommand]
    private void RemoveFavorite()
    {
        if (string.IsNullOrEmpty(SelectedFavoriteVRGame)) return;
        FavoriteVRGames.Remove(SelectedFavoriteVRGame);
    }

    [RelayCommand]
    private void AddCustomFavorite()
    {
        if (string.IsNullOrWhiteSpace(CustomGameName)) return;
        var trimmed = CustomGameName.Trim();
        if (!FavoriteVRGames.Contains(trimmed))
        {
            FavoriteVRGames.Add(trimmed);
        }
        CustomGameName = "";
    }

    [RelayCommand]
    private void MoveFavoriteUp()
    {
        if (string.IsNullOrEmpty(SelectedFavoriteVRGame)) return;
        int index = FavoriteVRGames.IndexOf(SelectedFavoriteVRGame);
        if (index > 0)
        {
            var item = SelectedFavoriteVRGame;
            FavoriteVRGames.RemoveAt(index);
            FavoriteVRGames.Insert(index - 1, item);
            SelectedFavoriteVRGame = item;
        }
    }

    [RelayCommand]
    private void MoveFavoriteDown()
    {
        if (string.IsNullOrEmpty(SelectedFavoriteVRGame)) return;
        int index = FavoriteVRGames.IndexOf(SelectedFavoriteVRGame);
        if (index >= 0 && index < FavoriteVRGames.Count - 1)
        {
            var item = SelectedFavoriteVRGame;
            FavoriteVRGames.RemoveAt(index);
            FavoriteVRGames.Insert(index + 1, item);
            SelectedFavoriteVRGame = item;
        }
    }

    [RelayCommand]
    private async Task SaveVRGamesListAsync()
    {
        IsSavingVRGames = true;
        try
        {
            await VRGameConfig.SaveAsync(FavoriteVRGames);
            SetStatusWithAutoClear("VR games list saved successfully");
        }
        catch (Exception ex)
        {
            SetStatusWithAutoClear($"Error: {ex.Message}");
            Logger.Error($"[Settings] VR games list save failed: {ex.Message}");
        }
        finally { IsSavingVRGames = false; }
    }

    [RelayCommand]
    private async Task SaveInternetSettingsAsync()
    {
        IsSavingInternet = true;
        try
        {
            var config = new InternetConfig
            {
                Enabled = InternetEnabled,
                TurnServerUrl = string.IsNullOrWhiteSpace(TurnServerUrl) ? null : TurnServerUrl,
                TurnUsername = string.IsNullOrWhiteSpace(TurnUsername) ? null : TurnUsername,
                TurnPassword = string.IsNullOrWhiteSpace(TurnPassword) ? null : TurnPassword,
                RequireToken = RequireToken
            };
            await InternetManager.SaveConfigAsync(config);
            SetStatusWithAutoClear("Internet settings saved (restart to apply tunnel changes)");
            Logger.Info("[Settings] Internet settings saved");
        }
        catch (Exception ex)
        {
            SetStatusWithAutoClear($"Error: {ex.Message}");
            Logger.Error($"[Settings] Internet settings save failed: {ex.Message}");
        }
        finally { IsSavingInternet = false; }
    }

    [RelayCommand]
    private void ToggleAutoStart()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            if (AutoStartEnabled)
            {
                var args = $"/Create /TN \"RemotePlayServer\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL LIMITED /F";
                RunSchtasks(args);
                SaveStatus = "Auto-start enabled";
            }
            else
            {
                RunSchtasks("/Delete /TN \"RemotePlayServer\" /F");
                SaveStatus = "Auto-start disabled";
            }
        }
        catch (Exception ex)
        {
            SaveStatus = $"Auto-start error: {ex.Message}";
            Logger.Error($"[Settings] Auto-start toggle failed: {ex.Message}");
        }
    }

    private void CheckAutoStartState()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"RemotePlayServer\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            p?.WaitForExit(3000);
            AutoStartEnabled = p?.ExitCode == 0;
        }
        catch
        {
            AutoStartEnabled = false;
        }
    }

    [RelayCommand]
    private async Task SaveDisplayConfigAsDefaultAsync()
    {
        try
        {
            // Snapshot current display state
            var monitors = Infrastructure.Capture.WgcInterop.ListMonitorsDXGI();
            int physicalCount = 0;
            int totalCount = monitors.Count;
            int width = 1920, height = 1080, refreshRate = 60;

            foreach (var mon in monitors)
            {
                if (!Infrastructure.Display.DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon))
                {
                    physicalCount++;
                    width = mon.width;
                    height = mon.height;
                    var mode = Infrastructure.Display.DisplayUtil.GetCurrentMode(mon.name);
                    if (mode.Frequency > 0) refreshRate = mode.Frequency;
                }
            }

            // Get current DPI scale from first monitor
            int scalePercent = 100;
            var dpiInfos = Infrastructure.Display.DpiScalingHelper.GetAllMonitorsDpiInfo();
            if (dpiInfos.Count > 0)
                scalePercent = (int)dpiInfos[0].info.Current;

            var config = new
            {
                monitorCount = totalCount,
                refreshRate,
                width,
                height,
                scalePercent,
                monitorType = DisplayConfig.MonitorType,
                preferredCodec = DisplayConfig.PreferredCodec,
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "display-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);

            // Apply to runtime
            DisplayConfig.MonitorCount = totalCount;
            DisplayConfig.RefreshRate = refreshRate;

            // Reset display snapshot to current state
            Infrastructure.Display.DisplayGuard.ResetAndCaptureSnapshot();

            SetStatusWithAutoClear($"Display config saved: {totalCount} monitors, {width}x{height}@{refreshRate}Hz, {scalePercent}%");
            Logger.Info($"[Settings] Display config saved as default: {json}");
        }
        catch (Exception ex)
        {
            SetStatusWithAutoClear($"Error: {ex.Message}");
            Logger.Error($"[Settings] Display config save failed: {ex.Message}");
        }
    }

    private void SetStatusWithAutoClear(string msg)
    {
        SaveStatus = msg;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { SaveStatus = ""; timer.Stop(); };
        timer.Start();
    }

    private static void RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        Process.Start(psi)?.WaitForExit(5000);
    }
}
