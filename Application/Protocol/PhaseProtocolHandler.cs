#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    /// <summary>
    /// Connection phase states.
    /// </summary>
    public enum ConnectionPhase
    {
        Connected,
        Phase1_HardwareDetect,
        Phase1_SpeedTest,
        Phase1_WaitingProceed,
        Phase2_ApplyConfig,
        Phase2_IceExchange,
        Phase3_WaitingStart,
        Phase3_Streaming,
        Disconnecting,
        Error
    }

    /// <summary>
    /// Handles the 3-phase connection protocol.
    /// Phase 1: Hardware discovery + Speed test + Suggested config
    /// Phase 2: Display configuration + ICE/WebRTC setup
    /// Phase 3: Streaming
    /// </summary>
    public partial class PhaseProtocolHandler
    {
        // === GUI Connection Tracking ===
        private static readonly ConcurrentDictionary<Guid, Models.ClientConnectionInfo> _activeClients = new();
        private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _clientCts = new();
        public static IReadOnlyDictionary<Guid, Models.ClientConnectionInfo> ActiveClients => _activeClients;
        public static event Action<Models.ClientConnectionInfo>? OnClientConnected;
        public static event Action<Guid>? OnClientDisconnected;
        public static event Action<Guid, ConnectionPhase>? OnClientPhaseChanged;

        public static void RequestDisconnect(Guid clientId)
        {
            if (_clientCts.TryGetValue(clientId, out var cts))
                cts.Cancel();
        }

        private readonly Guid _clientId;
        private readonly WebSocket _ws;
        private readonly System.Net.IPAddress? _remoteIp;
        private readonly CancellationToken _ct;

        private ConnectionPhase _phase = ConnectionPhase.Connected;
        private HardwareInfo? _hardwareInfo;
        private EncoderInfo? _encoderInfo;
        private SpeedTestResult? _speedTestResult;
        private DisplayConfigMessage? _displayConfig;

        // Client codec capabilities (received in hardware_info_ack)
        private ClientCodecCapability? _clientCodecCapability;
        private string _selectedCodec = "H264";
        // Client screen resolution (received in hardware_info_ack, used for resize decision)
        // Screens < 1440p get 50% resize; >= 1440p get original frame
        private int _clientScreenHeight = 0;

        // Capture and streaming resources
        private PerMonitorCapture? _capture;
        private SIPSorceryStreamer? _streamer;
        public SIPSorceryStreamer? Streamer => _streamer;

        /// <summary>Fired when streamer is created and ready (after Phase 2 ICE setup).</summary>
        public event Action<SIPSorceryStreamer>? OnStreamerReady;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;
        private TextureResizer? _textureResizer;

        // Host Desktop/Efficiency mode (Phase 2): host-side adaptive FPS.
        // _clientFpsCeiling = the client-chosen max FPS (the ceiling the controller ramps within).
        // Coordinator is flag-gated (EfficiencyConfig.AdaptiveFpsEnabled); OFF ⇒ pure no-op.
        private volatile int _clientFpsCeiling = 60;
        private AdaptiveFpsCoordinator? _adaptiveFpsCoordinator;

        // ICE handling
        private readonly Dictionary<int, List<string>> _pendingIce = new();
        private readonly HashSet<int> _answersReady = new();
        private readonly object _iceLock = new();
        // Cache all received client ICE candidates for re-application during DTLS retry
        private readonly List<string> _allReceivedIceCandidates = new();

        // WebSocket send lock to prevent concurrent sends
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // Frame timing
        private readonly ConcurrentQueue<(long frameNum, long captureTime)> _frameTiming = new();
        private long _frameCount = 0;

        // Shared capture instance (like Program.cs pattern)
        private static PerMonitorCapture? _sharedCapture;
        private static readonly object _captureLock = new();

        // Monitor list
        private List<(IntPtr hmon, string name, int width, int height)> _monitors = new();

        // Monitor rects for cursor tracking (x, y, w, h) - populated when monitors are refreshed
        private List<(int x, int y, int w, int h)> _monitorRects = new();

        // Foreground window tracking (auto-switch client view)
        private ForegroundWindowTracker? _foregroundTracker;
        private bool _isVrModeActive = false;
        private int _activeMonitorIndex = 0;

        // Cursor tracking (DXGI Desktop Duplication)
        private CancellationTokenSource? _cursorCts;
        private int _lastCursorMonitor = -1;
        private float _lastCursorU = -1f;
        private float _lastCursorV = -1f;
        private bool _lastCursorVisible = false;

        // Cursor image caching - track which cursor shape IDs have been sent to client
        private readonly HashSet<long> _sentCursorIds = new();

        // DXGI Cursor state tracking
        private long _lastDxgiCursorShapeId = -1;
        private readonly object _dxgiCursorLock = new();
        private byte[]? _pendingDxgiCursorBuffer;
        private Vortice.DXGI.OutduplPointerShapeInfo? _pendingDxgiCursorShapeInfo;
        private Vortice.DXGI.OutduplPointerPosition? _pendingDxgiCursorPosition;
        private long _pendingDxgiCursorShapeId;
        private int _pendingDxgiCursorMonitorIndex = -1;

        // Server-side keep-alive for early disconnect detection
        private System.Timers.Timer? _keepAliveTimer;
        private DateTime _lastPongReceived = DateTime.UtcNow;
        private int _missedPongs = 0;
        private const int KEEPALIVE_INTERVAL_MS = 2000;  // Ping every 2s (synchronized with client)
        private const int MAX_MISSED_PONGS = 3;          // 6s without pong = dead (faster detection)

        // Stall detection: track last client feedback for proactive recovery (ticks for thread safety)
        private long _lastClientFeedbackTicks = DateTime.UtcNow.Ticks;
        private volatile bool _feedbackEstablished = false; // true after first fps/quality feedback received

        // Wait for all monitors to connect before starting streaming
        private TaskCompletionSource<bool>? _allConnectedTcs;

        // Fatal error handling: linked to Phase 3 message loop
        private CancellationTokenSource? _fatalErrorCts;

        // Cached offer for video m-line counting
        private string? _lastOfferSdp;
        private volatile bool _dtlsRetrying;

        // Per-track PeerConnection mode: client requests separate video PC per monitor
        private bool _perTrackPc = false;
        private Action<int, string>? _videoIceCandidateHandler; // stored to prevent double-subscription on reconnect

        // Phase 2 restart limit: prevent infinite restart_phase2 ↔ reconnect_required loop
        private int _phase2RestartCount;
        private const int MAX_PHASE2_RESTARTS = 3;

        // When start_streaming arrives during Phase 2 ICE exchange (after restart_phase2),
        // skip WaitForStartStreamingAsync in Phase 3
        private volatile bool _startStreamingReceived;

        // Transport mode (USB Tethering vs WiFi vs Relay)
        private readonly bool _isUsbTransport;
        private readonly bool _isRelayTransport;
        private readonly bool _isViewerMode;

        // Track if display settings were modified (for cleanup)
        // Volatile: read from async cleanup + written from safety monitor callback thread
        private volatile bool _displayModified = false;

        // VDD monitor name for ultrawide/bind_mobile mode (used to filter monitors for capture)
        private string? _ultrawideVddName;

        // Safety monitor for VDD-only mode (Ctrl+Alt+F12 escape + max duration timer)
        private DisplaySafetyMonitor? _safetyMonitor;

        // Buffer for display_config that might arrive before WaitForDisplayConfigAsync is called
        private DisplayConfigMessage? _bufferedDisplayConfig;

        // Phase 5 (ICE restart): DTLS fingerprint captured from the last full offer that
        // established the CURRENT live session (initial handshake, reconnect "offer", or
        // restart_phase2's renegotiated offer). A later "ice_restart_offer" must carry this
        // SAME fingerprint (same DTLS cert/peer) or it is rejected as a possible hijack (F11).
        private string? _liveSessionDtlsFingerprint;

        /// <summary>
        /// Default bitrate for USB mode (higher due to stable bandwidth).
        /// </summary>
        private const int USB_DEFAULT_BITRATE_KBPS = 30000; // 30 Mbps for USB

        /// <summary>
        /// Default bitrate for WiFi mode (lower to handle variable bandwidth).
        /// </summary>
        private const int WIFI_DEFAULT_BITRATE_KBPS = 15000; // 15 Mbps for WiFi

        public PhaseProtocolHandler(
            Guid clientId,
            WebSocket ws,
            System.Net.IPAddress? remoteIp,
            CancellationToken ct,
            bool isUsbTransport = false,
            bool isRelayTransport = false,
            bool isViewerMode = false)
        {
            _clientId = clientId;
            _ws = ws;
            _remoteIp = remoteIp;
            _ct = ct;
            _isUsbTransport = isUsbTransport;
            _isRelayTransport = isRelayTransport;
            _isViewerMode = isViewerMode;
        }

        /// <summary>
        /// Get default bitrate based on transport mode.
        /// </summary>
        public int GetDefaultBitrate() => _isUsbTransport ? USB_DEFAULT_BITRATE_KBPS : WIFI_DEFAULT_BITRATE_KBPS;

        /// <summary>
        /// Main entry point for handling the client connection.
        /// </summary>
        public async Task HandleAsync()
        {
            string transportStr = _isUsbTransport ? "USB Tethering" : "WiFi";
            Logger.Info($"[Protocol] Client {_clientId} connected from {_remoteIp} (v2 protocol, transport={transportStr})");

            var clientInfo = new Models.ClientConnectionInfo
            {
                ClientId = _clientId,
                RemoteIp = _remoteIp?.ToString() ?? (_isRelayTransport ? "Relay" : ""),
                Phase = ConnectionPhase.Connected,
                TransportType = _isRelayTransport ? "Relay" : transportStr,
                IsUsbTransport = _isUsbTransport,
                IsRelayTransport = _isRelayTransport,
                ConnectedAt = DateTime.UtcNow
            };
            _activeClients[_clientId] = clientInfo;
            var disconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            _clientCts[_clientId] = disconnectCts;
            OnClientConnected?.Invoke(clientInfo);

            try
            {
                if (_isViewerMode)
                {
                    // Viewer: skip capture/encode, only ICE + receive pre-encoded frames
                    await RunViewerFastPathAsync();
                }
                else
                {
                    // Host: full pipeline
                    await RunPhase1Async();
                    await RunPhase2Async();
                    await RunPhase3Async();
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"[Protocol] Client {_clientId} cancelled");
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                Logger.Info($"[Protocol] Client {_clientId} disconnected prematurely");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] Client {_clientId} error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.Error($"[Protocol] InnerException: {ex.InnerException.Message}");
                }
                Logger.Error($"[Protocol] StackTrace: {ex.StackTrace}");
                try { await SendErrorAsync(GetPhaseNumber(), "UNEXPECTED_ERROR", ex.Message); } catch { }
            }
            finally
            {
                _activeClients.TryRemove(_clientId, out _);
                if (_clientCts.TryRemove(_clientId, out var removedCts))
                    removedCts.Dispose();
                OnClientDisconnected?.Invoke(_clientId);

                await CleanupAsync();
                _fatalErrorCts?.Dispose();
                _fatalErrorCts = null;
            }
        }
    }
}
