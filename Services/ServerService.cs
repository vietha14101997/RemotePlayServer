#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Models;
using RemotePlayServer.Server;

namespace RemotePlayServer.Services;

public class ServerService : IDisposable
{
    public ServerState State { get; } = new();

    private SignalServer? _server;
    private CloudflareTunnel? _tunnel;
    private LanDiscoveryService? _discovery;
    private InternetConfig? _internetConfig;
    private bool _disposed;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr hModule);

    public async Task StartAsync()
    {
        try
        {
            UpdateStatus("Checking admin privileges...");
            CheckAdmin();

            UpdateStatus("Checking dependencies...");
            await DependencyManager.EnsureAllAsync();

            var encoder = DetectEncoder();
            Dispatch(() => State.Encoder = encoder);
            Logger.Info($"[Encoder] {encoder}");

            UpdateStatus("Configuring firewall...");
            int discoveryPort = LanDiscoveryService.DefaultDiscoveryPort;
            FirewallHelper.EnsureRules(State.Port, discoveryPort);

            UpdateStatus("Detecting USB...");
            DetectUsb();

            DisplayGuard.CaptureSnapshotAtStartup();
            EnsureStartupRecoveryTask();

            UpdateStatus("Enumerating monitors...");
            RefreshMonitors();

            SetupAdbReverse();

            UpdateStatus("Starting signal server...");
            _server = new SignalServer($"http://+:{State.Port}/");
            var monitors = WgcInterop.ListMonitorsDXGI();
            _server.SetWindows(Win32.ListTopLevelWindows()
                .Where(w => !string.IsNullOrWhiteSpace(w.title))
                .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
                .ToList());
            _server.SetMonitors(monitors.Select(m => (m.hmon, m.name, m.width, m.height)).ToList());
            await _server.StartAsync();

            var preferredIP = NetUtil.GetPreferredLocalIP();
            Dispatch(() => State.LocalIp = preferredIP);
            Logger.Info($"[HTTP] Server: {preferredIP}:{State.Port}");

            UpdateStatus("Pre-warming DTLS & loading config...");
            await LoadConfigParallel();

            await StartTunnelIfEnabled();

            BuildQrData();

            UpdateStatus("Starting LAN discovery...");
            _discovery = new LanDiscoveryService(State.LocalIp, State.Port, discoveryPort);
            _discovery.Start();

            Dispatch(() =>
            {
                State.IsRunning = true;
                State.StartedAt = DateTime.UtcNow;
                State.StatusMessage = "Server running";
            });

            Logger.Info("[Server] Ready and running");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Server] Startup failed: {ex.Message}", ex);
            Dispatch(() => State.StatusMessage = $"Startup failed: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        Dispatch(() => State.StatusMessage = "Shutting down...");

        try
        {
            if (_server != null)
            {
                await _server.StopAsync();
                Logger.Info("[Shutdown] Server stopped");
            }

            VirtualDisplayManager.RestoreDpiSettings();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Shutdown] Server stop error: {ex.Message}");
        }

        _discovery?.Dispose();
        _discovery = null;

        AdbPortForwardHelper.RemoveReverse();

        if (_tunnel != null)
        {
            _tunnel.Dispose();
            _tunnel = null;
            Logger.Info("[Shutdown] Tunnel stopped");
        }

        try { await SignalServer.ForceCleanupResources(); } catch { }

        Dispatch(() =>
        {
            State.IsRunning = false;
            State.StatusMessage = "Server stopped";
        });

        Logger.Info("[Shutdown] Server exited");
    }

    private void CheckAdmin()
    {
        bool isAdmin = false;
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        Dispatch(() => State.IsAdmin = isAdmin);

        if (!isAdmin)
            Logger.Warn("[System] Not running as Administrator. Some features may be limited.");
        else
            Logger.Info("[System] Running as Administrator: YES");
    }

    private string DetectEncoder()
    {
        var amfPaths = new[]
        {
            "amfrt64.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "amfrt64.dll"),
        };
        foreach (var path in amfPaths)
        {
            try
            {
                IntPtr handle = LoadLibrary(path);
                if (handle != IntPtr.Zero) { FreeLibrary(handle); return "AMD AMF (Hardware)"; }
            }
            catch { }
        }

        var nvencPaths = new[]
        {
            "nvEncodeAPI64.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvEncodeAPI64.dll"),
        };
        foreach (var path in nvencPaths)
        {
            try
            {
                IntPtr handle = LoadLibrary(path);
                if (handle != IntPtr.Zero) { FreeLibrary(handle); return "NVIDIA NVENC (Hardware)"; }
            }
            catch { }
        }

        return "FFmpeg x264 (Software)";
    }

    private void DetectUsb()
    {
        var usbTetherInfo = UsbTetheringHelper.Detect();
        if (usbTetherInfo.IsAvailable)
        {
            Dispatch(() => State.UsbTetheringIp = usbTetherInfo.ServerIP);
            Logger.Info($"[USB] USB Tethering detected: {usbTetherInfo.ServerIP}");
        }
    }

    public void RefreshMonitors()
    {
        var monitors = WgcInterop.ListMonitorsDXGI();

        // Sort by X position (left-to-right) so Dashboard shows correct arrangement
        var sorted = monitors
            .Select(m =>
            {
                var (x, y, _, _, ok) = DisplayUtil.TryGetLayout(m.name);
                return (mon: m, x: ok ? x : int.MaxValue);
            })
            .OrderBy(item => item.x)
            .Select(item => item.mon)
            .ToList();

        var monitorInfos = new ObservableCollection<MonitorInfo>(
            sorted.Select(m => new MonitorInfo
            {
                Name = m.name,
                Width = m.width,
                Height = m.height,
                IsVirtual = DisplayUtil.IsVirtualDisplay(m.name, m.hmon)
            }));

        Dispatch(() => State.Monitors = monitorInfos);
    }

    private void SetupAdbReverse()
    {
        if (AdbPortForwardHelper.IsAdbAvailable() && AdbPortForwardHelper.IsDeviceConnected())
        {
            if (AdbPortForwardHelper.SetupReverse(State.Port))
            {
                Dispatch(() => State.AdbReverseActive = true);
                Logger.Info($"[USB] ADB reverse ACTIVE: localhost:{State.Port} via USB cable");
            }
        }

        if (!State.AdbReverseActive && State.UsbTetheringIp == null)
            Logger.Info("[USB] No USB connection. Plug in USB cable for USB streaming.");
    }

    private async Task LoadConfigParallel()
    {
        var dtlsTask = Task.Run(() => SIPSorceryStreamer.PreWarmDtls());

        var configTask = Task.Run(async () =>
        {
            try { _internetConfig = await InternetManager.LoadConfigAsync(); }
            catch (Exception ex) { Logger.Warn($"[Internet] Config load failed: {ex.Message}"); }

            try
            {
                var codecConfigPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "codec-settings.json");
                if (File.Exists(codecConfigPath))
                {
                    var json = await File.ReadAllTextAsync(codecConfigPath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("preferredCodec", out var codecProp))
                    {
                        var codec = codecProp.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(codec))
                        {
                            DisplayConfig.PreferredCodec = codec;
                            Dispatch(() => State.PreferredCodec = codec);
                            Logger.Info($"[Codec] Preferred codec: {codec}");
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Warn($"[Codec] Config load failed: {ex.Message}"); }
        });

        await Task.WhenAll(dtlsTask, configTask);

        Dispatch(() => State.InternetEnabled = _internetConfig?.Enabled == true);
    }

    private async Task StartTunnelIfEnabled()
    {
        if (_internetConfig?.Enabled != true) return;

        try
        {
            UpdateStatus("Starting tunnel...");
            _tunnel = new CloudflareTunnel();
            var tunnelUrl = await _tunnel.StartAsync(State.Port);
            if (tunnelUrl != null)
            {
                string? authToken = null;
                if (_internetConfig.RequireToken)
                    authToken = AuthTokenManager.GenerateToken();

                Dispatch(() =>
                {
                    State.TunnelUrl = tunnelUrl;
                    State.AuthToken = authToken;
                });
                Logger.Info($"[Tunnel] Public URL: {tunnelUrl}");
            }
            else
            {
                Logger.Warn("[Tunnel] Failed to start. LAN/USB modes still available.");
                _tunnel.Dispose();
                _tunnel = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Tunnel] Error: {ex.Message}");
            _tunnel?.Dispose();
            _tunnel = null;
        }
    }

    private void BuildQrData()
    {
        string usbIPJson = State.UsbTetheringIp != null ? $",\"usbIP\":\"{State.UsbTetheringIp}\"" : "";
        string tunnelJson = State.TunnelUrl != null ? $",\"tunnelUrl\":\"{State.TunnelUrl}\"" : "";
        string qrData = $"{{\"ip\":\"{State.LocalIp}\",\"port\":\"{State.Port}\"{usbIPJson}{tunnelJson}}}";

        Dispatch(() => State.QrData = qrData);
        Logger.Info($"[QR] Data: {qrData}");
    }

    private void EnsureStartupRecoveryTask()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var taskName = "RemotePlayServerDisplayRecovery";
            var args = $"/Create /TN \"{taskName}\" /TR \"\\\"{exePath}\\\" --restore-if-needed\" /SC ONSTART /RU SYSTEM /F";

            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);

            Logger.Info("[System] Startup recovery task registered.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[System] Recovery task registration failed: {ex.Message}");
        }
    }

    private void UpdateStatus(string message)
    {
        Dispatch(() => State.StatusMessage = message);
        Logger.Info(message);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _discovery?.Dispose();
        _tunnel?.Dispose();
        _server = null;
    }
}
