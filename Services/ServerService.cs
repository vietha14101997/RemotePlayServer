#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Infrastructure.Network.Upnp;
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

    /// <summary>Returns loaded internet config (available after StartAsync completes LoadConfigParallel).</summary>
    public InternetConfig? GetInternetConfig() => _internetConfig;

    /// <summary>Save relay credentials to internet-settings.json.</summary>
    public void SaveRelayCredentials(string relayUrl, string email, string password)
    {
        if (_internetConfig == null) return;
        _internetConfig.RelayUrl = relayUrl;
        _internetConfig.RelayEmail = email;
        _internetConfig.RelayPassword = password;
        _internetConfig.UseRelay = true;
        _ = InternetManager.SaveConfigAsync(_internetConfig);
    }

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
            await KillProcessOnPort(State.Port);
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

            // Discover the UPnP gateway in the background so per-connection
            // WebRTC port mappings (direct P2P from internet) are instant later.
            UpnpPortMappingService.WarmUp();

            UpdateStatus("Pre-warming DTLS & loading config...");
            await LoadConfigParallel();

            await StartTunnelIfEnabled();

            // Relay connection is now deferred until user logs in via LoginView.
            // See ConnectRelayAsync(RelayClient) called from MainViewModel.

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

        try { await UpnpPortMappingService.ShutdownAsync(); } catch { }

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

            try { await VRGameConfig.LoadAsync(); }
            catch (Exception ex) { Logger.Warn($"[VRGameConfig] Load failed: {ex.Message}"); }

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

        // Locally-minted TURN credentials (coturn static-auth-secret) — independent of the relay
        TurnCredentialProvider.Configure(_internetConfig);
        if (TurnCredentialProvider.IsConfigured)
        {
            Logger.Info($"[TURN] Local credential minting enabled for {TurnCredentialProvider.ConfiguredTurnHost}");
            RegisterTurnHostForDetection(TurnCredentialProvider.ConfiguredTurnHost!);
        }
    }

    /// <summary>
    /// Register the coturn host's IP in SIPSorceryStreamer.TurnServerIps so the
    /// P2P-vs-TURN connection-type detection recognizes relayed candidates.
    /// </summary>
    private static void RegisterTurnHostForDetection(string turnHost)
    {
        try
        {
            if (System.Net.IPAddress.TryParse(turnHost, out _))
            {
                SIPSorceryStreamer.TurnServerIps.Add(turnHost);
            }
            else
            {
                foreach (var ip in System.Net.Dns.GetHostAddresses(turnHost))
                    SIPSorceryStreamer.TurnServerIps.Add(ip.ToString());
            }
            Logger.Info($"[TURN] IPs for detection: {string.Join(", ", SIPSorceryStreamer.TurnServerIps)}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[TURN] Could not resolve TURN host '{turnHost}' for detection: {ex.Message}");
        }
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

    /// <summary>
    /// Kill any process holding the given TCP port so HttpListener can bind.
    /// Prevents "conflicts with an existing registration" after unclean shutdown.
    /// </summary>
    private static async Task KillProcessOnPort(int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = $"-ano",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var myPid = Environment.ProcessId;

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port} ") || !line.Contains("LISTENING")) continue;

                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                if (int.TryParse(parts[^1], out var pid) && pid != myPid)
                {
                    if (pid <= 4)
                    {
                        // PID 4 = System (HTTP.sys) — release the URL reservation
                        Logger.Info($"[Port] HTTP.sys holding port {port}, releasing URL reservation...");
                        await ReleaseHttpSysPort(port);
                    }
                    else
                    {
                        try
                        {
                            var target = System.Diagnostics.Process.GetProcessById(pid);
                            Logger.Info($"[Port] Killing process {target.ProcessName} (PID {pid}) holding port {port}");
                            target.Kill();
                            await Task.Delay(500);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Port] Failed to check port {port}: {ex.Message}");
        }
    }

    private static async Task ReleaseHttpSysPort(int port)
    {
        // Stop and restart HTTP.sys to release stale registrations
        var cmds = new[]
        {
            ("net", "stop http /y"),
            ("net", "start http")
        };

        foreach (var (cmd, args) in cmds)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) continue;
                await proc.WaitForExitAsync();
            }
            catch { }
        }

        await Task.Delay(1000);
        Logger.Info($"[Port] HTTP.sys restarted, port {port} should be free");
    }

    /// <summary>
    /// Connect to relay using a pre-authenticated RelayClient (from LoginViewModel).
    /// Registers device, fetches ICE, opens presence WS, creates room.
    /// </summary>
    public async Task ConnectRelayAsync(RelayClient client)
    {
        try
        {
            Logger.Info("[Relay] Starting relay connection with authenticated client...");

            await client.RegisterDeviceAsync(Environment.MachineName);
            await client.FetchIceServersAsync();
            await client.ConnectPresenceAsync();

            // Register guest access
            GuestIdManager.Generate();
            var guestRegistered = false;
            for (int i = 0; i < 3 && !guestRegistered; i++)
            {
                guestRegistered = await client.RegisterGuestDeviceAsync(
                    GuestIdManager.CurrentId, GuestIdManager.CurrentPassword, Environment.MachineName);
                if (!guestRegistered) GuestIdManager.Generate();
            }
            if (guestRegistered)
                Logger.Info($"[Relay] Guest Access: ID={GuestIdManager.DisplayId} Password={GuestIdManager.CurrentPassword}");

            // Set singleton
            RelayClientManager.SetInstance(client);

            // Register TURN server IPs for P2P vs TURN detection
            var iceServers = RelayClientManager.Instance!.IceServers;
            if (iceServers != null)
            {
                foreach (var server in iceServers)
                {
                    foreach (var url in server.Urls)
                    {
                        // Extract IP from "turn:1.2.3.4:3478?transport=udp"
                        var parts = url.Replace("turn:", "").Replace("turns:", "").Split(':');
                        if (parts.Length > 0)
                            SIPSorceryStreamer.TurnServerIps.Add(parts[0]);
                    }
                }
                Logger.Info($"[Relay] TURN IPs for detection: {string.Join(", ", SIPSorceryStreamer.TurnServerIps)}");
            }

            // Setup relay event handlers
            var relayBridge = new MultiClientRelayBridge(client);
            SharedEncoderManager.Initialize();

            client.OnRoomReady += () =>
            {
                var isFirstClient = relayBridge.ActiveCount == 0;
                Logger.Info($"[Relay] Room ready — creating handler (active: {relayBridge.ActiveCount}, role: {(isFirstClient ? "host" : "viewer")})");

                var handlerClientId = Guid.NewGuid();
                var clientIdStr = handlerClientId.ToString();
                var adapter = relayBridge.CreateAdapter(clientIdStr);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var handler = new PhaseProtocolHandler(
                            handlerClientId, adapter, null, CancellationToken.None,
                            isUsbTransport: false, isRelayTransport: true,
                            isViewerMode: !isFirstClient);

                        // Register streamer with SharedEncoderManager when ready
                        handler.OnStreamerReady += streamer =>
                        {
                            var mgr = SharedEncoderManager.Instance;
                            if (mgr == null) return;

                            if (isFirstClient)
                            {
                                mgr.SetHostStreamer(streamer);
                                // Store host config for viewers
                                mgr.SetHostConfig(new HostStreamConfig
                                {
                                    MonitorCount = streamer.MonitorCount,
                                    Codec = streamer.NegotiatedCodec,
                                    ResolutionHeight = streamer.ResolutionHeight,
                                    Fps = streamer.Fps,
                                }, streamer.SharedDevice);
                                Logger.Info("[SharedEncoder] Host streamer + config registered");
                            }
                            else
                            {
                                mgr.AddViewer(handlerClientId, streamer);
                                Logger.Info($"[SharedEncoder] Viewer {handlerClientId} attached");
                            }
                        };

                        await handler.HandleAsync();
                    }
                    finally
                    {
                        var mgr = SharedEncoderManager.Instance;
                        if (mgr != null)
                        {
                            if (isFirstClient)
                                mgr.RemoveHost();
                            else
                                mgr.RemoveViewer(handlerClientId);
                        }
                        relayBridge.RemoveAdapter(clientIdStr);
                        Logger.Info($"[Relay] Handler ended for {clientIdStr} (remaining: {relayBridge.ActiveCount})");
                    }
                });
            };

            client.OnPeerDisconnected += async () =>
            {
                // Regenerate password after each session ends
                GuestIdManager.RegeneratePassword();
                var currentRoomId = State.GuestId?.Replace("-", "");
                if (currentRoomId != null)
                    await client.SetRoomPasswordAsync(currentRoomId, GuestIdManager.CurrentPassword);
                Logger.Info($"[Relay] Guest password regenerated. New password: {GuestIdManager.CurrentPassword}");
                Dispatch(() =>
                {
                    State.GuestPassword = GuestIdManager.CurrentPassword;
                });
            };

            client.OnConnectionStateChanged += (connected) =>
            {
                if (connected)
                {
                    Logger.Info("[Relay] Reconnected to relay server");
                }
            };

            // Create room via relay (relay generates unique room_id)
            var roomId = await client.CreateRoomAsync();
            if (roomId != null)
            {
                // Set password (auto-generated)
                GuestIdManager.Generate(); // reuse for password generation only
                var password = GuestIdManager.CurrentPassword;

                await client.SetRoomPasswordAsync(roomId, password);

                Dispatch(() =>
                {
                    State.GuestId = roomId.Length == 6 ? $"{roomId[..3]}-{roomId[3..]}" : roomId;
                    State.GuestPassword = password;
                });

                Logger.Info($"[Relay] Room ready: ID={State.GuestId} Password={password}");
            }

            Dispatch(() => State.IsRelayConnected = true);

            Logger.Info("[Relay] Connected to relay server");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Relay] Startup error: {ex.Message}");
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
