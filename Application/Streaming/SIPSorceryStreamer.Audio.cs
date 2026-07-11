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

        try
        {
            _audioCapture = new DesktopAudioCapture();
            _opusEncoder = new OpusAudioEncoder();

            // Wire: capture -> DC (raw PCM) + encoder -> RTP (Opus, fallback)
            _audioCapture.OnAudioData += (pcm, length, sampleRate, channels, timestampMs) =>
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

                // Primary: send raw PCM16 via DataChannel for lowest latency.
                // Bypasses Opus encode+decode + libwebrtc jitter buffer entirely.
                // 48kHz stereo PCM16 = 192KB/s (~1.5Mbps), acceptable for USB/LAN.
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

                // Fallback: Opus via RTP when DC not available
                _opusEncoder.EncodePcm(pcm, length, sampleRate, channels, timestampMs);
            };

            _opusEncoder.OnEncodedAudio += (opusData, opusLength, rtpDuration, timestampMs) =>
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
            _audioCapture.OnAudioData += (_, _, _, _, _) =>
            {
                if (audioPathLogged) return;
                var dcCheck = _audioDc;
                if (dcCheck != null && dcCheck.readyState == SIPSorcery.Net.RTCDataChannelState.open)
                {
                    Logger.Info("[SIPSorcery] Audio path: DataChannel PCM (zero encode/decode latency)");
                    audioPathLogged = true;
                }
            };

            _audioCapture.Start();

            Logger.Info("[SIPSorcery] Audio pipeline started (WASAPI -> PCM DC primary, Opus RTP fallback)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Audio init failed (non-fatal, video continues): {ex.Message}");

            try { _opusEncoder?.Dispose(); } catch { }
            try { _audioCapture?.Dispose(); } catch { }
            _opusEncoder = null;
            _audioCapture = null;
        }
    }
}
