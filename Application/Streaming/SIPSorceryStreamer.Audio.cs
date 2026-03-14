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

            // Wire: capture -> encoder -> RTP send
            _audioCapture.OnAudioData += (pcm, length, sampleRate, channels, timestampMs) =>
            {
                if (_isPaused || !_connected || !_running) return;
                _opusEncoder.EncodePcm(pcm, length, sampleRate, channels, timestampMs);
            };

            _opusEncoder.OnEncodedAudio += (opusData, opusLength, rtpDuration, timestampMs) =>
            {
                if (!_connected || !_running || _mainPc == null) return;
                // Stop sending audio when paused — Opus encoder may still have buffered
                // data from EncodePcm calls that arrived before _isPaused was set.
                if (_isPaused) return;
                // Don't send audio during early capture — contributes to WiFi congestion
                if (!_phase3Active) return;
                try
                {
                    // Primary: send Opus via RTP (independent UDP transport).
                    // With H.265 video on DataChannel, SCTP congestion starves audio DC
                    // causing 300-500ms delay + distortion. RTP travels separate UDP path.
                    var pc = _mainPc;
                    if (pc?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
                    {
                        var packet = new byte[opusLength];
                        Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);
                        pc.SendAudio(rtpDuration, packet);
                        Interlocked.Increment(ref _audioPacketsSent);
                    }
                    else
                    {
                        // Fallback: send Opus via DataChannel if RTP not available
                        var dc = _audioDc;
                        if (dc != null && dc.readyState == SIPSorcery.Net.RTCDataChannelState.open)
                        {
                            var packet = new byte[opusLength];
                            Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);
                            dc.send(packet);
                            Interlocked.Increment(ref _audioPacketsSent);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Environment.TickCount64 % 5000 < 20)
                        Logger.Error($"[SIPSorcery] Audio send error: {ex.Message}");
                }
            };

            // Log which audio path is active (one-time, after first successful send)
            bool audioPathLogged = false;

            _opusEncoder.OnEncodedAudio += (_, _, _, _) =>
            {
                if (audioPathLogged) return;
                var dcCheck = _audioDc;
                if (_mainPc?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
                {
                    Logger.Info("[SIPSorcery] Audio path: RTP primary (independent UDP, avoids SCTP congestion from H.265 video DC)");
                    audioPathLogged = true;
                }
                else if (dcCheck != null && dcCheck.readyState == SIPSorcery.Net.RTCDataChannelState.open)
                {
                    Logger.Info("[SIPSorcery] Audio path: DataChannel fallback (RTP not connected)");
                    audioPathLogged = true;
                }
            };

            _audioCapture.Start();

            Logger.Info("[SIPSorcery] Audio pipeline started (WASAPI loopback -> Opus -> RTP primary)");
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
