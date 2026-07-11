#nullable enable
using System;
using System.Threading;
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

            // Wire encoder → relay exactly once. Each chunk gets a 1-byte channel tag.
            if (Interlocked.Exchange(ref _mediaRelayWired, 1) == 0)
            {
                streamer.OnRelayVideoChunk += (idx, chunk) =>
                    _ = SendBytesAsync(RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelVideo, chunk));
                streamer.OnRelayAudioChunk += pcm =>
                    _ = SendBytesAsync(RelayMediaProtocol.Wrap(RelayMediaProtocol.ChannelAudio, pcm));
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
