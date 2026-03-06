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
        if (_pc == null || !_hasAudioTrack)
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
                if (!_connected || !_running || _pc == null) return;
                // Don't send audio during early capture — contributes to WiFi congestion
                if (!_phase3Active) return;
                try
                {
                    // DataChannel path: send Opus frame as binary message
                    // Format: [type(1)][timestamp(8)][opus_data]
                    // Client decodes with Concentus + OnAudioFilterRead (~20ms latency)
                    if (_audioDc?.readyState == SIPSorcery.Net.RTCDataChannelState.open)
                    {
                        var msg = new byte[1 + 8 + opusLength];
                        msg[0] = 0x01; // Audio frame type
                        BitConverter.TryWriteBytes(msg.AsSpan(1, 8), timestampMs);
                        Buffer.BlockCopy(opusData, 0, msg, 9, opusLength);
                        _audioDc.send(msg);
                        Interlocked.Increment(ref _audioPacketsSent);
                    }
                    else if (_pc.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
                    {
                        // RTP fallback: used when DataChannel not yet open
                        var packet = new byte[opusLength];
                        Buffer.BlockCopy(opusData, 0, packet, 0, opusLength);

                        uint audioStep;
                        lock (_audioSyncLock)
                        {
                            if (!_audioClockInitialized && timestampMs > 0)
                                audioStep = CalculateAudioRtpStep(timestampMs);
                            else
                                audioStep = rtpDuration;
                        }

                        _pc.SendAudio(audioStep, packet);
                        Interlocked.Increment(ref _audioPacketsSent);
                    }
                }
                catch (Exception ex)
                {
                    if (Environment.TickCount64 % 5000 < 20)
                        Logger.Error($"[SIPSorcery] Audio send error: {ex.Message}");
                }
            };

            _audioCapture.Start();

            Logger.Info("[SIPSorcery] Audio pipeline started (WASAPI loopback -> Opus -> DataChannel)");
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
