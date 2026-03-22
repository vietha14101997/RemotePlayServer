#nullable enable
using System;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming
{
    /// <summary>
    /// Result of bitrate adjustment decision.
    /// </summary>
    public class BitrateDecision
    {
        public int NewBitrate { get; set; }
        public bool Changed { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Adaptive bitrate controller using EWMA-based algorithm.
    /// Adjusts encoder bitrate based on client feedback including:
    /// - Packet loss rate
    /// - Buffer status
    /// - Effective bandwidth estimation
    /// - Jitter and RTT
    /// </summary>
    public class AdaptiveBitrateController
    {
        // Configuration
        public int MinBitrateKbps { get; set; } = 2000;    // 2 Mbps floor
        public int MaxBitrateKbps { get; set; } = 50000;   // 50 Mbps ceiling
        public int TargetBitrateKbps { get; private set; }
        public int InitialBitrateKbps { get; private set; }

        // EWMA state for smoothing
        private double _ewmaBandwidth;
        private double _ewmaPacketLoss;
        private double _ewmaRtt;
        private const double EWMA_ALPHA = 0.3; // Smoothing factor (0.0-1.0, higher = more responsive)

        // WiFi mode - aggressive recovery (YouTube-like: recover in seconds, not minutes)
        public bool IsWiFiMode { get; set; }
        private const int WIFI_ADJUSTMENT_COOLDOWN_MS = 1500;  // Faster response on WiFi
        private const int WIFI_RECOVERY_DELAY_MS = 3000;       // 3s stable → start recovery (was 5s)
        private const int WIFI_RECOVERY_COOLDOWN_MS = 1500;    // 1.5s between recovery steps (was 3s)

        // Rate limiting for adjustments
        private DateTime _lastAdjustmentTime = DateTime.MinValue;
        private const int ADJUSTMENT_COOLDOWN_MS = 2000; // 2 second cooldown between adjustments

        // Warmup period - don't reduce bitrate due to FPS/buffer issues during startup
        private DateTime _streamStartTime = DateTime.MinValue;
        private const int WARMUP_PERIOD_MS = 15000; // 15 second warmup period

        // Auto-recovery: Track stable periods to recover bitrate
        private DateTime _lastNetworkIssueTime = DateTime.MinValue;
        private const int RECOVERY_DELAY_MS = 10000; // 10 seconds without issues before recovery
        private const int RECOVERY_COOLDOWN_MS = 5000; // 5 seconds between recovery steps

        // DC congestion ceiling: prevents sawtooth oscillation (8000→6400→8000→6400...)
        // When DC soft congestion fires at bitrate X, we cap recovery at X * 85%.
        // The ceiling slowly relaxes (+5% every 60s) to probe for more bandwidth.
        // Faster relaxation (was 120s/3%) because escalating congestion reduction now
        // provides stronger protection against over-recovery.
        private int _dcCongestionCeilingKbps;          // 0 = no ceiling (unlimited)
        private DateTime _lastDcCongestionTime = DateTime.MinValue;
        private const int DC_CEILING_RELAX_INTERVAL_MS = 15000;  // Relax ceiling every 15s (was 60s — YouTube-like fast recovery)
        private const float DC_CEILING_SAFETY_FACTOR = 0.85f;    // Cap at 85% of congestion trigger point
        private const float DC_CEILING_RELAX_STEP = 0.10f;       // +10% per relaxation (was 5% — faster probing)

        // Thresholds for bitrate decisions
        private const float PACKET_LOSS_INCREASE_THRESHOLD = 0.02f;  // >2% loss triggers decrease
        private const float PACKET_LOSS_DECREASE_THRESHOLD = 0.005f; // <0.5% loss allows increase
        private const float BUFFER_HEALTH_LOW = 0.3f;                // Buffer < 30% = starving
        private const float BUFFER_HEALTH_HIGH = 0.7f;               // Buffer > 70% = healthy
        private const float BANDWIDTH_SAFETY_MARGIN = 0.8f;          // Use 80% of available bandwidth
        private const float FPS_DROP_THRESHOLD = 0.7f;               // FPS < 70% of target = reduce bitrate
        private const float FPS_CRITICAL_THRESHOLD = 0.3f;           // FPS < 30% = critical, bypass warmup

        // Statistics
        public int AdjustmentCount { get; private set; }
        public DateTime LastAdjustmentTime => _lastAdjustmentTime;

        /// <summary>
        /// Initialize controller with separate min, initial, and max bitrates.
        /// Strategy: "Start High, Adjust Down" - initial is set to 80% of max by default.
        /// </summary>
        /// <param name="minBitrateKbps">Minimum allowed bitrate (floor).</param>
        /// <param name="maxBitrateKbps">Maximum allowed bitrate (ceiling).</param>
        /// <param name="initialBitrateKbps">Starting bitrate (optional, defaults to 80% of max).</param>
        public void Initialize(int minBitrateKbps, int maxBitrateKbps, int? initialBitrateKbps = null)
        {
            MinBitrateKbps = minBitrateKbps;
            MaxBitrateKbps = maxBitrateKbps;

            // "Start High, Adjust Down": default initial = 80% of max
            int initial = initialBitrateKbps ?? (int)(maxBitrateKbps * 0.8);
            initial = Math.Clamp(initial, minBitrateKbps, maxBitrateKbps);

            InitialBitrateKbps = initial;
            TargetBitrateKbps = initial;

            // Initialize EWMA with current values
            _ewmaBandwidth = initial;
            _ewmaPacketLoss = 0;
            _ewmaRtt = 30;

            // Start warmup period
            _streamStartTime = DateTime.UtcNow;
            _lastNetworkIssueTime = DateTime.UtcNow; // Avoid "stable for 2025 years" when no issue has occurred yet

            Logger.Info($"[AdaptiveBitrate] Initialized: target={initial}kbps, range=[{MinBitrateKbps}-{MaxBitrateKbps}]kbps, warmup={WARMUP_PERIOD_MS}ms");
        }

        /// <summary>
        /// Process client quality feedback and decide whether to adjust bitrate.
        /// </summary>
        /// <param name="feedback">Quality feedback from client.</param>
        /// <returns>Decision with new bitrate and whether it changed.</returns>
        public BitrateDecision ProcessFeedback(QualityFeedbackMessage feedback)
        {
            // Update EWMA values
            UpdateEwma(feedback);

            // Check cooldown (faster on WiFi for quicker response)
            int cooldownMs = IsWiFiMode ? WIFI_ADJUSTMENT_COOLDOWN_MS : ADJUSTMENT_COOLDOWN_MS;
            var timeSinceLastAdjustment = (DateTime.UtcNow - _lastAdjustmentTime).TotalMilliseconds;
            if (timeSinceLastAdjustment < cooldownMs)
            {
                return new BitrateDecision
                {
                    NewBitrate = TargetBitrateKbps,
                    Changed = false,
                    Reason = "cooldown"
                };
            }

            // Calculate new bitrate
            int newBitrate = CalculateNewBitrate(feedback);

            if (newBitrate != TargetBitrateKbps)
            {
                _lastAdjustmentTime = DateTime.UtcNow;
                AdjustmentCount++;

                string reason = BuildReason(feedback, newBitrate > TargetBitrateKbps);
                Logger.Info($"[AdaptiveBitrate] Adjusting: {TargetBitrateKbps} → {newBitrate} kbps ({reason})");

                TargetBitrateKbps = newBitrate;

                return new BitrateDecision
                {
                    NewBitrate = newBitrate,
                    Changed = true,
                    Reason = reason
                };
            }

            return new BitrateDecision
            {
                NewBitrate = TargetBitrateKbps,
                Changed = false,
                Reason = "stable"
            };
        }

        /// <summary>
        /// Update EWMA values with new feedback.
        /// Only updates EWMA bandwidth downward when there are actual dropped frames (network issue).
        /// Static content (low FPS, no drops) should not reduce EWMA bandwidth.
        /// </summary>
        private void UpdateEwma(QualityFeedbackMessage feedback)
        {
            // Calculate dropped frames ratio to detect actual network issues vs static content
            int totalDroppedFrames = 0;
            int totalRenderedFrames = 0;
            if (feedback.Monitors != null)
            {
                foreach (var monitor in feedback.Monitors)
                {
                    totalDroppedFrames += monitor.DroppedFrames;
                    totalRenderedFrames += monitor.RenderedFrames;
                }
            }

            // Estimate bandwidth from effective FPS ratio
            float fpsRatio = feedback.TargetFps > 0 ? feedback.EffectiveFps / feedback.TargetFps : 1f;
            double estimatedBandwidth = TargetBitrateKbps * fpsRatio;

            // Only update EWMA bandwidth downward if there are actual dropped frames (network issue)
            // Static content (low FPS, no drops) = content optimization, not network issue
            bool hasNetworkIssue = totalDroppedFrames > 0 || feedback.PacketLossRate > 0.01f;

            if (hasNetworkIssue)
            {
                // Network issue detected - update EWMA normally (can go up or down)
                _ewmaBandwidth = EWMA_ALPHA * estimatedBandwidth + (1 - EWMA_ALPHA) * _ewmaBandwidth;
            }
            else if (estimatedBandwidth > _ewmaBandwidth)
            {
                // No network issue and bandwidth estimate is higher - allow recovery
                // Use slower alpha for recovery to be conservative
                double recoveryAlpha = EWMA_ALPHA * 0.5;
                _ewmaBandwidth = recoveryAlpha * estimatedBandwidth + (1 - recoveryAlpha) * _ewmaBandwidth;
            }
            // else: No network issue, static content - keep EWMA stable (don't decrease)

            _ewmaPacketLoss = EWMA_ALPHA * feedback.AvgPacketLossRate + (1 - EWMA_ALPHA) * _ewmaPacketLoss;
            _ewmaRtt = EWMA_ALPHA * feedback.RttMs + (1 - EWMA_ALPHA) * _ewmaRtt;
        }

        /// <summary>
        /// Calculate new bitrate based on feedback and EWMA values.
        /// Uses asymmetric step sizes: faster decrease, slower increase for stability.
        /// </summary>
        private int CalculateNewBitrate(QualityFeedbackMessage feedback)
        {
            int current = TargetBitrateKbps;

            // Asymmetric step sizes for stability
            // Decrease faster (10%) than increase (5%) to react quickly to congestion
            int decreaseStep = Math.Max(500, current / 10);  // 10% decrease, min 500kbps
            int increaseStep = Math.Max(250, current / 20);  // 5% increase, min 250kbps

            // Check if we're still in warmup period
            bool inWarmup = (DateTime.UtcNow - _streamStartTime).TotalMilliseconds < WARMUP_PERIOD_MS;

            // Check if there are actual dropped frames (network issue vs static content)
            // Low FPS with NO dropped frames = static content optimization, don't reduce bitrate
            int totalDroppedFrames = 0;
            int totalRenderedFrames = 0;
            if (feedback.Monitors != null)
            {
                foreach (var monitor in feedback.Monitors)
                {
                    totalDroppedFrames += monitor.DroppedFrames;
                    totalRenderedFrames += monitor.RenderedFrames;
                }
            }
            bool hasActualProblems = totalDroppedFrames > 0 || feedback.PacketLossRate > 0.01f;

            // === DECREASE conditions (any of these triggers decrease) ===

            // High packet loss - always react, even during warmup (real network issue)
            if (feedback.PacketLossRate > PACKET_LOSS_INCREASE_THRESHOLD)
            {
                Logger.Info($"[AdaptiveBitrate] High packet loss: {feedback.PacketLossRate:P1}");
                return Math.Max(MinBitrateKbps, current - decreaseStep);
            }

            // CRITICAL FPS - only trigger if there are actual problems (dropped frames or packet loss)
            // Low FPS alone can be due to static content optimization, not network issues
            if (feedback.TargetFps > 0 && hasActualProblems)
            {
                float fpsRatio = feedback.EffectiveFps / feedback.TargetFps;
                if (fpsRatio < FPS_CRITICAL_THRESHOLD)
                {
                    // Device is severely struggling WITH evidence of problems
                    int aggressiveStep = Math.Max(1000, current / 4);  // 25% decrease, min 1Mbps
                    Logger.Info($"[AdaptiveBitrate] CRITICAL FPS with drops: {feedback.EffectiveFps:F1}/{feedback.TargetFps:F1} = {fpsRatio:P0}, drops={totalDroppedFrames}");
                    return Math.Max(MinBitrateKbps, current - aggressiveStep);
                }
            }

            // During warmup, skip normal buffer/FPS-based decreases (these are normal during startup)
            if (inWarmup)
            {
                // Emergency bypass: allow drastic reduction even during warmup
                // when conditions are catastrophic (stream is clearly broken, not just warming up)
                // Note: low FPS alone (static content) must NOT trigger - require evidence of problems
                float warmupFpsRatio = feedback.TargetFps > 0 ? feedback.EffectiveFps / feedback.TargetFps : 1f;
                bool hasProblems = feedback.PacketLossRate > 0.01f || totalDroppedFrames > 0;
                bool fpsEmergency = warmupFpsRatio < FPS_CRITICAL_THRESHOLD && hasProblems;  // < 30% target + evidence
                bool lossEmergency = feedback.PacketLossRate > 0.5f;           // > 50% packet loss (always real)

                if (fpsEmergency || lossEmergency)
                {
                    int emergencyBitrate = Math.Max(MinBitrateKbps, current / 2);  // 50% cut
                    Logger.Info($"[EmergencyWarmup] Bypass warmup: fpsRatio={warmupFpsRatio:F2}, loss={feedback.PacketLossRate:P1} → {current} → {emergencyBitrate} kbps");
                    return emergencyBitrate;
                }

                return current;
            }

            // Calculate hasSignificantDrops for remaining checks (>5% drop rate)
            bool hasSignificantDrops = totalDroppedFrames > 0 &&
                                       totalRenderedFrames > 0 &&
                                       (float)totalDroppedFrames / (totalDroppedFrames + totalRenderedFrames) > 0.05f;

            // Track network issue time for auto-recovery
            bool hasNetworkIssue = hasSignificantDrops || feedback.PacketLossRate > 0.01f;
            if (hasNetworkIssue)
            {
                _lastNetworkIssueTime = DateTime.UtcNow;
            }

            // Buffer starving - only reduce if there are actual dropped frames
            if (feedback.BufferStatus == "starving" && hasSignificantDrops)
            {
                Logger.Info($"[AdaptiveBitrate] Buffer starving with drops: {totalDroppedFrames}/{totalRenderedFrames + totalDroppedFrames}");
                return Math.Max(MinBitrateKbps, current - decreaseStep);
            }

            // FPS significantly below target - only reduce if there are actual dropped frames
            if (feedback.TargetFps > 0 && hasSignificantDrops)
            {
                float fpsRatio = feedback.EffectiveFps / feedback.TargetFps;
                if (fpsRatio < FPS_DROP_THRESHOLD)
                {
                    Logger.Info($"[AdaptiveBitrate] FPS drop with network issues: {feedback.EffectiveFps:F1}/{feedback.TargetFps:F1} = {fpsRatio:P0}");
                    return Math.Max(MinBitrateKbps, current - decreaseStep);
                }
            }

            // EWMA bandwidth check - only if there are actual drops
            if (_ewmaBandwidth < current * BANDWIDTH_SAFETY_MARGIN && hasSignificantDrops)
            {
                Logger.Info($"[AdaptiveBitrate] EWMA bandwidth low: {_ewmaBandwidth:F0} < {current * BANDWIDTH_SAFETY_MARGIN:F0}");
                return Math.Max(MinBitrateKbps, current - decreaseStep);
            }

            // === DC CONGESTION CEILING RELAXATION ===
            // Slowly raise the ceiling over time if no new congestion events
            if (_dcCongestionCeilingKbps > 0)
            {
                double timeSinceLastCongestion = (DateTime.UtcNow - _lastDcCongestionTime).TotalMilliseconds;
                if (timeSinceLastCongestion > DC_CEILING_RELAX_INTERVAL_MS)
                {
                    int oldCeiling = _dcCongestionCeilingKbps;
                    int relaxStep = Math.Max(500, (int)(_dcCongestionCeilingKbps * DC_CEILING_RELAX_STEP));
                    _dcCongestionCeilingKbps = Math.Min(MaxBitrateKbps, _dcCongestionCeilingKbps + relaxStep);
                    _lastDcCongestionTime = DateTime.UtcNow; // Reset timer for next relaxation
                    if (_dcCongestionCeilingKbps >= MaxBitrateKbps)
                    {
                        _dcCongestionCeilingKbps = 0; // Ceiling removed
                        Logger.Info($"[AdaptiveBitrate] DC ceiling removed (relaxed from {oldCeiling}kbps to max)");
                    }
                    else
                    {
                        Logger.Info($"[AdaptiveBitrate] DC ceiling relaxed: {oldCeiling} → {_dcCongestionCeilingKbps}kbps (+{DC_CEILING_RELAX_STEP:P0})");
                    }
                }
            }

            // Effective max = min(MaxBitrateKbps, dcCongestionCeiling)
            int effectiveMax = _dcCongestionCeilingKbps > 0
                ? Math.Min(MaxBitrateKbps, _dcCongestionCeilingKbps)
                : MaxBitrateKbps;

            // === AUTO-RECOVERY: Restore bitrate after stable period ===
            // If no network issues for RECOVERY_DELAY_MS and bitrate is below effective max, recover gradually
            double timeSinceLastIssue = (DateTime.UtcNow - _lastNetworkIssueTime).TotalMilliseconds;
            double timeSinceLastAdjustment = (DateTime.UtcNow - _lastAdjustmentTime).TotalMilliseconds;
            // DC congestion cooldown: after DC congestion, wait at least recoveryDelayMs before any recovery
            double timeSinceDcCongestion = (DateTime.UtcNow - _lastDcCongestionTime).TotalMilliseconds;

            int recoveryDelayMs = IsWiFiMode ? WIFI_RECOVERY_DELAY_MS : RECOVERY_DELAY_MS;
            int recoveryCooldownMs = IsWiFiMode ? WIFI_RECOVERY_COOLDOWN_MS : RECOVERY_COOLDOWN_MS;
            if (current < effectiveMax &&
                timeSinceLastIssue > recoveryDelayMs &&
                timeSinceLastAdjustment > recoveryCooldownMs &&
                timeSinceDcCongestion > recoveryDelayMs &&  // Must also wait after DC congestion
                !hasNetworkIssue)
            {
                // Aggressive recovery toward effective max (respects DC congestion ceiling)
                // WiFi: 40% of deficit per step (reaches target in ~3 steps = ~5s)
                // Wired: 25% of deficit per step (more conservative)
                int divisor = IsWiFiMode ? 3 : 4;
                int minStep = IsWiFiMode ? 1000 : 500;
                int recoveryStep = Math.Max(minStep, (effectiveMax - current) / divisor);
                int newBitrate = Math.Min(effectiveMax, current + recoveryStep);
                string ceilingInfo = _dcCongestionCeilingKbps > 0 ? $", ceiling={_dcCongestionCeilingKbps}kbps" : "";
                Logger.Info($"[AdaptiveBitrate] Auto-recovery: {current} → {newBitrate} kbps (stable for {timeSinceLastIssue/1000:F1}s{ceilingInfo})");
                return newBitrate;
            }

            // === INCREASE conditions ===
            // Only increase if content is actively streaming (not static)
            // Static content (low FPS) doesn't tell us anything about bandwidth headroom
            float currentFpsRatio = feedback.TargetFps > 0 ? feedback.EffectiveFps / feedback.TargetFps : 0f;
            bool isActiveContent = currentFpsRatio > 0.6f;  // At least 60% of target FPS = active streaming

            // Only increase if we're below effective max AND conditions are good AND enough time
            // has passed since last network issue. Without this delay, bitrate yo-yos:
            // congestion → drop → immediately climb back → congestion again.
            bool canIncrease =
                current < effectiveMax &&
                feedback.PacketLossRate < PACKET_LOSS_DECREASE_THRESHOLD &&
                (feedback.BufferStatus == "healthy" || feedback.BufferStatus == "overflow") &&
                !hasNetworkIssue &&
                isActiveContent &&
                timeSinceLastIssue > recoveryCooldownMs &&     // Must wait after network issue
                timeSinceDcCongestion > recoveryCooldownMs;    // Must wait after DC congestion too

            if (canIncrease)
            {
                int newBitrate = Math.Min(effectiveMax, current + increaseStep);

                if (newBitrate > current)
                {
                    string ceilingInfo = _dcCongestionCeilingKbps > 0 ? $", ceiling={_dcCongestionCeilingKbps}kbps" : "";
                    Logger.Info($"[AdaptiveBitrate] Active content good (FPS {currentFpsRatio:P0}), recovering to {newBitrate} kbps{ceilingInfo}");
                    return newBitrate;
                }
            }

            return current; // No change
        }

        /// <summary>
        /// Build human-readable reason string for logging/debugging.
        /// </summary>
        private string BuildReason(QualityFeedbackMessage feedback, bool isIncrease)
        {
            if (isIncrease)
            {
                return $"conditions_good (loss={feedback.PacketLossRate:P1}, buffer={feedback.BufferStatus})";
            }

            if (feedback.PacketLossRate > PACKET_LOSS_INCREASE_THRESHOLD)
                return $"high_loss_{feedback.PacketLossRate:P1}";

            if (feedback.BufferStatus == "starving")
                return "buffer_starving";

            if (feedback.TargetFps > 0 && feedback.EffectiveFps / feedback.TargetFps < FPS_DROP_THRESHOLD)
                return $"fps_drop_{feedback.EffectiveFps:F0}/{feedback.TargetFps:F0}";

            if (_ewmaBandwidth < TargetBitrateKbps * BANDWIDTH_SAFETY_MARGIN)
                return $"bandwidth_limited_{_ewmaBandwidth:F0}kbps";

            return "adaptive";
        }

        /// <summary>
        /// Force the target bitrate to a specific value (bypasses cooldown).
        /// Used by stall detection to sync controller with direct encoder changes.
        /// </summary>
        public void ForceTarget(int kbps)
        {
            int clamped = Math.Clamp(kbps, MinBitrateKbps, MaxBitrateKbps);
            TargetBitrateKbps = clamped;
            _lastAdjustmentTime = DateTime.UtcNow;
            _lastNetworkIssueTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Mark that DC congestion occurred at the given bitrate.
        /// Sets a ceiling so ABC recovery won't exceed 90% of the congestion trigger point.
        /// This prevents the sawtooth oscillation pattern:
        ///   8000 → congestion → 6400 → recover → 8000 → congestion → repeat
        /// Instead: 8000 → congestion → 6400 → recover → 7200 (ceiling) → stable
        /// </summary>
        public void MarkDcCongestion(int congestionBitrateKbps)
        {
            int newCeiling = (int)(congestionBitrateKbps * DC_CEILING_SAFETY_FACTOR);
            // Floor: ceiling must not drop below MinBitrateKbps (prevents ratchet-down to unusable levels)
            newCeiling = Math.Max(newCeiling, MinBitrateKbps);
            // Only lower the ceiling, never raise it from a congestion event
            if (_dcCongestionCeilingKbps == 0 || newCeiling < _dcCongestionCeilingKbps)
            {
                _dcCongestionCeilingKbps = newCeiling;
                Logger.Info($"[AdaptiveBitrate] DC congestion ceiling set: {newCeiling}kbps (triggered at {congestionBitrateKbps}kbps)");
            }
            _lastDcCongestionTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Mark that DC congestion has just cleared. Resets recovery timer so the full
        /// RECOVERY_DELAY_MS must elapse before bitrate starts climbing again.
        /// Without this, recovery starts almost immediately because _lastNetworkIssueTime
        /// was set when congestion STARTED, not when it CLEARED.
        /// </summary>
        public void MarkCongestionCleared()
        {
            _lastNetworkIssueTime = DateTime.UtcNow;
            _lastAdjustmentTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Reset controller to initial state.
        /// </summary>
        public void Reset()
        {
            TargetBitrateKbps = InitialBitrateKbps;
            _ewmaBandwidth = InitialBitrateKbps;
            _ewmaPacketLoss = 0;
            _ewmaRtt = 30;
            _lastAdjustmentTime = DateTime.MinValue;
            _lastNetworkIssueTime = DateTime.UtcNow; // Reset to now, not MinValue
            _dcCongestionCeilingKbps = 0; // Remove DC ceiling on reset
            AdjustmentCount = 0;

            Logger.Info($"[AdaptiveBitrate] Reset to {InitialBitrateKbps}kbps");
        }

        /// <summary>
        /// Get current statistics for debugging.
        /// </summary>
        public string GetStats()
        {
            string ceilingStr = _dcCongestionCeilingKbps > 0 ? $", DC_ceiling={_dcCongestionCeilingKbps}kbps" : "";
            return $"Target={TargetBitrateKbps}kbps, EWMA(bw={_ewmaBandwidth:F0}, loss={_ewmaPacketLoss:P2}, rtt={_ewmaRtt:F0}ms), Adjustments={AdjustmentCount}{ceilingStr}";
        }
    }
}
