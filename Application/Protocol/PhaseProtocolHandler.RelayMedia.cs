#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Network.Telemetry;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    internal static class RelayClientGate
    {
        public static async Task<bool> OpenAsync(
            Func<Task<bool>> sendStartAsync,
            Func<bool> enableRelay,
            Func<Task> rollbackStartAsync)
        {
            if (!await sendStartAsync().ConfigureAwait(false)) return false;

            // Activation exceptions are owned by the caller, which must quiesce any
            // partially started producers before closing the client gate.
            if (enableRelay()) return true;

            await rollbackStartAsync().ConfigureAwait(false);
            return false;
        }
    }

    internal sealed class RelayVideoFrame
    {
        public RelayVideoFrame(
            int trackIndex,
            List<byte[]> chunks,
            bool isKeyframe,
            int recoveryGeneration,
            int relayGeneration = 0)
        {
            TrackIndex = trackIndex;
            Chunks = chunks;
            IsKeyframe = isKeyframe;
            RecoveryGeneration = recoveryGeneration;
            RelayGeneration = relayGeneration;
        }

        public int TrackIndex { get; }
        public List<byte[]> Chunks { get; }
        public bool IsKeyframe { get; }
        public int RecoveryGeneration { get; }
        public int RelayGeneration { get; }
    }

    internal sealed class RelayKeyframeRecoveryState
    {
        private readonly int[] _taintGeneration;
        private readonly int[] _recoveredGeneration;

        public RelayKeyframeRecoveryState(int trackCount)
        {
            _taintGeneration = new int[Math.Max(1, trackCount)];
            _recoveredGeneration = new int[_taintGeneration.Length];
        }

        public int CurrentGeneration(int trackIndex) =>
            IsValidTrack(trackIndex) ? Volatile.Read(ref _taintGeneration[trackIndex]) : 0;

        public bool IsWaitingForKeyframe(int trackIndex) =>
            IsValidTrack(trackIndex) &&
            Volatile.Read(ref _taintGeneration[trackIndex]) != Volatile.Read(ref _recoveredGeneration[trackIndex]);

        public void MarkTainted(int trackIndex)
        {
            if (IsValidTrack(trackIndex))
                Interlocked.Increment(ref _taintGeneration[trackIndex]);
        }

        public bool ShouldSuppress(RelayVideoFrame frame) =>
            !frame.IsKeyframe && IsWaitingForKeyframe(frame.TrackIndex);

        public bool TryCompleteKeyframe(RelayVideoFrame frame)
        {
            if (!frame.IsKeyframe || !IsValidTrack(frame.TrackIndex)) return false;
            if (Volatile.Read(ref _taintGeneration[frame.TrackIndex]) != frame.RecoveryGeneration) return false;

            Volatile.Write(ref _recoveredGeneration[frame.TrackIndex], frame.RecoveryGeneration);
            return true;
        }

        private bool IsValidTrack(int trackIndex) => (uint)trackIndex < (uint)_taintGeneration.Length;
    }

    internal sealed class RelayMediaWorkerSession : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private int _stopped;

        public RelayMediaWorkerSession(int generation, int trackCount, int videoCapacity, int audioCapacity,
            CancellationToken parentToken)
        {
            Generation = generation;
            Recovery = new RelayKeyframeRecoveryState(trackCount);
            for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
                Recovery.MarkTainted(trackIndex);
            VideoQueue = Channel.CreateBounded<RelayVideoFrame>(new BoundedChannelOptions(videoCapacity)
            {
                SingleReader = true,
                SingleWriter = false
            });
            AudioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(audioCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }

        public int Generation { get; }
        public RelayKeyframeRecoveryState Recovery { get; }
        public Channel<RelayVideoFrame> VideoQueue { get; }
        public Channel<byte[]> AudioQueue { get; }
        public CancellationToken Token => _cts.Token;
        public Task VideoTask { get; private set; } = Task.CompletedTask;
        public Task AudioTask { get; private set; } = Task.CompletedTask;

        public void Start(
            Func<ChannelReader<RelayVideoFrame>, RelayMediaWorkerSession, Task> videoLoop,
            Func<ChannelReader<byte[]>, RelayMediaWorkerSession, Task> audioLoop)
        {
            VideoTask = Task.Run(() => videoLoop(VideoQueue.Reader, this));
            AudioTask = Task.Run(() => audioLoop(AudioQueue.Reader, this));
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            VideoQueue.Writer.TryComplete();
            AudioQueue.Writer.TryComplete();
            _cts.Cancel();
            try
            {
                await Task.WhenAll(VideoTask, AudioTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose() => _cts.Dispose();
    }

    internal sealed class RelayP2PState
    {
        private readonly object _lock = new();
        private bool _connected;
        private int _version;

        public bool IsConnected
        {
            get { lock (_lock) return _connected; }
        }

        public int MarkConnected()
        {
            lock (_lock)
            {
                if (!_connected)
                {
                    _connected = true;
                    _version++;
                }
                return _version;
            }
        }

        public int MarkTerminalFailure()
        {
            lock (_lock)
            {
                if (_connected || _version == 0)
                {
                    _connected = false;
                    _version++;
                }
                return _version;
            }
        }

        public bool IsCurrent(int version, bool connected)
        {
            lock (_lock) return _version == version && _connected == connected;
        }

        public bool TryGetCurrentVersion(bool connected, out int version)
        {
            lock (_lock)
            {
                version = _version;
                return _connected == connected;
            }
        }

        public bool TryRunIfCurrent(int version, bool connected, Action action)
        {
            lock (_lock)
            {
                if (_version != version || _connected != connected) return false;
                action();
                return true;
            }
        }
    }

    // DERP-style media relay fallback. When WebRTC ICE fails after the restart
    // budget is spent (both peers behind symmetric CGNAT), media flows
    // host -> relay(WebSocket) -> client instead of over WebRTC. Only activates on
    // an already-dead WebRTC path, so it can never regress a working P2P session.
    public partial class PhaseProtocolHandler
    {
        private volatile bool _mediaRelayMode;
        private readonly RelayP2PState _p2pState = new();
        private int _mediaRelayWired; // 0/1 — subscribe streamer relay events only once
        private readonly SemaphoreSlim _mediaRelayLifecycle = new(1, 1);
        private int _dtlsFailCount;   // host-side DTLS/ICE failures observed this session
        private CancellationTokenSource? _relayFallbackCts;

        // Bounded frame-atomic video send queue. A single consumer preserves chunk
        // order; when the TCP relay can't drain fast enough, WHOLE frames are dropped
        // at enqueue time (never individual chunks — a missing chunk corrupts the NAL
        // and poisons every following P-frame) and a recovery IDR is requested.
        private RelayMediaWorkerSession? _relayWorkers;
        private int _relayGeneration;
        private long _lastRelayKeyframeReqMs;
        private const int RELAY_QUEUE_FRAMES = 4;
        private const int RELAY_AUDIO_QUEUE_PACKETS = 4; // 40ms at 10ms PCM packets

        // Fall back to media relay after this many DTLS/ICE failures. 1 = on the first
        // failure: the initial ICE attempt already tried every P2P path (incl. IPv6), so
        // its failure means direct won't work here — no point waiting for slow restarts.
        private const int DERP_AFTER_DTLS_FAILURES = 1;

        // P2P head-start before relay kicks in (Tailscale-style, but P2P-preferred):
        // LAN/IPv6 connect in ~1-2s and win outright; if nothing has connected by this
        // deadline we bring up the relay fast instead of waiting the ~30s ICE timeout.
        // Background ICE keeps trying and auto-upgrades to P2P if it ever connects.
        private const int RELAY_FALLBACK_DELAY_MS = 8000;

        /// <summary>
        /// Arm the P2P head-start timer when ICE exchange begins. If no WebRTC connection
        /// lands within RELAY_FALLBACK_DELAY_MS, start the media relay. Cancelled the moment
        /// P2P connects (OnAllTracksReady) or if relay is entered another way.
        /// </summary>
        private void ArmRelayFallbackTimer()
        {
            _relayFallbackCts?.Cancel();
            var cts = new CancellationTokenSource();
            _relayFallbackCts = cts;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(RELAY_FALLBACK_DELAY_MS, cts.Token); }
                catch (OperationCanceledException) { return; }
                if (cts.IsCancellationRequested) return;
                if (_p2pState.IsConnected || _mediaRelayMode) return;
                try
                {
                    // Phase 1 pairing gate: EnterMediaRelayModeAsync() enforces IsPeerAuthorized()
                    // itself (single source of truth) and defers/no-ops if unpaired — checked
                    // here too so the log line doesn't claim "starting" when it will actually
                    // be deferred pending a pairing_client_proof.
                    if (!IsPeerAuthorized())
                    {
                        Logger.Warn($"[RelayMedia] No P2P after {RELAY_FALLBACK_DELAY_MS}ms, but session isn't paired-bound yet — deferring media relay entry");
                        await EnterMediaRelayModeAsync(); // sends pairing_required + stashes the resume
                        return;
                    }

                    Logger.Info($"[RelayMedia] No P2P after {RELAY_FALLBACK_DELAY_MS}ms — starting media relay (P2P keeps trying in background)");
                    await EnterMediaRelayModeAsync();
                }
                catch (Exception ex) { Logger.Error($"[RelayMedia] Fallback timer error: {ex.Message}"); }
            });
        }

        private void CancelRelayFallbackTimer()
        {
            _relayFallbackCts?.Cancel();
            _relayFallbackCts = null;
        }

        public bool IsMediaRelayMode => _mediaRelayMode;

        /// <summary>
        /// Switch to media-over-relay: drive the encode pipeline without WebRTC and
        /// pump framed chunks / PCM to the client over the room WebSocket. Idempotent.
        /// </summary>
        private async Task EnterMediaRelayModeAsync(int? expectedP2PVersion = null)
        {
            var streamer = _streamer;
            if (streamer == null)
            {
                Logger.Warn("[RelayMedia] Cannot enter relay mode — no streamer");
                return;
            }

            // Phase 1 pairing gate (BLOCKER fix): this fallback bypasses the P2P DTLS-connect
            // flow entirely — Phase2's OnAllTracksReady (and thus EnsurePairedBeforeMediaAsync)
            // may never fire if WebRTC never connects, and it sets _startStreamingReceived +
            // jumps straight to Phase 3 below, which would otherwise skip every other gate too.
            // No-op when RequirePairing is OFF (EnsurePairedSync returns true immediately).
            // Unpaired ⇒ pairing_required already sent, deferred here; resumed (or the whole
            // relay entry re-attempted) from HandlePairingClientProofAsync on a successful proof.
            if (!EnsurePairedSync(PendingResumeKind.EnterRelay))
            {
                Logger.Warn("[RelayMedia] Session not paired-bound — deferring media relay entry until pairing completes");
                return;
            }

            await _mediaRelayLifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_p2pState.TryGetCurrentVersion(connected: false, out int p2pVersion)) return;
                if (expectedP2PVersion.HasValue && expectedP2PVersion.Value != p2pVersion) return;
                if (_mediaRelayMode) return;

                RelayMediaWorkerSession? workers = null;
                bool workersPublished = false;
                bool clientGateOpen = false;
                try
                {
                    CancelRelayFallbackTimer();

                    workers = new RelayMediaWorkerSession(
                        Interlocked.Increment(ref _relayGeneration),
                        _monitors.Count,
                        RELAY_QUEUE_FRAMES,
                        RELAY_AUDIO_QUEUE_PACKETS,
                        _ct);

                    int monitors = _monitors.Count;
                    string startMessage =
                        $"{{\"type\":\"media_relay_start\",\"monitors\":{monitors},\"codec\":\"{_selectedCodec}\"}}";

                    async Task RollbackClientGateAsync()
                    {
                        if (!clientGateOpen) return;
                        await TrySendTextAsync("{\"type\":\"media_relay_stop\"}").ConfigureAwait(false);
                        clientGateOpen = false;
                    }

                    // The client must install its binary demux gate before active capture can make
                    // StartRelayMediaMode emit an IDR. The P2P state lock makes the generation check
                    // and relay publication one transition, so a recovery cannot commit between them.
                    bool relayEnabled = await RelayClientGate.OpenAsync(
                        async () =>
                        {
                            clientGateOpen = await TrySendTextAsync(startMessage).ConfigureAwait(false);
                            return clientGateOpen;
                        },
                        () => _p2pState.TryRunIfCurrent(p2pVersion, connected: false, () =>
                        {
                            // Wire encoder -> relay exactly once. No relay callback can enqueue until
                            // _mediaRelayMode and the new generation are published below.
                            if (Interlocked.Exchange(ref _mediaRelayWired, 1) == 0)
                            {
                                streamer.OnRelayVideoFrame += EnqueueRelayVideoFrame;
                                streamer.OnRelayVideoFrameDropped += HandleRelayVideoFrameDropped;
                                streamer.OnRelayAudioChunk += EnqueueRelayAudioChunk;
                            }

                            _relayWorkers = workers;
                            workersPublished = true;
                            _mediaRelayMode = true;
                            workers.Start(RelayVideoSendLoopAsync, RelayAudioSendLoopAsync);
                            streamer.StartRelayMediaMode();
                        }),
                        RollbackClientGateAsync).ConfigureAwait(false);

                    if (!relayEnabled)
                    {
                        workers.Dispose();
                        Logger.Warn("[RelayMedia] Relay entry abandoned before media activation");
                        return;
                    }

                    if (_p2pState.IsConnected)
                    {
                        _mediaRelayMode = false;
                        await StopRelayWorkersLockedAsync().ConfigureAwait(false);
                        workersPublished = false;
                        streamer.StopRelayMediaMode();
                        await RollbackClientGateAsync().ConfigureAwait(false);
                        return;
                    }

                    Logger.Info("[RelayMedia] Entering media relay mode (WebRTC unavailable, using WS relay)");
                    EmitWsSafeModeTelemetry("ws_safe_mode_enter");

                    // Relay fallback bypasses CompleteConnectionReadyAsync, so apply the same BGRA
                    // zero-copy/resizer setup here. Without this, a negotiated 1600x900 stream is
                    // recreated at the native 1920x1080 capture size on the first frame.
                    if (streamer.AnyTrackRequiresBgraInput() && _capture != null)
                    {
                        Logger.Info("[RelayMedia] Enabling BGRA resize pipeline for negotiated resolution");
                        _capture.UseBgraMode = true;
                    }

                    // RemotePlay renders one monitor at a time. Pause inactive tracks before capture
                    // starts instead of waiting for the normal WebRTC Phase-3 handshake (which can take
                    // 15s or never complete in relay mode), avoiding a transient 2x bitrate spike.
                    if (_capture != null && ShouldAutoPauseInactiveMonitors(_streamAllMonitors))
                    {
                        for (int i = 1; i < streamer.MonitorCount; i++)
                        {
                            streamer.PauseMonitor(i);
                            _capture.PauseMonitor(i);
                        }
                        Logger.Info("[RelayMedia] Active-monitor-only policy applied before capture start");
                    }

                    _startStreamingReceived = true; // relay path doesn't use the start_streaming handshake
                    StartCaptureThread();

                    SetPhase(ConnectionPhase.Phase3_Streaming);
                    Logger.Info("[RelayMedia] Media relay streaming active");
                }
                catch
                {
                    _mediaRelayMode = false;
                    if (workersPublished)
                    {
                        await StopRelayWorkersLockedAsync().ConfigureAwait(false);
                        workersPublished = false;
                    }
                    else
                    {
                        workers?.Dispose();
                    }
                    streamer.StopRelayMediaMode();
                    if (clientGateOpen)
                    {
                        await TrySendTextAsync("{\"type\":\"media_relay_stop\"}").ConfigureAwait(false);
                        clientGateOpen = false;
                    }
                    throw;
                }
            }
            finally
            {
                _mediaRelayLifecycle.Release();
            }
        }

        /// <summary>
        /// Producer (encoder callback threads): enqueue one frame's chunks atomically.
        /// Queue full ⇒ the relay TCP path is saturated — drop the WHOLE frame, then
        /// discard subsequent P-frames (they reference the dropped frame and would only
        /// feed the decoder garbage) until a fresh IDR makes it through.
        /// </summary>
        private void EnqueueRelayVideoFrame(int trackIdx, List<byte[]> chunks, bool isKeyframe)
        {
            var workers = Volatile.Read(ref _relayWorkers);
            if (workers == null || !_mediaRelayMode) return;

            if (workers.Recovery.IsWaitingForKeyframe(trackIdx) && !isKeyframe)
            {
                RequestRelayRecoveryKeyframe(trackIdx);
                return;
            }

            var frame = new RelayVideoFrame(
                trackIdx, chunks, isKeyframe, workers.Recovery.CurrentGeneration(trackIdx), workers.Generation);
            if (workers.VideoQueue.Writer.TryWrite(frame)) return;

            workers.Recovery.MarkTainted(trackIdx);
            RequestRelayRecoveryKeyframe(trackIdx);
        }

        private void HandleRelayVideoFrameDropped(int trackIdx)
        {
            var workers = Volatile.Read(ref _relayWorkers);
            if (workers == null || !_mediaRelayMode) return;
            workers.Recovery.MarkTainted(trackIdx);
            RequestRelayRecoveryKeyframe(trackIdx);
        }

        private void EnqueueRelayAudioChunk(byte[] pcm)
        {
            var workers = Volatile.Read(ref _relayWorkers);
            if (workers != null && _mediaRelayMode)
                workers.AudioQueue.Writer.TryWrite(pcm);
        }

        private bool IsCurrentRelaySession(RelayMediaWorkerSession workers) =>
            _mediaRelayMode && ReferenceEquals(Volatile.Read(ref _relayWorkers), workers);

        private async Task RelayAudioSendLoopAsync(
            ChannelReader<byte[]> reader,
            RelayMediaWorkerSession workers)
        {
            try
            {
                while (await reader.WaitToReadAsync(workers.Token).ConfigureAwait(false))
                {
                    // Drain queued stale packets and send only the newest available PCM chunk.
                    byte[]? latest = null;
                    while (reader.TryRead(out var pcm)) latest = pcm;
                    if (latest != null && IsCurrentRelaySession(workers))
                    {
                        await SendBytesAsync(
                            RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelAudio, latest),
                            lockTimeoutMs: 50,
                            workers.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"[RelayMedia] Audio send loop error: {ex.Message}");
            }
            finally
            {
                while (reader.TryRead(out _)) { }
            }
        }

        /// <summary>Rate-limited (1/s) IDR request so the client decoder re-anchors after drops.</summary>
        private void RequestRelayRecoveryKeyframe(int trackIdx)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastRelayKeyframeReqMs);
            if (now - last < 1000) return; // IDRs are 5-10x P-frame size — don't storm a saturated link
            if (Interlocked.CompareExchange(ref _lastRelayKeyframeReqMs, now, last) != last) return;
            Logger.Warn($"[RelayMedia] Track {trackIdx}: frame dropped under backpressure — requesting recovery IDR");
            try { _streamer?.RequestKeyframe(trackIdx, force: true); } catch { }
        }

        /// <summary>
        /// Single consumer: sends every chunk of each dequeued frame in order. Waits on
        /// the send lock indefinitely — load shedding already happened at enqueue time
        /// with frame granularity, so a mid-frame chunk must never be lost here.
        /// </summary>
        private async Task RelayVideoSendLoopAsync(
            ChannelReader<RelayVideoFrame> reader,
            RelayMediaWorkerSession workers)
        {
            try
            {
                while (await reader.WaitToReadAsync(workers.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var frame))
                    {
                        if (!IsCurrentRelaySession(workers) || frame.RelayGeneration != workers.Generation) continue;
                        if (workers.Recovery.ShouldSuppress(frame)) continue;

                        bool frameSent = true;
                        foreach (var chunk in frame.Chunks)
                        {
                            if (!IsCurrentRelaySession(workers))
                            {
                                frameSent = false;
                                break;
                            }

                            bool sent = await SendBytesAsync(
                                RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelVideo, chunk),
                                lockTimeoutMs: -1,
                                workers.Token).ConfigureAwait(false);
                            if (!sent)
                            {
                                frameSent = false;
                                if (IsCurrentRelaySession(workers))
                                {
                                    workers.Recovery.MarkTainted(frame.TrackIndex);
                                    RequestRelayRecoveryKeyframe(frame.TrackIndex);
                                }
                                break;
                            }
                        }

                        if (frameSent && frame.IsKeyframe)
                            workers.Recovery.TryCompleteKeyframe(frame);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"[RelayMedia] Video send loop error: {ex.Message}");
            }
            finally
            {
                while (reader.TryRead(out _)) { }
            }
        }

        /// <summary>
        /// Leave relay mode when a WebRTC P2P path recovers (auto-upgrade). Media goes
        /// back to DataChannels/RTP; the client is told to stop reading WS media.
        /// </summary>
        private async Task ExitMediaRelayModeAsync(bool notifyClient = true, int? expectedP2PVersion = null)
        {
            await _mediaRelayLifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (expectedP2PVersion.HasValue &&
                    !_p2pState.IsCurrent(expectedP2PVersion.Value, connected: true)) return;
                if (!_mediaRelayMode)
                {
                    await StopRelayWorkersLockedAsync().ConfigureAwait(false);
                    _streamer?.StopRelayMediaMode();
                    return;
                }

                // Reject new relay frames first, then cancel and await queued/in-flight WS sends
                // while the streamer still routes encoder output to relay. Only after the old
                // relay generation is fully quiescent may direct output be exposed.
                _mediaRelayMode = false;
                await StopRelayWorkersLockedAsync().ConfigureAwait(false);
                _streamer?.StopRelayMediaMode();
                if (notifyClient)
                    await SendTextAsync("{\"type\":\"media_relay_stop\"}").ConfigureAwait(false);
                Logger.Info("[RelayMedia] Left media relay mode — WebRTC path resumed");
                EmitWsSafeModeTelemetry("ws_safe_mode_exit");
            }
            finally
            {
                _mediaRelayLifecycle.Release();
            }
        }

        private async Task StopRelayWorkersLockedAsync()
        {
            var workers = Interlocked.Exchange(ref _relayWorkers, null);
            if (workers == null) return;

            await workers.StopAsync().ConfigureAwait(false);
            workers.Dispose();
        }

        private async Task PromoteMediaToP2PAsync()
        {
            int p2pVersion = _p2pState.MarkConnected();
            CancelRelayFallbackTimer();
            await ExitMediaRelayModeAsync(expectedP2PVersion: p2pVersion).ConfigureAwait(false);
        }

        private Task HandleP2PFailureAsync(int p2pVersion)
        {
            // Only terminal ICE/DTLS failure calls this. Transient disconnected callbacks do not,
            // so brief consent/rebinding interruptions cannot spuriously switch a live path.
            return EnterMediaRelayModeAsync(p2pVersion);
        }

        private async Task ShutdownRelayMediaAsync()
        {
            CancelRelayFallbackTimer();
            _p2pState.MarkTerminalFailure();
            await ExitMediaRelayModeAsync(notifyClient: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Explicit WS-safe-mode lifecycle telemetry (contract-v1). No selected-pair/QoE
        /// block applies to these events — only the identity block is meaningful. pc_role is
        /// reported as "main" and generation as 0: WS safe mode is a whole-connection fallback,
        /// not tied to a specific PC's ICE generation.
        /// </summary>
        private void EmitWsSafeModeTelemetry(string eventName)
        {
            try
            {
                var sessionId = _clientId.ToString();
                var snapshot = new ConnectionTelemetrySnapshot
                {
                    Event = eventName,
                    SessionId = sessionId,
                    PcRole = "main",
                    MonitorIndex = 0,
                    Generation = 0,
                    Sequence = HostTelemetryReporter.NextSequence(sessionId, "main", 0)
                };
                HostTelemetryReporter.ReportFireAndForget(snapshot);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[RelayMedia] EmitWsSafeModeTelemetry({eventName}) failed (non-fatal): {ex.Message}");
            }
        }
    }
}
