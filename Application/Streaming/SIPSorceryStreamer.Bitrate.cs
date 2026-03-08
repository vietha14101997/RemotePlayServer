#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RemotePlayServer.Infrastructure.Encoding;
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

            // Force keyframe on severe conditions — but NOT for H265/DataChannel.
            // H265 IDR frames are 200-400KB; bursting them into SCTP causes congestion death spiral.
            // Intra refresh handles H265 recovery gradually without bandwidth spikes.
            bool severe = fpsRatio < 0.3f || dropRate > 0.5f;
            if (severe && _negotiatedCodec != VideoCodec.H265)
            {
                Logger.Info($"[FpsBitrateAction] Severe condition → keyframe burst mon={monitorIndex}");
                RequestKeyframeBurst(monitorIndex, 1);
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
        // Initialization is now done in constructur / config changes

        // Block global bitrate increases while ANY DC buffer is congested.
        // Per-track congestion is handled independently, but global ABC should still
        // be conservative when any track is struggling.
        bool anyTrackCongested = _dcWasAboveHigh; // legacy single-DC
        if (!anyTrackCongested)
        {
            lock (_lock) { anyTrackCongested = _tracks.Any(t => t.PFramesDroppedDuringCongestion || t.CongestionBitrateReduced); }
        }
        if (anyTrackCongested)
        {
            return null;
        }

        // Process feedback through adaptive bitrate controller
        var decision = _bitrateController.ProcessFeedback(feedback);

        if (decision.Changed)
        {
            // Apply new bitrate to all track encoders, respecting per-track congestion.
            // Tracks in per-track congestion keep their own reduced bitrate.
            TrackInfo[] snapshot;
            lock (_lock) { snapshot = _tracks.ToArray(); }

            int successCount = 0;
            foreach (var track in snapshot)
            {
                // Skip tracks that are in per-track congestion — they manage their own bitrate
                if (track.CongestionBitrateReduced)
                {
                    Logger.Debug($"[SIPSorcery] Track {track.Index} skipped ABC bitrate change → track has own congestion bitrate {track.TrackBitrateKbps}kbps");
                    continue;
                }

                lock (track.EncodeLock)
                {
                    if (track.Encoder != null)
                    {
                        if (track.Encoder.SetBitrate(decision.NewBitrate))
                        {
                            track.TrackBitrateKbps = decision.NewBitrate;
                            successCount++;
                            Logger.Info($"[SIPSorcery] Track {track.Index} bitrate → {decision.NewBitrate}kbps");
                        }
                        else
                        {
                            Logger.Error($"[SIPSorcery] Track {track.Index} SetBitrate failed");
                        }
                    }
                }
            }

            if (successCount > 0)
            {
                Logger.Info($"[SIPSorcery] Bitrate adjusted: {decision.NewBitrate}kbps ({decision.Reason})");
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
        // H265 via DataChannel: IDR frames are huge (~200KB each).
        // Burst of 5 IDR × 2 tracks = ~2MB flooding SCTP → instant death spiral.
        // Cap to 1 IDR per burst in H265/DC mode, and skip if DC is already congested.
        if (_negotiatedCodec == VideoCodec.H265)
        {
            // Check per-track congestion: skip burst if the target track is congested
            bool anyCongested = false;
            lock (_lock)
            {
                if (monitorIndex == -1)
                    anyCongested = _tracks.Any(t => t.DcSoftCongestion);
                else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
                    anyCongested = _tracks[monitorIndex].DcSoftCongestion;
            }
            if (anyCongested)
            {
                Logger.Warn($"[SIPSorcery] Keyframe burst SKIPPED (DC congested, monitor={monitorIndex}, requested={count})");
                return;
            }
            count = 1; // Single IDR is sufficient for H265 resync
        }

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
        int len = au.Length;
        while (i + 3 < len)
        {
            // Scan for 00 00 01 or 00 00 00 01
            int sc = 0;
            if (au[i] == 0 && au[i + 1] == 0)
            {
                if (au[i + 2] == 1) sc = 3;
                else if (i + 3 < len && au[i + 2] == 0 && au[i + 3] == 1) sc = 4;
            }

            if (sc > 0)
            {
                i += sc;
                if (i < len)
                {
                    int type;
                    if (_negotiatedCodec == VideoCodec.H265)
                        type = (au[i] >> 1) & 0x3F;
                    else
                        type = au[i] & 0x1F;
                    
                    types.Add(type);
                }
            }
            else
            {
                i++;
            }
        }
        var uniqueTypes = new List<int>();
        foreach (var t in types) if (!uniqueTypes.Contains(t)) uniqueTypes.Add(t);
        return uniqueTypes;
    }

    public (int MinBitrate, int MaxBitrate) GetBitrateRange(int resolutionHeight, int fps)
    {
        if (resolutionHeight <= 720)
        {
            if (fps <= 30) return (2000, 5000);
            if (fps <= 60) return (3000, 7000);
            return (5000, 10000); // 120 fps
        }
        else if (resolutionHeight <= 1080)
        {
            if (fps <= 30) return (4000, 8000);
            if (fps <= 60) return (6000, 15000);
            return (12000, 25000); // 120 fps
        }
        else // 1440p+
        {
            if (fps <= 30) return (6000, 15000);
            if (fps <= 60) return (10000, 25000);
            return (20000, 40000); // 120 fps
        }
    }

    /// <summary>
    /// Dynamically update streaming configuration during Phase 3.
    /// </summary>
    /// <param name="fps">New target FPS (optional, null = no change). Note: FPS change may not take effect until reconnect.</param>
    /// <param name="resolutionHeight">New resolution height (optional, null = no change).</param>
    /// <returns>Tuple of (success, appliedFps, appliedResolutionHeight, message)</returns>
    public (bool Success, int Fps, int ResolutionHeight, string Message) UpdateConfig(int? fps, int? resolutionHeight)
    {
        var messages = new List<string>();
        int appliedFps = _fps;
        int appliedResolutionHeight = _resolutionHeight;
        bool anySuccess = false;
        bool configChanged = false;

        // FPS change - apply to all encoders
        // Snapshot tracks under _lock, then SetFps under track.EncodeLock to serialize with encode path.
        if (fps.HasValue && fps.Value != _fps && fps.Value > 0)
        {
            Logger.Info($"[SIPSorcery] FPS update: {_fps} → {fps.Value}");

            TrackInfo[] fpsSnapshot;
            int trackCount;
            lock (_lock) { fpsSnapshot = _tracks.ToArray(); trackCount = _tracks.Count; }

            int successCount = 0;
            foreach (var track in fpsSnapshot)
            {
                lock (track.EncodeLock)
                {
                    if (track.Encoder != null && track.Encoder.SetFps(fps.Value))
                    {
                        successCount++;
                        Logger.Info($"[SIPSorcery] Track {track.Index} FPS → {fps.Value}");
                    }
                }
            }

            if (successCount > 0)
            {
                _fps = fps.Value;
                appliedFps = fps.Value;
                messages.Add($"FPS: {fps.Value} ({successCount}/{trackCount} encoders updated)");
                anySuccess = true;
                configChanged = true;
            }
            else if (trackCount > 0)
            {
                // Even if encoder doesn't support FPS change, update the internal state
                // so capture rate can be adjusted
                _fps = fps.Value;
                appliedFps = fps.Value;
                messages.Add($"FPS: {fps.Value} (encoder FPS change not supported, capture rate will be adjusted)");
                anySuccess = true;
                configChanged = true;
            }
        }

        // Resolution Height change
        if (resolutionHeight.HasValue && resolutionHeight.Value > 0 && resolutionHeight.Value != _resolutionHeight)
        {
            Logger.Info($"[SIPSorcery] Resolution Height update: {_resolutionHeight} → {resolutionHeight.Value}");
            _resolutionHeight = resolutionHeight.Value;
            appliedResolutionHeight = resolutionHeight.Value;
            messages.Add($"Resolution: {resolutionHeight.Value}p");
            anySuccess = true;
            configChanged = true;

            // CRITICAL: When resolution changes, do NOT update FPS/Bitrate synchronously here.
            // This prevents deadlocks between the message loop (this thread) and the capture thread.
            // The capture thread will detect the resolution change in the next PushBgraTexture call
            // and recreate the encoder safely via EnsureEncoderMatchesResolution.
            Logger.Info("[SIPSorcery] Resolution changed, deferred encoder update to capture thread");
            
            // Still update the bitrate controller so the next encoder gets the right initial values
            var range = GetBitrateRange(_resolutionHeight, _fps);
            _bitrateController.Initialize(range.MinBitrate, range.MaxBitrate);
            messages.Add($"Bitrate controller re-initialized for {appliedResolutionHeight}p");
            
            return (true, appliedFps, appliedResolutionHeight, string.Join(", ", messages));
        }

        if (configChanged)
        {
            var range = GetBitrateRange(_resolutionHeight, _fps);
            _bitrateController.Initialize(range.MinBitrate, range.MaxBitrate);
            
            int newTargetBitrate = _bitrateController.TargetBitrateKbps;
            
            TrackInfo[] brSnapshot;
            lock (_lock) { brSnapshot = _tracks.ToArray(); }

            foreach (var track in brSnapshot)
            {
                lock (track.EncodeLock)
                {
                    if (track.Encoder != null)
                    {
                        track.Encoder.SetBitrate(newTargetBitrate);
                    }
                }
            }
            
            messages.Add($"Target Bitrate reset to {newTargetBitrate}kbps based on {appliedResolutionHeight}p @ {appliedFps}fps");
        }

        string message = messages.Count > 0 ? string.Join(", ", messages) : "No changes applied";
        Logger.Info($"[SIPSorcery] UpdateConfig result: {message}");

        return (anySuccess, appliedFps, appliedResolutionHeight, message);
    }

    /// <summary>
    /// Get current streaming config.
    /// Returns actual current bitrate from adaptive controller (not initial config value).
    /// </summary>
    public (int Fps, int TotalBitrateKbps, int MonitorCount) GetCurrentConfig()
    {
        // Use adaptive controller's current target
        int currentBitrate = _bitrateController.TargetBitrateKbps > 0
            ? _bitrateController.TargetBitrateKbps
            : 0;
        return (_fps, currentBitrate * Math.Max(1, _monitorCount), _monitorCount);
    }

    /// <summary>
    /// Force set bitrate on all track encoders, bypassing adaptive controller cooldowns.
    /// Used by escalating stall detection when immediate bitrate reduction is needed.
    /// Also syncs the adaptive controller's target to prevent it from overriding.
    /// Uses track.EncodeLock per-track to serialize with the encode path (PushBgraTexture),
    /// preventing concurrent native P/Invoke calls (SetBitrate + Encode) on the same handle.
    /// </summary>
    public void ForceSetBitrate(int kbps)
    {
        int clampedKbps = Math.Max(_bitrateController.MinBitrateKbps, kbps);

        // Sync adaptive controller so it doesn't immediately override
        if (_bitrateController.TargetBitrateKbps > 0)
        {
            _bitrateController.ForceTarget(clampedKbps);
        }

        // Snapshot tracks under _lock, then SetBitrate under each track's EncodeLock.
        // Lock order: _lock → track.EncodeLock (same as capture path, no deadlock).
        TrackInfo[] snapshot;
        lock (_lock) { snapshot = _tracks.ToArray(); }

        foreach (var track in snapshot)
        {
            lock (track.EncodeLock)
            {
                track.Encoder?.SetBitrate(clampedKbps);
            }
            track.TrackBitrateKbps = clampedKbps;
            track.CongestionBitrateReduced = false; // Stall escalation overrides per-track congestion
        }
        Logger.Info($"[SIPSorcery] Forced bitrate → {clampedKbps}kbps (stall escalation)");
    }
}
