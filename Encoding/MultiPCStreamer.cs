#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using Vortice.Direct3D11;
using RemotePlayServer.Utils;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Multi-PC WebRTC streamer: Creates N separate PeerConnections (one per monitor).
/// This solves the SIPSorcery limitation where SendVideo() broadcasts to all tracks.
/// 
/// Protocol (multiplexed over single WebSocket):
/// - Client: "offer:0:&lt;sdp&gt;" for monitor 0, "offer:1:&lt;sdp&gt;" for monitor 1, etc.
/// - Server: "answer:0:&lt;sdp&gt;", "answer:1:&lt;sdp&gt;", etc.
/// - ICE: "candidate:0:&lt;candidate&gt;", "candidate:1:&lt;candidate&gt;", etc.
/// </summary>
public class MultiPCStreamer : IDisposable
{
    private readonly int _fps;
    private readonly int _kbps;
    private readonly int _monitorCount;
    private ID3D11Device? _device;
    private readonly VideoCodec _preferredCodec;
    
    // Per-monitor devices for parallel encoding
    private readonly Dictionary<int, ID3D11Device> _perMonitorDevices = new();
    
    private readonly List<MonitorPC> _monitors = new();
    private readonly object _lock = new();
    
    private volatile bool _running;
    private volatile bool _disposed;
    
    public bool IsRunning => _running;
    
    public event Action? OnAllConnected;
    public event Action<int, string>? OnIceCandidate; // monitorIndex, candidate
    public event Action<int>? OnPeerDisconnected; // monitorIndex
    public event Action<int>? OnMonitorNeedsReconnect; // monitorIndex - fired when PC closed abnormally, needs re-offer

