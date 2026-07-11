#nullable enable
using System;
using System.Threading;
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
        private int _mediaRelayWired; // 0/1 — subscribe streamer relay events only once

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
