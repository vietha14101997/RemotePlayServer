#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Services;

namespace RemotePlayServer.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ServerService _serverService;

    public List<string> CodecOptions { get; } = new() { "Auto", "H264", "H265" };

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

    public SettingsViewModel(ServerService serverService)
    {
        _serverService = serverService;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        PreferredCodec = _serverService.State.PreferredCodec;
        InternetEnabled = _serverService.State.InternetEnabled;

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
