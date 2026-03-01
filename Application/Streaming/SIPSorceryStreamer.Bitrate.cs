#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    public void ProcessFpsFeedback(int monitorIndex, float effectiveFps, int droppedFrames, long clientTotalFrames)
    {
        // Get server's sent frame count for this monitor
        long serverSentFrames = 0;
        lock (_lock)
        {
            if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                serverSentFrames = Interlocked.Read(ref _tracks[monitorIndex].SentFrames);
            }
        }

        // Pipeline ratio for logging only — NOT used for bitrate decisions.
        // Server encodes at target FPS (e.g. 60fps) but client may only render ~10fps
        // due to WebRTC decoder throughput limits. This rate mismatch is normal,
        // not packet loss. Only droppedFrames indicates actual delivery problems.
        float pipelineRatio = serverSentFrames > 0
            ? (float)clientTotalFrames / serverSentFrames
            : 1f;

        Logger.Debug($"[Pipeline] Mon{monitorIndex}: Server sent {serverSentFrames}, Client received {clientTotalFrames} (ratio={pipelineRatio:F2})");
        Logger.Debug($"[SIPSorcery] FPS feedback m{monitorIndex}: {effectiveFps:F1}fps, dropped={droppedFrames}");

        // Only trigger bitrate reduction when client reports ACTUAL dropped frames.
        // Low pipeline ratio (server sends >> client renders) is normal rate mismatch,
        // not evidence of network problems. droppedFrames > 0 means the client received
        // frames but couldn't display them — a real delivery problem.
        if (droppedFrames > 0)
        {
            float fpsRatio = _fps > 0 ? effectiveFps / _fps : 1f;
            // Use drop rate as proxy for loss, not pipeline ratio
            float dropRate = (clientTotalFrames + droppedFrames) > 0
                ? (float)droppedFrames / (clientTotalFrames + droppedFrames)
                : 0f;

            Logger.Info($"[FpsBitrateAction] Triggered: fpsRatio={fpsRatio:F2}, drops={droppedFrames}, dropRate={dropRate:P1} → creating synthetic QualityFeedback");

            var syntheticFeedback = new QualityFeedbackMessage
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EffectiveFps = effectiveFps,
                TargetFps = _fps,
                PacketLossRate = dropRate,
                AvgPacketLossRate = dropRate,
                BufferStatus = fpsRatio < 0.3f ? "starving" : "lossy",
                RttMs = 0,
                JitterMs = 0,
                ConnectionHealth = fpsRatio < 0.3f ? 1 : 3,
                IsWiFi = _bitrateController.IsWiFiMode,
                Monitors = new List<MonitorFeedback>
                {
                    new MonitorFeedback
                    {
                        Index = monitorIndex,
                        DroppedFrames = droppedFrames,
                        RenderedFrames = Math.Max(0, (int)(clientTotalFrames - droppedFrames)),
                    }
                }
            };

            ProcessQualityFeedback(syntheticFeedback);

            // Force keyframe burst on severe conditions
            bool severe = fpsRatio < 0.3f || dropRate > 0.5f;
            if (severe)
            {
                Logger.Info($"[FpsBitrateAction] Severe condition → keyframe burst mon={monitorIndex}");
                RequestKeyframeBurst(monitorIndex, 3);
            }
        }
    }

    public int GetCurrentTargetFps(int monitorIndex) => _fps;

    /// <summary>
    /// Process quality feedback from client and adjust bitrate if needed.
    /// </summary>
    /// <param name="feedback">Quality feedback from client.</param>
    /// <returns>BitrateAdjustedMessage if bitrate was changed, null otherwise.</returns>
    public BitrateAdjustedMessage? ProcessQualityFeedback(QualityFeedbackMessage feedback)
    {
        // Initialize controller on first feedback if not already done
        if (_bitrateController.TargetBitrateKbps == 0)
        {
            _bitrateController.Initialize(_bitrateKbps, _bitrateKbps * 2);
        }

        // Process feedback through adaptive bitrate controller
        var decision = _bitrateController.ProcessFeedback(feedback);

        if (decision.Changed)
        {
            // Apply new bitrate to all track encoders
            lock (_lock)
            {
                int successCount = 0;
                foreach (var track in _tracks)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetBitrate(decision.NewBitrate))
                        {
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} bitrate → {decision.NewBitrate}kbps");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetBitrate failed");
                        }
                    }
                }

                if (successCount > 0)
                {
                    Logger.Info($"[SIPSorcery] Bitrate adjusted: {decision.NewBitrate}kbps ({decision.Reason})");
                }
            }

            // Return message to notify client
            return new BitrateAdjustedMessage
            {
                MonitorIndex = -1, // All monitors
                BitrateKbps = decision.NewBitrate,
                Reason = decision.Reason
            };
        }

        return null;
    }

    /// <summary>
    /// Get current adaptive bitrate statistics.
    /// </summary>
    public string GetBitrateStats() => _bitrateController.GetStats();

    /// <summary>
    /// Reset adaptive bitrate controller to initial state.
    /// </summary>
    public void ResetBitrateController() => _bitrateController.Reset();

    public void SetWiFiMode(bool isWiFi) => _bitrateController.IsWiFiMode = isWiFi;

    /// <summary>
    /// Request keyframe for a track (or all tracks if monitorIndex == -1).
    /// When force=false, rate-limited to 1 request per second per track to prevent
    /// keyframe storms that cause WiFi congestion.
    /// When requesting ALL tracks (monitorIndex == -1), keyframes are staggered:
    /// Track 0 gets immediate keyframe, Track N gets it after N*10 frames (~167ms @ 60fps).
    /// This prevents simultaneous keyframe bursts that saturate WiFi and drop Track 1's packets.
    /// Internal callers (Resume, ResumeMonitor) should use force=true.
    /// </summary>
    public void RequestKeyframe(int monitorIndex = -1, bool force = false)
    {
        const long MinIntervalMs = 1000;
        const int StaggerFramesPerTrack = 10; // ~167ms @ 60fps between track keyframes
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (monitorIndex == -1)
            {
                // Stagger: Track 0 immediate, Track 1 after 10 frames, Track 2 after 20, etc.
                for (int i = 0; i < _tracks.Count; i++)
                {
                    var t = _tracks[i];
                    if (force || now - t.LastKeyframeRequestTicks >= MinIntervalMs)
                    {
                        if (i == 0)
                        {
                            t.ForceNextKeyframe = true;
                        }
                        else
                        {
                            t.KeyframeStaggerCountdown = i * StaggerFramesPerTrack;
                        }
                        t.LastKeyframeRequestTicks = now;
                    }
                }
            }
            else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                var t = _tracks[monitorIndex];
                if (force || now - t.LastKeyframeRequestTicks >= MinIntervalMs)
                {
                    t.ForceNextKeyframe = true;
                    t.LastKeyframeRequestTicks = now;
                }
            }
        }
    }

    public void RequestKeyframeBurst(int monitorIndex = -1, int count = 3)
    {
        lock (_lock)
        {
            if (monitorIndex == -1)
            {
                foreach (var t in _tracks) t.KeyframeBurstRemaining = count;
            }
            else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                _tracks[monitorIndex].KeyframeBurstRemaining = count;
            }
        }
        Logger.Info($"[SIPSorcery] Keyframe burst: monitor={monitorIndex}, count={count}");
    }

    private List<int> GetNalTypes(byte[] au)
    {
        var types = new List<int>();
        int i = 0;
        while (i + 4 <= au.Length)
        {
            int sc = (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ? 3 :
                     (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) ? 4 : 0;
            if (sc == 0) break;
            i += sc;
            if (i >= au.Length) break;
            types.Add(au[i] & 0x1F);
            // Find next start code
            int j = i + 1;
            for (; j + 3 < au.Length; j++)
            {
                if ((au[j] == 0 && au[j + 1] == 0 && au[j + 2] == 1) ||
                    (j + 4 <= au.Length && au[j] == 0 && au[j + 1] == 0 && au[j + 2] == 0 && au[j + 3] == 1))
                    break;
            }
            i = j;
        }
        return types;
    }

    /// <summary>
    /// Dynamically update streaming configuration during Phase 3.
    /// </summary>
    /// <param name="fps">New target FPS (optional, null = no change). Note: FPS change may not take effect until reconnect.</param>
    /// <param name="totalBitrateKbps">New TOTAL bitrate in kbps for ALL monitors (optional, null = no change).</param>
    /// <returns>Tuple of (success, appliedFps, appliedTotalBitrate, message)</returns>
    public (bool Success, int Fps, int BitrateKbps, string Message) UpdateConfig(int? fps, int? totalBitrateKbps)
    {
        var messages = new List<string>();
        int appliedFps = _fps;
        int appliedBitrate = _bitrateKbps;
        bool anySuccess = false;

        // FPS change - apply to all encoders
        if (fps.HasValue && fps.Value != _fps && fps.Value > 0)
        {
            Logger.Info($"[SIPSorcery] FPS update: {_fps} → {fps.Value}");

            lock (_lock)
            {
                int successCount = 0;
                foreach (var track in _tracks)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetFps(fps.Value))
                        {
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} FPS → {fps.Value}");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetFps failed (encoder may not support runtime change)");
                        }
                    }
                }

                if (successCount > 0)
                {
                    _fps = fps.Value;
                    appliedFps = fps.Value;
                    messages.Add($"FPS: {fps.Value} ({successCount}/{_tracks.Count} encoders updated)");
                    anySuccess = true;
                }
                else if (_tracks.Count > 0)
                {
                    // Even if encoder doesn't support FPS change, update the internal state
                    // so capture rate can be adjusted
                    _fps = fps.Value;
                    appliedFps = fps.Value;
                    messages.Add($"FPS: {fps.Value} (encoder FPS change not supported, capture rate will be adjusted)");
                    anySuccess = true;
                }
            }
        }

        // Bitrate change - apply to all encoders
        if (totalBitrateKbps.HasValue && totalBitrateKbps.Value > 0)
        {
            int perMonitorBitrate = totalBitrateKbps.Value / Math.Max(1, _monitorCount);
            Logger.Info($"[SIPSorcery] Bitrate update: total={totalBitrateKbps.Value}kbps, per-monitor={perMonitorBitrate}kbps");

            lock (_lock)
            {
                int successCount = 0;
                foreach (var track in _tracks)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetBitrate(perMonitorBitrate))
                        {
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} bitrate → {perMonitorBitrate}kbps");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetBitrate failed (encoder may not support runtime change)");
                        }
                    }
                }

                if (successCount > 0)
                {
                    appliedBitrate = totalBitrateKbps.Value;
                    messages.Add($"Bitrate: {totalBitrateKbps.Value}kbps ({successCount}/{_tracks.Count} encoders updated)");
                    anySuccess = true;
                }
                else if (_tracks.Count > 0)
                {
                    messages.Add("Bitrate change not supported by current encoder(s)");
                }
            }
        }

        string message = messages.Count > 0 ? string.Join(", ", messages) : "No changes applied";
        Logger.Info($"[SIPSorcery] UpdateConfig result: {message}");

        return (anySuccess, appliedFps, appliedBitrate, message);
    }

    /// <summary>
    /// Get current streaming config.
    /// </summary>
    public (int Fps, int TotalBitrateKbps, int MonitorCount) GetCurrentConfig() =>
        (_fps, _bitrateKbps, _monitorCount);
}
