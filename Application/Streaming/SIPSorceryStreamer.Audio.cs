#nullable enable
using System;
using System.Threading;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    /// <summary>
    /// Initialize audio capture and encoding pipeline.
    /// Non-fatal: if audio init fails, video streaming continues.
    /// </summary>
    private void InitializeAudio()
    {
        // Only init if we added an audio track during SDP negotiation
        if (_mainPc == null || !_hasAudioTrack)
        {
            if (!_hasAudioTrack)
                Logger.Info("[SIPSorcery] Audio pipeline skipped (no audio track negotiated)");
            return;
        }

        lock (_audioLifecycleLock)
        {
            if (_audioCapture != null && _opusEncoder != null) return;

            DesktopAudioCapture? audioCapture = null;
            OpusAudioEncoder? opusEncoder = null;
            try
            {
                audioCapture = new DesktopAudioCapture();
                opusEncoder = new OpusAudioEncoder();
                _audioCapture = audioCapture;
                _opusEncoder = opusEncoder;

                // Wire: capture -> DC (raw PCM) + encoder -> RTP (Opus, fallback)
                audioCapture.OnAudioData += (pcm, length, sampleRate, channels, timestampMs) =>
                {
                    if (_isPaused || !_running || !_phase3Active) return;
                    // Relay-media mode streams without DTLS — don't gate on WebRTC connect.
                    if (!_connected && !RelayMediaMode) return;

                    // Relay-media fallback: raw PCM16 over the WebSocket relay instead of DC.
                    if (RelayMediaMode)
                    {
                        try
                        {
                            var packet = new byte[length];
                            Buffer.BlockCopy(pcm, 0, packet, 0, length);
                            OnRelayAudioChunk?.Invoke(packet);
                            Interlocked.Increment(ref _audioPacketsSent); // count relay audio in stats too
                        }
                        catch { }
                        return;
                    }

                    // Adaptive Audio routing:
                    // When UsePcmDataChannelAudio is true: send raw PCM16 via DataChannel for lowest latency (LAN/USB).
                    // When UsePcmDataChannelAudio is false: send Opus RTP (TURN Relay / 4G / WAN) to save 95% bandwidth.
                    if (UsePcmDataChannelAudio)
                    {
                        var dc = _audioDc;
                        if (dc != null && dc.readyState == SIPSorcery.Net.RTCDataChannelState.open)
                        {
                            try
                            {
                                var packet = new byte[length];
                                Buffer.BlockCopy(pcm, 0, packet, 0, length);
                                dc.send(packet);
                                Interlocked.Increment(ref _audioPacketsSent);
                            }
                            catch { }
                            return; // Don't also send via RTP
                        }
                    }

                    // Opus via RTP (used for TURN Relay, 4G/WAN, or when DataChannel is not open)
                    opusEncoder.EncodePcm(pcm, length, sampleRate, channels, timestampMs);
                };

                opusEncoder.OnEncodedAudio += (opusData, opusLength, rtpDuration, timestampMs) =>
                {
                    if (!_connected || !_running || _mainPc == null || _isPaused || !_phase3Active) return;
                    try
                    {
                        var pc = _mainPc;
                        if (pc?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
                        {
                            var packet = new byte[opusLength];
                            Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);
                            pc.SendAudio(rtpDuration, packet);
                            Interlocked.Increment(ref _audioPacketsSent);
                        }
                    }
                    catch { }
                };

                bool audioPathLogged = false;
                audioCapture.OnAudioData += (_, _, _, _, _) =>
                {
                    if (audioPathLogged) return;
                    if (UsePcmDataChannelAudio && _audioDc != null && _audioDc.readyState == SIPSorcery.Net.RTCDataChannelState.open)
                    {
                        Logger.Info("[SIPSorcery] Audio active path: DataChannel PCM (zero encode/decode latency)");
                        audioPathLogged = true;
                    }
                    else if (!UsePcmDataChannelAudio && _connected)
                    {
                        Logger.Info("[SIPSorcery] Audio active path: Opus RTP (compressed ~96kbps, FEC enabled)");
                        audioPathLogged = true;
                    }
                };

                audioCapture.Start();

                Logger.Info("[SIPSorcery] Audio pipeline started (Adaptive: PCM DC for LAN/USB, Opus RTP for Relay/WAN)");
            }
            catch (Exception ex)
            {
                Logger.Error($"[SIPSorcery] Audio init failed (non-fatal, video continues): {ex.Message}");

                try { opusEncoder?.Dispose(); } catch { }
                try { audioCapture?.Dispose(); } catch { }
                if (ReferenceEquals(_opusEncoder, opusEncoder)) _opusEncoder = null;
                if (ReferenceEquals(_audioCapture, audioCapture)) _audioCapture = null;
            }
        }
    }
}
