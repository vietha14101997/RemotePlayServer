#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    // DERP-style media relay fallback. When WebRTC ICE fails after the restart
    // budget is spent (both peers behind symmetric CGNAT), media flows
    // host -> relay(WebSocket) -> client instead of over WebRTC. Only activates on
    // an already-dead WebRTC path, so it can never regress a working P2P session.
    public partial class PhaseProtocolHandler
    {
        private volatile bool _mediaRelayMode;
        private volatile bool _p2pConnected; // WebRTC DTLS ever completed this session
        private int _mediaRelayWired; // 0/1 — subscribe streamer relay events only once
        private int _dtlsFailCount;   // host-side DTLS/ICE failures observed this session
        private CancellationTokenSource? _relayFallbackCts;

        // Bounded frame-atomic video send queue. A single consumer preserves chunk
        // order; when the TCP relay can't drain fast enough, WHOLE frames are dropped
        // at enqueue time (never individual chunks — a missing chunk corrupts the NAL
        // and poisons every following P-frame) and a recovery IDR is requested.
        private Channel<List<byte[]>>? _relayVideoQueue;
        private bool[] _relayWaitKeyframe = Array.Empty<bool>(); // per-track: drop P-frames until an IDR passes
        private long _lastRelayKeyframeReqMs;
        private const int RELAY_QUEUE_FRAMES = 4;

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
                if (_p2pConnected || _mediaRelayMode) return;
                try
                {
                    Logger.Info($"[RelayMedia] No P2P after {RELAY_FALLBACK_DELAY_MS}ms — starting media relay (P2P keeps trying in background)");
                    EnterMediaRelayMode();
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
        private void EnterMediaRelayMode()
        {
            if (_mediaRelayMode) return;
            var streamer = _streamer;
            if (streamer == null)
            {
                Logger.Warn("[RelayMedia] Cannot enter relay mode — no streamer");
                return;
            }

            CancelRelayFallbackTimer();
            _mediaRelayMode = true;
            Logger.Info("[RelayMedia] Entering media relay mode (WebRTC unavailable, using WS relay)");

            // Wire encoder → relay exactly once. Video goes through a bounded ordered
            // queue with a single-writer send loop; audio PCM stays fire-and-forget
            // (each packet is independent — losing one is a 10ms glitch, not corruption).
            if (Interlocked.Exchange(ref _mediaRelayWired, 1) == 0)
            {
                _relayWaitKeyframe = new bool[Math.Max(1, _monitors.Count)];
                var queue = Channel.CreateBounded<List<byte[]>>(new BoundedChannelOptions(RELAY_QUEUE_FRAMES)
                {
                    SingleReader = true,
                    SingleWriter = false // two encoder callback threads (one per track)
                });
                _relayVideoQueue = queue;

                streamer.OnRelayVideoFrame += EnqueueRelayVideoFrame;
                streamer.OnRelayAudioChunk += pcm =>
                    _ = SendBytesAsync(RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelAudio, pcm));

                _ = Task.Run(() => RelayVideoSendLoopAsync(queue.Reader));
            }

            // Tell the client to expect media over the WS binary channel.
            int monitors = _monitors.Count;
            _ = SendTextAsync(
                $"{{\"type\":\"media_relay_start\",\"monitors\":{monitors},\"codec\":\"{_selectedCodec}\"}}");

            // Bring up encode/audio without a PeerConnection, then start capture.
            streamer.StartRelayMediaMode();
            _startStreamingReceived = true; // relay path doesn't use the start_streaming handshake
            StartCaptureThread();

            SetPhase(ConnectionPhase.Phase3_Streaming);
            Logger.Info("[RelayMedia] Media relay streaming active");
        }

        /// <summary>
        /// Producer (encoder callback threads): enqueue one frame's chunks atomically.
        /// Queue full ⇒ the relay TCP path is saturated — drop the WHOLE frame, then
        /// discard subsequent P-frames (they reference the dropped frame and would only
        /// feed the decoder garbage) until a fresh IDR makes it through.
        /// </summary>
        private void EnqueueRelayVideoFrame(int trackIdx, List<byte[]> chunks, bool isKeyframe)
        {
            var queue = _relayVideoQueue;
            if (queue == null || !_mediaRelayMode) return;

            bool waiting = trackIdx < _relayWaitKeyframe.Length && _relayWaitKeyframe[trackIdx];
            if (waiting && !isKeyframe)
            {
                RequestRelayRecoveryKeyframe(trackIdx);
                return;
            }

            if (queue.Writer.TryWrite(chunks))
            {
                if (isKeyframe && trackIdx < _relayWaitKeyframe.Length)
                    _relayWaitKeyframe[trackIdx] = false;
                return;
            }

            if (trackIdx < _relayWaitKeyframe.Length)
                _relayWaitKeyframe[trackIdx] = true;
            RequestRelayRecoveryKeyframe(trackIdx);
        }

        /// <summary>Rate-limited (1/s) IDR request so the client decoder re-anchors after drops.</summary>
        private void RequestRelayRecoveryKeyframe(int trackIdx)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastRelayKeyframeReqMs);
            if (now - last < 1000) return; // IDRs are 5-10x P-frame size — don't storm a saturated link
            if (Interlocked.CompareExchange(ref _lastRelayKeyframeReqMs, now, last) != last) return;
            Logger.Debug($"[RelayMedia] Track {trackIdx}: frame dropped under backpressure — requesting recovery IDR");
            try { _streamer?.RequestKeyframe(trackIdx, force: true); } catch { }
        }

        /// <summary>
        /// Single consumer: sends every chunk of each dequeued frame in order. Waits on
        /// the send lock indefinitely — load shedding already happened at enqueue time
        /// with frame granularity, so a mid-frame chunk must never be lost here.
        /// </summary>
        private async Task RelayVideoSendLoopAsync(ChannelReader<List<byte[]>> reader)
        {
            try
            {
                while (await reader.WaitToReadAsync(_ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var chunks))
                    {
                        foreach (var chunk in chunks)
                        {
                            await SendBytesAsync(
                                RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelVideo, chunk),
                                lockTimeoutMs: -1).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"[RelayMedia] Video send loop error: {ex.Message}");
            }
        }

        /// <summary>
        /// Leave relay mode when a WebRTC P2P path recovers (auto-upgrade). Media goes
        /// back to DataChannels/RTP; the client is told to stop reading WS media.
        /// </summary>
        private void ExitMediaRelayMode()
        {
            if (!_mediaRelayMode) return;
            _mediaRelayMode = false;
            _streamer?.StopRelayMediaMode();
            _ = SendTextAsync("{\"type\":\"media_relay_stop\"}");
            Logger.Info("[RelayMedia] Left media relay mode — WebRTC path resumed");
        }
    }
}