    /// <summary>
    /// Connection state machine to prevent race conditions in recovery logic.
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,       // Initial state or after failure
        Connecting,         // ICE negotiation in progress
        Connected,          // Fully connected and streaming
        WaitingRecovery,    // Temporary disconnect, waiting for auto-recovery
        Reconnecting        // Recovery failed, requesting full reconnect
    }

    private class MonitorPC : IDisposable
    {
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public RTCPeerConnection? PC { get; set; }
        public ITextureEncoder? Encoder { get; set; }
        public ID3D11Texture2D? StagingNV12 { get; set; }

        // Thread-safe connection state (replaces bool IceConnected)
        private volatile ConnectionState _state = ConnectionState.Disconnected;
        public ConnectionState State
        {
            get => _state;
            set => _state = value;
        }

        public int PcGeneration { get; set; } // Track PC generation to avoid race conditions in callbacks
        public int RecoveryTaskId { get; set; } // Track which recovery task is active to prevent duplicates

        public long SentCount;
        public long SkipCount;
        public long EncodeCount;
        public long LastPts100ns;
        public volatile bool ForceNextKeyframe; // Set by RequestKeyframe, consumed by encoder

        // FPS tracking for diagnostics
        public long LastFpsLogTime;
        public long LastFpsLogSentCount;

        // Adaptive FPS control (based on client feedback)
        public int OriginalFps;           // Original FPS from config
        public int CurrentTargetFps;      // Current encoding target (adjusted based on feedback)
        public int SkipCounter;           // Frame skip counter for encoding throttling
        public int SkipThreshold;         // Skip every N frames (0 = no skip)
        public float LastClientFps;       // Last reported client FPS
        public DateTime LastFpsChange;    // For cooldown between changes

        // Per-monitor D3D11 device for parallel encoding (no contention!)
        public ID3D11Device? Device { get; set; }

        // Helper to check if connected (for backward compatibility)
        public bool IsConnected => _state == ConnectionState.Connected;

        public void Dispose()
        {
            try { Encoder?.Dispose(); } catch { }
            try { StagingNV12?.Dispose(); } catch { }
            try { PC?.close(); } catch { }
            // Note: Don't dispose Device here - it's owned by PerMonitorCapture
        }
    }

    public MultiPCStreamer(int monitorCount, int fps, int kbps, ID3D11Device? device = null, VideoCodec preferredCodec = VideoCodec.H264)
    {
        _monitorCount = monitorCount;
        _fps = fps;
        _kbps = kbps;
        _originalFps = fps; // Store original FPS for adaptive recovery
        _device = device;
        _preferredCodec = preferredCodec;
        _sendLocks = new object[monitorCount];
        for (int i = 0; i < monitorCount; i++)
            _sendLocks[i] = new object();

        Console.WriteLine($"[MultiPC] Created: {monitorCount}mon {fps}fps {kbps}kbps codec={preferredCodec} (parallel device mode)");
    }

    private readonly int _originalFps; // Original FPS for adaptive recovery

    public void SetDevice(ID3D11Device device) => _device = device;
    
    /// <summary>
    /// Set D3D11 device for a specific monitor (for parallel encoding)
    /// This should be called before ProcessOfferAsync for that monitor
    /// </summary>
    public void SetDeviceForMonitor(int monitorIndex, ID3D11Device device)
    {
        lock (_lock)
        {
            // Store in dictionary (will be applied when monitor is created in ProcessOfferAsync)
            _perMonitorDevices[monitorIndex] = device;
            
            // Also apply to existing monitor if already created
            var monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
            if (monitor != null)
            {
                monitor.Device = device;
            }
            Console.WriteLine($"[MultiPC] m{monitorIndex} device registered (parallel mode)");
        }
    }

    /// <summary>
    /// Process offer for a specific monitor and return answer
    /// </summary>
    public async Task<string> ProcessOfferAsync(int monitorIndex, string offerSdp, int width, int height)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitorCount)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex));
        
        Console.WriteLine($"[MultiPC] m{monitorIndex} offer: {width}x{height}");

        MonitorPC monitor;
        RTCPeerConnection? oldPcToClose = null;  // Will be set if we need to close old PC
        lock (_lock)
        {
            // Find or create monitor entry
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex)!;
            if (monitor == null)
            {
                monitor = new MonitorPC
                {
                    Index = monitorIndex,
                    Width = width,
                    Height = height,
                    OriginalFps = _originalFps,       // For adaptive FPS recovery
                    CurrentTargetFps = _originalFps   // Start at full speed
                };

                // Apply per-monitor device if registered (for parallel encoding)
                if (_perMonitorDevices.TryGetValue(monitorIndex, out var perMonDevice))
                {
                    monitor.Device = perMonDevice;
                    Console.WriteLine($"[MultiPC] m{monitorIndex} using dedicated D3D11 device");
                }

                // Set initial state for new monitor
                monitor.State = ConnectionState.Connecting;
                monitor.PcGeneration = 1;  // Start at 1 for first PC

                _monitors.Add(monitor);
            }
            else
            {
                // Check if resolution changed
                bool resolutionChanged = monitor.Width != width || monitor.Height != height;

                // CRITICAL: Increment generation FIRST to invalidate old PC callbacks immediately
                // This prevents race conditions where old PC fires events during close()
                monitor.PcGeneration++;

                // Save old PC reference for cleanup outside lock
                oldPcToClose = monitor.PC;
                monitor.PC = null;  // Clear reference immediately

                // If resolution changed, cleanup encoder and staging texture
                if (resolutionChanged)
                {
                    Console.WriteLine($"[MultiPC] m{monitorIndex} res changed: {monitor.Width}x{monitor.Height} -> {width}x{height}");

                    // Dispose old encoder
                    try { monitor.Encoder?.Dispose(); } catch { }
                    monitor.Encoder = null;

                    // Dispose old staging texture
                    try { monitor.StagingNV12?.Dispose(); } catch { }
                    monitor.StagingNV12 = null;

                    // Clear captured SPS/PPS for this monitor (no longer valid)
                    _capturedSPS.TryRemove(monitorIndex, out _);
                    _capturedPPS.TryRemove(monitorIndex, out _);

                    // Reset counters
                    Interlocked.Exchange(ref monitor.SentCount, 0);
                    Interlocked.Exchange(ref monitor.SkipCount, 0);
                    Interlocked.Exchange(ref monitor.LastPts100ns, 0);
                }

                monitor.Width = width;
                monitor.Height = height;

                // Set state to Connecting and reset recovery task
                monitor.State = ConnectionState.Connecting;
                monitor.RecoveryTaskId++;  // Invalidate any pending recovery tasks
            }
        }

        // Close old PC OUTSIDE lock to avoid blocking, then wait for resources to release
        if (oldPcToClose != null)
        {
            Console.WriteLine($"[MultiPC] m{monitorIndex} closing old PC...");
            oldPcToClose.close();
            // Wait for network resources (UDP ports, DTLS) to fully release
            await Task.Delay(200);
            Console.WriteLine($"[MultiPC] m{monitorIndex} old PC closed, resources released");
        }

        // Create PeerConnection with STUN + TURN servers for robust NAT traversal
        // TURN servers provide relay fallback when direct/STUN connections fail
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                // Google STUN servers (free, global, reliable) - for server reflexive candidates
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },

                // Open Relay TURN servers (free 500MB/month) - for relay candidates
                // Get your API key at: https://www.metered.ca/tools/openrelay/
                // These provide critical fallback when symmetric NAT blocks direct connections
                new RTCIceServer
                {
                    urls = "turn:a.relay.metered.ca:80",
                    username = "83eebabf8b4cce9d5dbcb649",
                    credential = "2D7JvfkOQtBdYW3R"
                },
                new RTCIceServer
                {
                    urls = "turn:a.relay.metered.ca:80?transport=tcp",
                    username = "83eebabf8b4cce9d5dbcb649",
                    credential = "2D7JvfkOQtBdYW3R"
                },
                new RTCIceServer
                {
                    urls = "turn:a.relay.metered.ca:443",
                    username = "83eebabf8b4cce9d5dbcb649",
                    credential = "2D7JvfkOQtBdYW3R"
                },
                new RTCIceServer
                {
                    urls = "turns:a.relay.metered.ca:443?transport=tcp",
                    username = "83eebabf8b4cce9d5dbcb649",
                    credential = "2D7JvfkOQtBdYW3R"
                },
            }
        };
        var pc = new RTCPeerConnection(cfg);
        monitor.PC = pc;

        // Capture current generation for callback validation
        // (generation was already incremented in lock block above)
        int currentGeneration = monitor.PcGeneration;

        // Reset SentCount for new PC so we see "streaming started" again
        Interlocked.Exchange(ref monitor.SentCount, 0);
        Interlocked.Exchange(ref monitor.EncodeCount, 0);

        // Parse H264 from offer
        var (h264Pt, h264Fmtp) = TryGetH264FromOffer(offerSdp);
        
        // Create video track
        var h264Format = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            h264Pt ?? 96,
            "H264",
            90000,
            0,
            string.IsNullOrWhiteSpace(h264Fmtp)
                ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f"
                : h264Fmtp);
        
        var track = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            false,
            new List<SDPAudioVideoMediaFormat> { h264Format },
            MediaStreamStatusEnum.SendOnly);
        
        pc.addTrack(track);

        // ICE gathering complete signal for Vanilla ICE mode
        var gatheringComplete = new TaskCompletionSource<bool>();

        // ICE candidate forwarding
        pc.onicecandidate += (cand) =>
        {
            if (cand == null)
            {
                // null candidate = ICE gathering complete
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE gathering complete");
                gatheringComplete.TrySetResult(true);
            }
            else if (!string.IsNullOrEmpty(cand.candidate))
            {
                OnIceCandidate?.Invoke(monitorIndex, cand.candidate);
            }
        };

        pc.oniceconnectionstatechange += (state) =>
        {
            // Check if this callback is from the current PC (not an old one being closed)
            if (monitor.PcGeneration != currentGeneration)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE {state} (ignored - old PC generation)");
                return;
            }

            if (state == RTCIceConnectionState.connected)
            {
                monitor.State = ConnectionState.Connected;
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE connected");
                CheckAllConnected();
            }
            else if (state == RTCIceConnectionState.disconnected)
            {
                // ICE disconnected can be temporary - don't immediately trigger reconnect
                // Only change state if currently connected (don't override WaitingRecovery)
                if (monitor.State == ConnectionState.Connected)
                {
                    monitor.State = ConnectionState.Disconnected;
                }
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE disconnected (may recover)");
            }
            else if (state == RTCIceConnectionState.failed || state == RTCIceConnectionState.closed)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} ICE {state}");

                // Only set to disconnected if not already in recovery
                if (monitor.State == ConnectionState.Connected || monitor.State == ConnectionState.Connecting)
                {
                    monitor.State = ConnectionState.Disconnected;
                }

                // Use unified recovery handler (prevents race conditions)
                TryStartRecovery(monitor, currentGeneration, $"ICE {state}");
            }
        };

        pc.onconnectionstatechange += (state) =>
        {
            // Check if this callback is from the current PC (not an old one being closed)
            if (monitor.PcGeneration != currentGeneration)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} PC {state} (ignored - old PC generation)");
                return;
            }

            if (state == RTCPeerConnectionState.connected)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} PC connected");
                monitor.State = ConnectionState.Connected;  // Mark as connected
            }
            else if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.failed)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} PC {state}");

                // Only set to disconnected if not already in recovery
                if (monitor.State == ConnectionState.Connected || monitor.State == ConnectionState.Connecting)
                {
                    monitor.State = ConnectionState.Disconnected;
                }

                // Use unified recovery handler (prevents race conditions with ICE handler)
                TryStartRecovery(monitor, currentGeneration, $"PC {state}");
            }
            else if (state != RTCPeerConnectionState.connecting && state != RTCPeerConnectionState.@new)
            {
                Console.WriteLine($"[MultiPC] m{monitorIndex} PC {state}");
            }
        };

        // Set remote offer and create answer
        pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        // Wait for ICE gathering to complete (STUN/TURN candidates gathered)
        // This ensures all candidates are embedded in the answer SDP
        // Use 2 second timeout - if gathering takes longer, continue anyway
        var gatheringTask = gatheringComplete.Task;
        var timeoutTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(gatheringTask, timeoutTask);
        if (completedTask == timeoutTask)
        {
            Console.WriteLine($"[MultiPC] m{monitorIndex} ICE gathering timeout (2s), continuing anyway");
        }

        _running = true;

        // Initialize encoder immediately (don't wait for ICE connected)
        // This ensures both encoders are ready at the same time
        InitializeEncoder(monitor);

        // Get the final SDP with all gathered candidates
        // After gathering, localDescription should have updated SDP with candidates embedded
        var finalSdp = pc.localDescription?.sdp?.ToString() ?? answer.sdp ?? "";
        // Fix: Only add F if not already SAVPF (avoid SAVPF -> SAVPFF bug)
        var answerSdp = finalSdp.Contains("SAVPF") ? finalSdp : finalSdp.Replace("SAVP", "SAVPF");

        // CRITICAL: Ensure SDP has proper payload type info (fixes video not playing)
        var chosenPt = h264Pt ?? 96;
        answerSdp = EnsureVideoMLineHasPayload(answerSdp, chosenPt, h264Fmtp);
        // NOTE: Keep ICE candidates in SDP for Vanilla ICE mode - do NOT filter them out
        // answerSdp = FilterIceCandidates(answerSdp);

        Console.WriteLine($"[MultiPC] m{monitorIndex} answer created (with gathered candidates)");
        return answerSdp;
    }

    /// <summary>
    /// Add ICE candidate for a specific monitor
    /// </summary>
    public void AddIceCandidate(int monitorIndex, string candidate)
    {
        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }
        
        if (monitor?.PC == null) return;
        
        var candStr = candidate.Trim();
        if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
            candStr = candStr.Substring(2);
        if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
            candStr = "candidate:" + candStr;
        
        try
        {
            monitor.PC.addIceCandidate(new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = "0" });
        }
        catch { }
    }

    private void InitializeEncoder(MonitorPC monitor)
    {
        // Use per-monitor device if available, otherwise fall back to shared device
        var device = monitor.Device ?? _device;
        if (device == null || monitor.Encoder != null) return;
        
        lock (_lock)
        {
            if (monitor.Encoder != null) return;
            
            try
            {
                // Detect GPU vendor and select appropriate encoder
                var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();
                ITextureEncoder? encoder = null;
                
                bool usingPerMonitorDevice = monitor.Device != null;
                if (usingPerMonitorDevice)
                {
                    Console.WriteLine($"[MultiPC] m{monitor.Index} using DEDICATED D3D11 device (parallel mode)");
                }
                
                switch (gpuVendor)
                {
                    case GpuVendorDetector.GpuVendor.AMD:
                        // AMD: Use native AMF encoder for best performance
                        Console.WriteLine($"[MultiPC] m{monitor.Index} AMD GPU detected, using AMF encoder");
                        try
                        {
                            var amfEncoder = new AmfNativeWrapper();
                            amfEncoder.OnEncodedData += (nalData, isKeyframe, pts) => 
                                OnEncodedData(monitor, nalData, isKeyframe, pts);
                            
                            if (amfEncoder.Initialize(monitor.Width, monitor.Height, _fps, _kbps, device))
                            {
                                encoder = amfEncoder;
                            }
                            else
                            {
                                amfEncoder.Dispose();
                                Console.WriteLine($"[MultiPC] m{monitor.Index} AMF failed, falling back to LibAv");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MultiPC] m{monitor.Index} AMF exception: {ex.Message}, falling back to LibAv");
                        }
                        break;
                        
                    case GpuVendorDetector.GpuVendor.NVIDIA:
                        Console.WriteLine($"[MultiPC] m{monitor.Index} NVIDIA GPU detected, using NVENC encoder");
                        break;
                        
                    case GpuVendorDetector.GpuVendor.Intel:
                        Console.WriteLine($"[MultiPC] m{monitor.Index} Intel GPU detected, using QSV encoder");
                        break;
                        
                    default:
                        Console.WriteLine($"[MultiPC] m{monitor.Index} Unknown GPU, using LibAv encoder");
                        break;
                }
                
                // Use LibAvEncoder for NVIDIA/Intel/Unknown or as fallback for AMD
                if (encoder == null)
                {
                    var libAvEncoder = new LibAvEncoderAdapter();
                    libAvEncoder.OnEncodedData += (nalData, isKeyframe, pts) =>
                        OnEncodedData(monitor, nalData, isKeyframe, pts);

                    // Pass preferred codec from negotiation
                    Console.WriteLine($"[MultiPC] m{monitor.Index} Initializing LibAvEncoder with codec={_preferredCodec}");
                    if (libAvEncoder.Initialize(monitor.Width, monitor.Height, _fps, _kbps, device, _preferredCodec))
                    {
                        encoder = libAvEncoder;
                        Console.WriteLine($"[MultiPC] m{monitor.Index} LibAvEncoder initialized with codec={libAvEncoder.CurrentCodec}");
                    }
                    else
                    {
                        libAvEncoder.Dispose();
                        Console.WriteLine($"[MultiPC] m{monitor.Index} LibAv encoder init failed");
                    }
                }
                
                if (encoder != null)
                {
                    monitor.Encoder = encoder;
                    Console.WriteLine($"[MultiPC] m{monitor.Index} encoder ready (parallel={usingPerMonitorDevice})");
                }
                else
                {
                    Console.WriteLine($"[MultiPC] m{monitor.Index} encoder init failed - no working encoder found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MultiPC] m{monitor.Index} encoder error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Unified recovery handler to prevent race conditions.
    /// Only one recovery task can be active per monitor at a time.
    /// </summary>
    private void TryStartRecovery(MonitorPC monitor, int currentGeneration, string reason)
    {
        lock (_lock)
        {
            // Only start recovery if:
            // 1. Still running and not disposed
            // 2. Monitor is disconnected (not already recovering/reconnecting)
            // 3. Same PC generation (not already replaced)
            if (!_running || _disposed)
            {
                OnPeerDisconnected?.Invoke(monitor.Index);
                return;
            }

            // If already waiting for recovery or reconnecting, don't start another task
            if (monitor.State == ConnectionState.WaitingRecovery ||
                monitor.State == ConnectionState.Reconnecting)
            {
                Console.WriteLine($"[MultiPC] m{monitor.Index} {reason} - recovery already in progress, skipping");
                return;
            }

            // If PC generation changed, this is an old callback
            if (monitor.PcGeneration != currentGeneration)
            {
                Console.WriteLine($"[MultiPC] m{monitor.Index} {reason} - old generation, skipping");
                return;
            }

            // Set state to waiting recovery and increment task ID
            monitor.State = ConnectionState.WaitingRecovery;
            monitor.RecoveryTaskId++;
            int taskId = monitor.RecoveryTaskId;

            Console.WriteLine($"[MultiPC] m{monitor.Index} {reason}, waiting 3s for recovery...");

            // Start recovery task
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);

                lock (_lock)
                {
                    // Check if this recovery task is still valid
                    if (monitor.RecoveryTaskId != taskId)
                    {
                        Console.WriteLine($"[MultiPC] m{monitor.Index} recovery task {taskId} superseded by {monitor.RecoveryTaskId}");
                        return;
                    }

                    if (!_running || _disposed)
                    {
                        Console.WriteLine($"[MultiPC] m{monitor.Index} stopped, skip reconnect");
                        return;
                    }

                    // Check if recovered (state changed to Connected)
                    if (monitor.State == ConnectionState.Connected)
                    {
                        Console.WriteLine($"[MultiPC] m{monitor.Index} recovered, skip reconnect");
                        return;
                    }

                    // Still in WaitingRecovery - need to reconnect
                    if (monitor.State == ConnectionState.WaitingRecovery)
                    {
                        monitor.State = ConnectionState.Reconnecting;
                        Console.WriteLine($"[MultiPC] m{monitor.Index} still disconnected after 3s, requesting reconnect...");
                    }
                }

                // Fire reconnect event outside lock
                if (monitor.State == ConnectionState.Reconnecting)
                {
                    OnMonitorNeedsReconnect?.Invoke(monitor.Index);
                }
            });
        }
    }

    private void CheckAllConnected()
    {
        lock (_lock)
        {
            if (_monitors.Count >= _monitorCount && _monitors.All(m => m.IsConnected))
                OnAllConnected?.Invoke();
        }
    }

    /// <summary>
    /// Push NV12 texture for a specific monitor - SYNCHRONOUS encoding
    /// Note: Async queue was causing crashes due to texture reuse issues
    /// </summary>
    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed) return;

        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }

        if (monitor == null) return;

        // Check if encoder is ready
        if (monitor.Encoder == null || !monitor.IsConnected)
        {
            Interlocked.Increment(ref monitor.SkipCount);
            return;
        }

        // ADAPTIVE FPS: Check if we should encode this frame based on client feedback
        if (!ShouldEncodeFrame(monitorIndex))
            return;

        // Use per-monitor device for parallel encoding, fallback to shared device
        var device = monitor.Device ?? _device;
        if (device == null)
        {
            Interlocked.Increment(ref monitor.SkipCount);
            return;
        }

        try
        {
            // Use encoder dimensions (from client request), not capture dimensions
            int encWidth = monitor.Width;
            int encHeight = monitor.Height;

            // Create staging texture if needed (use encoder dimensions)
            if (monitor.StagingNV12 == null)
            {
                monitor.StagingNV12 = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)encWidth,
                    Height = (uint)encHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.NV12,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.None
                });
            }

            if (monitor.StagingNV12 != null)
            {
                // Copy texture to staging (this is fast GPU copy)
                device.ImmediateContext.CopyResource(monitor.StagingNV12, nv12Texture);

                // Encode directly (synchronous)
                long sent = Interlocked.Read(ref monitor.SentCount);
                long skipped = Interlocked.Read(ref monitor.SkipCount);

                // Check if client requested keyframe (consume the flag)
                bool clientRequested = monitor.ForceNextKeyframe;
                if (clientRequested)
                    monitor.ForceNextKeyframe = false;

                // Force IDR for:
                // - First 3 frames to ensure SPS/PPS capture
                // - Every 30 frames (1 second at 30fps, 0.5s at 60fps) for responsive visual updates
                // - When too many frames skipped waiting for SPS/PPS
                // - When client explicitly requests (mouse interaction, etc.)
                bool forceIdr = sent < 3 || (sent % 30 == 0) ||
                               (skipped > 0 && skipped <= 10) || clientRequested;

                monitor.Encoder.EncodeTexture(monitor.StagingNV12, forceKeyframe: forceIdr);
            }
        }
        catch { }
    }

    /// <summary>
    /// Push NV12 bytes for a specific monitor - used for NVIDIA compatibility
    /// This path matches how Cluster mode works with LibAv encoder
    /// </summary>
    public void PushNV12Bytes(int monitorIndex, byte[] nv12Bytes, int width, int height)
    {
        if (!_running || _disposed) return;
        
        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }
        if (monitor == null) return;
        
        // Check if encoder is ready
        if (monitor.Encoder == null || !monitor.IsConnected)
        {
            Interlocked.Increment(ref monitor.SkipCount);
            return;
        }
        
        try
        {
            // Use encoder's EncodeNV12Bytes if available (LibAvEncoderAdapter)
            if (monitor.Encoder is LibAvEncoderAdapter libAvEncoder)
            {
                long sent = Interlocked.Read(ref monitor.SentCount);

                // Check if client requested keyframe (consume the flag)
                bool clientRequested = monitor.ForceNextKeyframe;
                if (clientRequested)
                    monitor.ForceNextKeyframe = false;

                bool forceIdr = sent < 3 || (sent % 30 == 0) || clientRequested;
                libAvEncoder.EncodeNV12Bytes(nv12Bytes, width, height, forceIdr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiPC] PushNV12Bytes m{monitorIndex} error: {ex.Message}");
        }
    }

    // Per-monitor locks for SendVideo - avoid global lock blocking all streams
    private readonly object[] _sendLocks;
    
    // Captured SPS/PPS from AMF encoder (per monitor) - will be extracted from first keyframe
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _capturedSPS = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _capturedPPS = new();
    
    private void OnEncodedData(MonitorPC monitor, byte[] nalData, bool isKeyframe, long pts)
    {
        if (!_running || monitor.PC == null || !monitor.IsConnected) return;
        if (monitor.PC.connectionState != RTCPeerConnectionState.connected) return;
        
        // Debug: log first few callbacks per monitor
        long encCount = Interlocked.Increment(ref monitor.EncodeCount);
        if (encCount <= 5)
        {
            var debugNals = ParseNalUnits(nalData);
            var nalTypes = string.Join(",", debugNals.Select(n => n.type));
            Console.WriteLine($"[MultiPC] m{monitor.Index} enc#{encCount}: {nalData.Length}B key={isKeyframe} NALs=[{nalTypes}]");
        }
        
        try
        {
            byte[] au = nalData;
            bool wasAvcc = !HasAnnexBStartCode(au);
            if (wasAvcc)
                au = ConvertAvccToAnnexB(au);
            
            au = StripAud(au);
            
            // Extract and capture SPS/PPS from encoder output
            var nalUnits = ParseNalUnits(au);
            bool hasSps = false, hasPps = false;
            
            foreach (var (type, data) in nalUnits)
            {
                if (type == 7 && !_capturedSPS.ContainsKey(monitor.Index))
                    _capturedSPS[monitor.Index] = data;
                else if (type == 8 && !_capturedPPS.ContainsKey(monitor.Index))
                    _capturedPPS[monitor.Index] = data;
                
                if (type == 7) hasSps = true;
                if (type == 8) hasPps = true;
            }
            
            // If keyframe is missing SPS/PPS, prepend captured ones
            if (isKeyframe && (!hasSps || !hasPps))
            {
                // Try captured SPS/PPS first - must match current resolution
                if (_capturedSPS.TryGetValue(monitor.Index, out var sps) && 
                    _capturedPPS.TryGetValue(monitor.Index, out var pps) &&
                    sps.Length > 10 && pps.Length > 3)
                {
                    var withSpsPps = new byte[sps.Length + pps.Length + au.Length];
                    Buffer.BlockCopy(sps, 0, withSpsPps, 0, sps.Length);
                    Buffer.BlockCopy(pps, 0, withSpsPps, sps.Length, pps.Length);
                    Buffer.BlockCopy(au, 0, withSpsPps, sps.Length + pps.Length, au.Length);
                    au = withSpsPps;
                }
                else
                {
                    // No captured SPS/PPS yet - try to borrow from another monitor with same resolution
                    bool borrowed = false;
                    foreach (var kvp in _capturedSPS)
                    {
                        if (kvp.Key != monitor.Index && 
                            _capturedPPS.TryGetValue(kvp.Key, out var otherPps) &&
                            kvp.Value.Length > 10 && otherPps.Length > 3)
                        {
                            // Use SPS/PPS from another monitor (same resolution assumed)
                            var withSpsPps = new byte[kvp.Value.Length + otherPps.Length + au.Length];
                            Buffer.BlockCopy(kvp.Value, 0, withSpsPps, 0, kvp.Value.Length);
                            Buffer.BlockCopy(otherPps, 0, withSpsPps, kvp.Value.Length, otherPps.Length);
                            Buffer.BlockCopy(au, 0, withSpsPps, kvp.Value.Length + otherPps.Length, au.Length);
                            au = withSpsPps;
                            borrowed = true;
                            break;
                        }
                    }
                    
                    if (!borrowed)
                    {
                        // Use fallback SPS/PPS generator
                        var (fallbackSps, fallbackPps) = GenerateFallbackSpsPps(monitor.Width, monitor.Height);
                        if (fallbackSps != null && fallbackPps != null)
                        {
                            var withSpsPps = new byte[fallbackSps.Length + fallbackPps.Length + au.Length];
                            Buffer.BlockCopy(fallbackSps, 0, withSpsPps, 0, fallbackSps.Length);
                            Buffer.BlockCopy(fallbackPps, 0, withSpsPps, fallbackSps.Length, fallbackPps.Length);
                            Buffer.BlockCopy(au, 0, withSpsPps, fallbackSps.Length + fallbackPps.Length, au.Length);
                            au = withSpsPps;
                        }
                        else
                        {
                            // Last resort: skip frame
                            Interlocked.Increment(ref monitor.SkipCount);
                            return;
                        }
                    }
                }
            }

            uint rtpStep = CalcRtpStep(monitor, pts);
            
            // Use per-monitor lock to avoid blocking other streams
            lock (_sendLocks[monitor.Index])
            {
                monitor.PC.SendVideo(rtpStep, au);
            }
            
            long sent = Interlocked.Increment(ref monitor.SentCount);
            if (sent == 1)
            {
                Console.WriteLine($"[MultiPC] m{monitor.Index} streaming started");
                monitor.LastFpsLogTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                monitor.LastFpsLogSentCount = 0;
            }
            
            // FPS logging every 3 seconds
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long timeSinceLastLog = now - monitor.LastFpsLogTime;
            if (timeSinceLastLog >= 3000 && monitor.LastFpsLogTime > 0)
            {
                long framesSinceLastLog = sent - monitor.LastFpsLogSentCount;
                double fps = framesSinceLastLog * 1000.0 / timeSinceLastLog;
                long skipped = Interlocked.Read(ref monitor.SkipCount);
                Console.WriteLine($"[Encode FPS] Mon{monitor.Index}: {fps:F1} fps (sent {framesSinceLastLog} in {timeSinceLastLog}ms, skipped={skipped})");
                monitor.LastFpsLogTime = now;
                monitor.LastFpsLogSentCount = sent;
            }
        }
        catch { }
    }
    
    // Parse NAL units from Annex-B stream - returns list of (nalType, fullNalWithStartCode)
    private static List<(int type, byte[] data)> ParseNalUnits(byte[] au)
    {
        var result = new List<(int, byte[])>();
        var starts = new List<(int pos, int headerLen)>();
        
        // Find all start codes (00 00 01 or 00 00 00 01)
        for (int i = 0; i < au.Length - 3; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0)
            {
                if (au[i + 2] == 1)
                {
                    starts.Add((i, 3));
                    i += 2; // Skip past start code
                }
                else if (i + 3 < au.Length && au[i + 2] == 0 && au[i + 3] == 1)
                {
                    starts.Add((i, 4));
                    i += 3; // Skip past start code
                }
            }
        }
        
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].pos;
            int headerLen = starts[i].headerLen;
            int end = (i + 1 < starts.Count) ? starts[i + 1].pos : au.Length;
            int nalTypePos = start + headerLen;
            
            if (nalTypePos < au.Length && end > start)
            {
                int nalType = au[nalTypePos] & 0x1F;
                int dataLen = end - start;
                if (dataLen > 0)
                {
                    byte[] nalData = new byte[dataLen];
                    Buffer.BlockCopy(au, start, nalData, 0, dataLen);
                    result.Add((nalType, nalData));
                }
            }
        }
        
        return result;
    }
    
    private static List<int> GetNalTypes(byte[] au)
    {
        var types = new List<int>();
        int pos = 0;
        while (pos < au.Length - 4)
        {
            if (au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 0 && au[pos + 3] == 1)
            {
                pos += 4;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else if (pos + 2 < au.Length && au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 1)
            {
                pos += 3;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else
            {
                pos++;
            }
        }
        return types;
    }
    
    private static string GetNalUnitInfo(byte[] au)
    {
        var types = new List<int>();
        int pos = 0;
        while (pos < au.Length - 4)
        {
            // Find start code
            if (au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 0 && au[pos + 3] == 1)
            {
                pos += 4;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else if (au[pos] == 0 && au[pos + 1] == 0 && au[pos + 2] == 1)
            {
                pos += 3;
                if (pos < au.Length)
                    types.Add(au[pos] & 0x1F);
            }
            else
            {
                pos++;
            }
        }
        return types.Count > 0 ? $"NAL=[{string.Join(",", types)}]" : "NAL=[]";
    }

    private uint CalcRtpStep(MonitorPC monitor, long pts100ns)
    {
        uint fallback = (uint)(90000 / _fps);
        long last = Interlocked.Read(ref monitor.LastPts100ns);
        
        if (last <= 0)
        {
            Interlocked.Exchange(ref monitor.LastPts100ns, pts100ns);
            return fallback;
        }

        long delta = pts100ns - last;
        if (delta <= 0 || delta > 5_000_000)
        {
            Interlocked.Exchange(ref monitor.LastPts100ns, pts100ns);
            return fallback;
        }

        Interlocked.Exchange(ref monitor.LastPts100ns, pts100ns);
        return (uint)Math.Max(1, 90000L * delta / 10_000_000L);
    }

    /// <summary>
    /// Request next frame to be encoded as keyframe (IDR).
    /// Called when client needs immediate visual update (e.g., after mouse interaction).
    /// </summary>
    /// <param name="monitorIndex">Monitor index, or -1 for all monitors</param>
    public void RequestKeyframe(int monitorIndex = -1)
    {
        lock (_lock)
        {
            foreach (var monitor in _monitors)
            {
                if (monitorIndex == -1 || monitor.Index == monitorIndex)
                {
                    monitor.ForceNextKeyframe = true;
                }
            }
        }
    }

    #region Adaptive FPS

    /// <summary>
    /// Process FPS feedback from client and adjust encoding rate.
    /// Uses smooth ramping to avoid jarring changes.
    /// </summary>
    public void ProcessFpsFeedback(int monitorIndex, float clientFps, int droppedFrames)
    {
        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }
        if (monitor == null) return;

        monitor.LastClientFps = clientFps;
        int newTargetFps = CalculateTargetFps(monitor, clientFps, droppedFrames);

        if (newTargetFps != monitor.CurrentTargetFps)
            ApplyFpsChange(monitor, newTargetFps);
    }

    /// <summary>
    /// Algorithm for calculating new target FPS based on client feedback.
    /// </summary>
    private int CalculateTargetFps(MonitorPC monitor, float clientFps, int droppedFrames)
    {
        int current = monitor.CurrentTargetFps > 0 ? monitor.CurrentTargetFps : _originalFps;
        float headroom = clientFps - current;
        float dropRatio = droppedFrames / (float)Math.Max(1, droppedFrames + (int)clientFps);

        const float DROP_THRESHOLD = 0.1f;      // 10% drop rate triggers reduction
        const float RECOVER_HEADROOM = 5.0f;    // 5 FPS headroom to start recovery
        const int MIN_FPS = 15;                 // Never go below 15 FPS
        const int RAMP_STEP = 5;                // Adjust by 5 FPS at a time

        // Cooldown 2s between changes (prevent oscillation)
        if ((DateTime.UtcNow - monitor.LastFpsChange).TotalSeconds < 2.0)
            return current;

        // Client is struggling - reduce target
        if (dropRatio > DROP_THRESHOLD || clientFps < current * 0.8f)
        {
            int newFps = Math.Max(MIN_FPS, current - RAMP_STEP);
            Console.WriteLine($"[AdaptiveFPS] m{monitor.Index}: Reducing {current}->{newFps} (clientFps={clientFps:F1}, drops={dropRatio:P0})");
            return newFps;
        }

        // Client has headroom - try to recover toward original
        if (current < _originalFps && headroom > RECOVER_HEADROOM && dropRatio < 0.02f)
        {
            int newFps = Math.Min(_originalFps, current + RAMP_STEP);
            Console.WriteLine($"[AdaptiveFPS] m{monitor.Index}: Recovering {current}->{newFps} (headroom={headroom:F1})");
            return newFps;
        }

        return current; // No change
    }

    /// <summary>
    /// Apply FPS change with smooth ramping.
    /// </summary>
    private void ApplyFpsChange(MonitorPC monitor, int newTargetFps)
    {
        int oldFps = monitor.CurrentTargetFps > 0 ? monitor.CurrentTargetFps : _originalFps;
        monitor.CurrentTargetFps = newTargetFps;
        monitor.LastFpsChange = DateTime.UtcNow;

        // Calculate skip pattern for encoding throttling
        // E.g., 60fps original, 30fps target = skip every 2nd frame (threshold=1)
        if (newTargetFps < _originalFps && newTargetFps > 0)
            monitor.SkipThreshold = (_originalFps / newTargetFps) - 1;
        else
            monitor.SkipThreshold = 0; // No skipping at full speed

        monitor.SkipCounter = 0;
        Console.WriteLine($"[AdaptiveFPS] m{monitor.Index}: {oldFps}->{newTargetFps} fps (skipThreshold={monitor.SkipThreshold})");
    }

    /// <summary>
    /// Check if frame should be encoded based on adaptive FPS.
    /// Returns true if frame should be encoded, false if skipped.
    /// </summary>
    public bool ShouldEncodeFrame(int monitorIndex)
    {
        MonitorPC? monitor;
        lock (_lock)
        {
            monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
        }
        if (monitor == null || monitor.SkipThreshold <= 0) return true;

        // Apply skip pattern
        monitor.SkipCounter++;
        if (monitor.SkipCounter > monitor.SkipThreshold)
        {
            monitor.SkipCounter = 0;
            return true; // Encode this frame
        }

        return false; // Skip this frame
    }

    /// <summary>
    /// Get current target FPS for a monitor (for diagnostics/acknowledgment).
    /// </summary>
    public int GetCurrentTargetFps(int monitorIndex)
    {
        lock (_lock)
        {
            var monitor = _monitors.FirstOrDefault(m => m.Index == monitorIndex);
            return monitor?.CurrentTargetFps > 0 ? monitor.CurrentTargetFps : _originalFps;
        }
    }

    #endregion

    public void Stop()
    {
        _running = false;

        lock (_lock)
        {
            foreach (var m in _monitors) m.Dispose();
            _monitors.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    #region Fallback SPS/PPS Generation
    
    /// <summary>
    /// Generate fallback SPS/PPS for common resolutions when encoder doesn't output them inline.
    /// SPS/PPS are resolution-specific, so we generate them dynamically.
    /// Profile: Constrained Baseline (42 00), Level: auto-calculated
    /// </summary>
    private static (byte[]? sps, byte[]? pps) GenerateFallbackSpsPps(int width, int height)
    {
        try
        {
            // Calculate level based on resolution (macroblocks per second)
            int mbWidth = (width + 15) / 16;
            int mbHeight = (height + 15) / 16;
            int totalMbs = mbWidth * mbHeight;
            
            // Level calculation based on total macroblocks
            byte level;
            if (totalMbs <= 99) level = 10;           // 176x144 (99 MBs) - Level 1.0
            else if (totalMbs <= 396) level = 13;     // 352x288 (396 MBs) - Level 1.3
            else if (totalMbs <= 792) level = 21;     // 352x576 (792 MBs) - Level 2.1
            else if (totalMbs <= 1620) level = 30;    // 720x480 (1350 MBs) - Level 3.0
            else if (totalMbs <= 3600) level = 31;    // 1280x720 (3600 MBs) - Level 3.1
            else if (totalMbs <= 8192) level = 40;    // 1920x1080 (8160 MBs) - Level 4.0
            else if (totalMbs <= 8704) level = 41;    // 2048x1024 (8192 MBs) - Level 4.1
            else level = 42;                          // 2048x1080+ - Level 4.2
            
            // Generate SPS NAL unit (type 7)
            // Using Constrained Baseline profile (profile_idc=66, constraint_set1=1)
            var spsData = GenerateSpsNal(width, height, level);
            
            // Generate PPS NAL unit (type 8)
            var ppsData = GeneratePpsNal();
            
            // Add Annex-B start codes
            var sps = new byte[4 + spsData.Length];
            sps[0] = 0; sps[1] = 0; sps[2] = 0; sps[3] = 1;
            Buffer.BlockCopy(spsData, 0, sps, 4, spsData.Length);
            
            var pps = new byte[4 + ppsData.Length];
            pps[0] = 0; pps[1] = 0; pps[2] = 0; pps[3] = 1;
            Buffer.BlockCopy(ppsData, 0, pps, 4, ppsData.Length);
            
            return (sps, pps);
        }
        catch
        {
            return (null, null);
        }
    }
    
    /// <summary>
    /// Generate SPS NAL unit data (without start code)
    /// </summary>
    private static byte[] GenerateSpsNal(int width, int height, byte level)
    {
        // Width and height in macroblocks (minus 1 for pic_width/height_in_mbs_minus1)
        int mbWidth = (width + 15) / 16;
        int mbHeight = (height + 15) / 16;
        
        // Calculate cropping if resolution is not multiple of 16
        int cropRight = mbWidth * 16 - width;
        int cropBottom = mbHeight * 16 - height;
        bool needsCrop = cropRight > 0 || cropBottom > 0;
        
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        
        // NAL unit header: forbidden_zero_bit(1) + nal_ref_idc(2) + nal_unit_type(5)
        // 0x67 = 0 11 00111 = SPS with high priority
        bw.Write((byte)0x67);
        
        // profile_idc = 66 (Baseline) - but we use 100 (High) for better compatibility
        bw.Write((byte)100); // High profile
        
        // constraint_set0_flag(1) + constraint_set1_flag(1) + constraint_set2_flag(1) + 
        // constraint_set3_flag(1) + constraint_set4_flag(1) + constraint_set5_flag(1) + reserved(2)
        bw.Write((byte)0x00); // No constraints
        
        // level_idc
        bw.Write(level);
        
        // seq_parameter_set_id = 0 (ue(v) = 1 bit = 1)
        // log2_max_frame_num_minus4 = 0 (ue(v) = 1 bit = 1)  
        // pic_order_cnt_type = 2 (ue(v) = 011 = 3 bits)
        // max_num_ref_frames = 1 (ue(v) = 010 = 3 bits)
        // gaps_in_frame_num_value_allowed_flag = 0 (1 bit)
        // This is simplified - real SPS uses exp-golomb coding
        
        // For High profile, we need to write more fields
        // Simplified High profile SPS with hardcoded values
        byte[] spsPayload;
        if (needsCrop)
        {
            // SPS with cropping for non-16-aligned resolutions
            spsPayload = BuildSpsWithCropping(mbWidth, mbHeight, cropRight / 2, cropBottom / 2);
        }
        else
        {
            // SPS without cropping
            spsPayload = BuildSpsNoCropping(mbWidth, mbHeight);
        }
        
        var result = new byte[4 + spsPayload.Length];
        result[0] = 0x67; // NAL type SPS
        result[1] = 100;  // High profile
        result[2] = 0x00; // Constraints
        result[3] = level;
        Buffer.BlockCopy(spsPayload, 0, result, 4, spsPayload.Length);
        
        return result;
    }
    
    private static byte[] BuildSpsNoCropping(int mbWidth, int mbHeight)
    {
        // Pre-built SPS payload for common resolutions (High profile, no cropping)
        // seq_parameter_set_id=0, log2_max_frame_num=4, pic_order_cnt_type=2, num_ref_frames=1
        // This is a simplified version - encoding exp-golomb properly
        
        var bits = new System.Collections.Generic.List<bool>();
        
        // For High profile: chroma_format_idc = 1 (4:2:0)
        WriteExpGolomb(bits, 1); // chroma_format_idc
        WriteExpGolomb(bits, 0); // bit_depth_luma_minus8
        WriteExpGolomb(bits, 0); // bit_depth_chroma_minus8
        bits.Add(false); // qpprime_y_zero_transform_bypass_flag
        bits.Add(false); // seq_scaling_matrix_present_flag
        
        // log2_max_frame_num_minus4 = 0
        WriteExpGolomb(bits, 0);
        
        // pic_order_cnt_type = 2 (no POC info needed)
        WriteExpGolomb(bits, 2);
        
        // max_num_ref_frames = 1
        WriteExpGolomb(bits, 1);
        
        // gaps_in_frame_num_value_allowed_flag = 0
        bits.Add(false);
        
        // pic_width_in_mbs_minus1
        WriteExpGolomb(bits, mbWidth - 1);
        
        // pic_height_in_map_units_minus1
        WriteExpGolomb(bits, mbHeight - 1);
        
        // frame_mbs_only_flag = 1 (progressive)
        bits.Add(true);
        
        // direct_8x8_inference_flag = 1
        bits.Add(true);
        
        // frame_cropping_flag = 0
        bits.Add(false);
        
        // vui_parameters_present_flag = 0
        bits.Add(false);
        
        return BitsToBytes(bits);
    }
    
    private static byte[] BuildSpsWithCropping(int mbWidth, int mbHeight, int cropRight, int cropBottom)
    {
        var bits = new System.Collections.Generic.List<bool>();
        
        // For High profile
        WriteExpGolomb(bits, 1); // chroma_format_idc = 1 (4:2:0)
        WriteExpGolomb(bits, 0); // bit_depth_luma_minus8
        WriteExpGolomb(bits, 0); // bit_depth_chroma_minus8
        bits.Add(false); // qpprime_y_zero_transform_bypass_flag
        bits.Add(false); // seq_scaling_matrix_present_flag
        
        WriteExpGolomb(bits, 0); // log2_max_frame_num_minus4
        WriteExpGolomb(bits, 2); // pic_order_cnt_type
        WriteExpGolomb(bits, 1); // max_num_ref_frames
        bits.Add(false); // gaps_in_frame_num_value_allowed_flag
        
        WriteExpGolomb(bits, mbWidth - 1); // pic_width_in_mbs_minus1
        WriteExpGolomb(bits, mbHeight - 1); // pic_height_in_map_units_minus1
        
        bits.Add(true); // frame_mbs_only_flag
        bits.Add(true); // direct_8x8_inference_flag
        
        // frame_cropping_flag = 1
        bits.Add(true);
        WriteExpGolomb(bits, 0); // frame_crop_left_offset
        WriteExpGolomb(bits, cropRight); // frame_crop_right_offset
        WriteExpGolomb(bits, 0); // frame_crop_top_offset
        WriteExpGolomb(bits, cropBottom); // frame_crop_bottom_offset
        
        bits.Add(false); // vui_parameters_present_flag
        
        return BitsToBytes(bits);
    }
    
    /// <summary>
    /// Generate PPS NAL unit data (without start code)
    /// </summary>
    private static byte[] GeneratePpsNal()
    {
        // Simple PPS for High profile
        // NAL type = 8 (PPS), nal_ref_idc = 3
        // 0x68 = 0 11 01000
        
        var bits = new System.Collections.Generic.List<bool>();
        
        WriteExpGolomb(bits, 0); // pic_parameter_set_id
        WriteExpGolomb(bits, 0); // seq_parameter_set_id
        bits.Add(false); // entropy_coding_mode_flag (CAVLC)
        bits.Add(false); // bottom_field_pic_order_in_frame_present_flag
        WriteExpGolomb(bits, 0); // num_slice_groups_minus1
        WriteExpGolomb(bits, 0); // num_ref_idx_l0_default_active_minus1
        WriteExpGolomb(bits, 0); // num_ref_idx_l1_default_active_minus1
        bits.Add(false); // weighted_pred_flag
        bits.Add(false); bits.Add(false); // weighted_bipred_idc (2 bits = 0)
        WriteSignedExpGolomb(bits, 0); // pic_init_qp_minus26
        WriteSignedExpGolomb(bits, 0); // pic_init_qs_minus26
        WriteSignedExpGolomb(bits, 0); // chroma_qp_index_offset
        bits.Add(false); // deblocking_filter_control_present_flag
        bits.Add(false); // constrained_intra_pred_flag
        bits.Add(false); // redundant_pic_cnt_present_flag
        
        var ppsPayload = BitsToBytes(bits);
        var result = new byte[1 + ppsPayload.Length];
        result[0] = 0x68; // NAL type PPS
        Buffer.BlockCopy(ppsPayload, 0, result, 1, ppsPayload.Length);
        
        return result;
    }
    
    private static void WriteExpGolomb(System.Collections.Generic.List<bool> bits, int value)
    {
        // Exp-Golomb coding: codeNum = value, code = (leadingZeros, 1, suffix)
        int codeNum = value;
        int leadingZeros = 0;
        int temp = codeNum + 1;
        while (temp > 1)
        {
            temp >>= 1;
            leadingZeros++;
        }
        
        for (int i = 0; i < leadingZeros; i++)
            bits.Add(false);
        
        for (int i = leadingZeros; i >= 0; i--)
            bits.Add(((codeNum + 1) >> i & 1) == 1);
    }
    
    private static void WriteSignedExpGolomb(System.Collections.Generic.List<bool> bits, int value)
    {
        // Signed exp-golomb: map to unsigned
        int mapped = value <= 0 ? -2 * value : 2 * value - 1;
        WriteExpGolomb(bits, mapped);
    }
    
    private static byte[] BitsToBytes(System.Collections.Generic.List<bool> bits)
    {
        // Add RBSP trailing bits (1 followed by zeros to byte align)
        bits.Add(true);
        while (bits.Count % 8 != 0)
            bits.Add(false);
        
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = 0;
            for (int j = 0; j < 8; j++)
            {
                if (bits[i * 8 + j])
                    b |= (byte)(1 << (7 - j));
            }
            bytes[i] = b;
        }
        return bytes;
    }
    
    #endregion

    #region Helpers
    
    private static (int?, string?) TryGetH264FromOffer(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return (null, null);
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            if (!line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("a=rtpmap:".Length);
            var sp = rest.IndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(rest.Substring(0, sp), out var pt)) continue;
            if (rest.Substring(sp + 1).IndexOf("H264/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Find fmtp
                var fmtpPrefix = $"a=fmtp:{pt} ";
                var fmtp = lines.FirstOrDefault(l => l.StartsWith(fmtpPrefix, StringComparison.OrdinalIgnoreCase));
                return (pt, fmtp?.Substring(fmtpPrefix.Length).Trim());
            }
        }
        return (null, null);
    }

    private static string FilterIceCandidates(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return sdp;
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var kept = lines.Where(l => 
            !l.TrimStart().StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
            !l.TrimStart().StartsWith("a=end-of-candidates", StringComparison.OrdinalIgnoreCase));
        return string.Join("\r\n", kept);
    }

    private static bool HasAnnexBStartCode(byte[] d) =>
        (d.Length >= 4 && d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 1) ||
        (d.Length >= 3 && d[0] == 0 && d[1] == 0 && d[2] == 1);

    private static byte[] ConvertAvccToAnnexB(byte[] avcc)
    {
        if (avcc.Length < 8) return avcc;
        try
        {
            var outBuf = new byte[avcc.Length + 32];
            int outPos = 0, pos = 0;
            while (pos + 4 <= avcc.Length)
            {
                int nalLen = (avcc[pos] << 24) | (avcc[pos + 1] << 16) | (avcc[pos + 2] << 8) | avcc[pos + 3];
                pos += 4;
                if (nalLen <= 0 || pos + nalLen > avcc.Length) return avcc;
                if (outPos + 4 + nalLen > outBuf.Length)
                    Array.Resize(ref outBuf, Math.Max(outBuf.Length * 2, outPos + 4 + nalLen));
                outBuf[outPos++] = 0; outBuf[outPos++] = 0; outBuf[outPos++] = 0; outBuf[outPos++] = 1;
                Buffer.BlockCopy(avcc, pos, outBuf, outPos, nalLen);
                outPos += nalLen; pos += nalLen;
            }
            if (outPos <= 0) return avcc;
            var res = new byte[outPos];
            Buffer.BlockCopy(outBuf, 0, res, 0, outPos);
            return res;
        }
        catch { return avcc; }
    }

    private static byte[] StripAud(byte[] au)
    {
        if (au.Length < 4) return au;
        int pos = (au.Length >= 4 && au[0] == 0 && au[1] == 0 && au[2] == 0 && au[3] == 1) ? 4 :
                  (au.Length >= 3 && au[0] == 0 && au[1] == 0 && au[2] == 1) ? 3 : 0;
        if (pos == 0 || pos >= au.Length) return au;
        if ((au[pos] & 0x1F) != 9) return au;
        for (int i = pos + 1; i + 3 < au.Length; i++)
        {
            if ((au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ||
                (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1))
            {
                var trimmed = new byte[au.Length - i];
                Buffer.BlockCopy(au, i, trimmed, 0, trimmed.Length);
                return trimmed;
            }
        }
        return au;
    }

    /// <summary>
    /// Ensures SDP answer has proper m=video line with payload type.
    /// SIPSorcery can generate incomplete SDP that breaks video playback.
    /// </summary>
    private static string EnsureVideoMLineHasPayload(string answerSdp, int pt, string? fmtp)
    {
        if (string.IsNullOrWhiteSpace(answerSdp)) return answerSdp;
        
        var lines = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line == null) continue;
            if (!line.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase)) continue;
            
            // m=video <port> <proto> <fmt> ...
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            // Check if payload type is missing or incomplete
            if (parts.Length <= 3)
            {
                // Add payload type to m-line
                lines[i] = line.TrimEnd() + " " + pt;
                
                // Check for rtpmap and fmtp
                bool hasRtpmap = false;
                bool hasFmtp = false;
                
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var l = lines[j];
                    if (l.StartsWith("m=", StringComparison.OrdinalIgnoreCase)) break;
                    if (l.StartsWith($"a=rtpmap:{pt}", StringComparison.OrdinalIgnoreCase)) hasRtpmap = true;
                    if (l.StartsWith($"a=fmtp:{pt}", StringComparison.OrdinalIgnoreCase)) hasFmtp = true;
                }
                
                // Insert rtpmap and fmtp if missing
                int insertAt = i + 1;
                if (!hasRtpmap)
                {
                    lines.Insert(insertAt++, $"a=rtpmap:{pt} H264/90000");
                }
                if (!hasFmtp)
                {
                    var fmtpVal = string.IsNullOrWhiteSpace(fmtp) 
                        ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f" 
                        : fmtp;
                    lines.Insert(insertAt, $"a=fmtp:{pt} {fmtpVal}");
                }
            }
            break;
        }
        
        return string.Join("\r\n", lines);
    }
    
    #endregion
}
