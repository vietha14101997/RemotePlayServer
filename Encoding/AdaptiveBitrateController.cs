#nullable enable
using System;
using RemotePlayServer.Protocol;

namespace RemotePlayServer.Encoding
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

        // Rate limiting for adjustments
        private DateTime _lastAdjustmentTime = DateTime.MinValue;
        private const int ADJUSTMENT_COOLDOWN_MS = 2000; // 2 second cooldown between adjustments

        // Warmup period - don't reduce bitrate due to FPS/buffer issues during startup
        private DateTime _streamStartTime = DateTime.MinValue;
        private const int WARMUP_PERIOD_MS = 15000; // 15 second warmup period

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
        /// Initialize controller with starting bitrate.
        /// </summary>
        /// <param name="initialBitrateKbps">Initial target bitrate in kbps.</param>
        /// <param name="maxBitrateKbps">Maximum allowed bitrate (optional, defaults to 50Mbps).</param>
        public void Initialize(int initialBitrateKbps, int? maxBitrateKbps = null)
        {
            InitialBitrateKbps = initialBitrateKbps;
            TargetBitrateKbps = initialBitrateKbps;

            if (maxBitrateKbps.HasValue)
                MaxBitrateKbps = maxBitrateKbps.Value;

            // Initialize EWMA with current values
            _ewmaBandwidth = initialBitrateKbps;
            _ewmaPacketLoss = 0;
            _ewmaRtt = 30;

            // Start warmup period
            _streamStartTime = DateTime.UtcNow;

            Console.WriteLine($"[AdaptiveBitrate] Initialized: target={initialBitrateKbps}kbps, range=[{MinBitrateKbps}-{MaxBitrateKbps}]kbps, warmup={WARMUP_PERIOD_MS}ms");
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

            // Check cooldown
            var timeSinceLastAdjustment = (DateTime.UtcNow - _lastAdjustmentTime).TotalMilliseconds;
            if (timeSinceLastAdjustment < ADJUSTMENT_COOLDOWN_MS)
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
                Console.WriteLine($"[AdaptiveBitrate] Adjusting: {TargetBitrateKbps} → {newBitrate} kbps ({reason})");

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
        /// </summary>
        private void UpdateEwma(QualityFeedbackMessage feedback)
        {
            // Estimate bandwidth from effective FPS ratio
            float fpsRatio = feedback.TargetFps > 0 ? feedback.EffectiveFps / feedback.TargetFps : 1f;
            double estimatedBandwidth = TargetBitrateKbps * fpsRatio;

            _ewmaBandwidth = EWMA_ALPHA * estimatedBandwidth + (1 - EWMA_ALPHA) * _ewmaBandwidth;
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

            // === DECREASE conditions (any of these triggers decrease) ===

            // High packet loss - always react, even during warmup (real network issue)
            if (feedback.PacketLossRate > PACKET_LOSS_INCREASE_THRESHOLD)
            {
                Console.WriteLine($"[AdaptiveBitrate] High packet loss: {feedback.PacketLossRate:P1}");
                return Math.Max(MinBitrateKbps, current - decreaseStep);
            }

            // CRITICAL FPS - bypass warmup if FPS is extremely low (device cannot handle bitrate)
            if (feedback.TargetFps > 0)
            {
                float fpsRatio = feedback.EffectiveFps / feedback.TargetFps;
                if (fpsRatio < FPS_CRITICAL_THRESHOLD)
                {
                    // Device is severely struggling - aggressive bitrate reduction even during warmup
                    int aggressiveStep = Math.Max(1000, current / 4);  // 25% decrease, min 1Mbps
                    Console.WriteLine($"[AdaptiveBitrate] CRITICAL FPS: {feedback.EffectiveFps:F1}/{feedback.TargetFps:F1} = {fpsRatio:P0} - aggressive reduction");
                    return Math.Max(MinBitrateKbps, current - aggressiveStep);
                }
            }

            // During warmup, skip normal buffer/FPS-based decreases (these are normal during startup)
            if (inWarmup)
            {
                // Only log once every few seconds to avoid spam
                return current;
            }

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
            bool hasSignificantDrops = totalDroppedFrames > 0 &&
                                       totalRenderedFrames > 0 &&
                                       (float)totalDroppedFrames / (totalDroppedFrames + totalRenderedFrames) > 0.05f; // >5% drop rate

            // Buffer starving - only reduce if there are actual dropped frames
            if (feedback.BufferStatus == "starving" && hasSignificantDrops)
            {
                Console.WriteLine($"[AdaptiveBitrate] Buffer starving with drops: {totalDroppedFrames}/{totalRenderedFrames + totalDroppedFrames}");
                return Math.Max(MinBitrateKbps, current - decreaseStep);
            }

            // FPS significantly below target - only reduce if there are actual dropped frames
            if (feedback.TargetFps > 0 && hasSignificantDrops)
            {
                float fpsRatio = feedback.EffectiveFps / feedback.TargetFps;
                if (fpsRatio < FPS_DROP_THRESHOLD)
                {
                    Console.WriteLine($"[AdaptiveBitrate] FPS drop with network issues: {feedback.EffectiveFps:F1}/{feedback.TargetFps:F1} = {fpsRatio:P0}");
                    return Math.Max(MinBitrateKbps, current - decreaseStep);
                }
            }

            // EWMA bandwidth check - only if there are actual drops
            if (_ewmaBandwidth < current * BANDWIDTH_SAFETY_MARGIN && hasSignificantDrops)
            {
                Console.WriteLine($"[AdaptiveBitrate] EWMA bandwidth low: {_ewmaBandwidth:F0} < {current * BANDWIDTH_SAFETY_MARGIN:F0}");
                return Math.Max(MinBitrateKbps, current - decreaseStep);
            }

            // === INCREASE conditions (all must be true) ===

            bool canIncrease =
                feedback.PacketLossRate < PACKET_LOSS_DECREASE_THRESHOLD &&
                (feedback.BufferStatus == "healthy" || feedback.BufferStatus == "overflow") &&
                _ewmaBandwidth > current * 1.2 &&  // EWMA bandwidth 20% above current
                current < MaxBitrateKbps;

            if (canIncrease)
            {
                // Don't exceed initial bitrate by too much (prevent runaway)
                int maxIncreaseTarget = (int)(InitialBitrateKbps * 1.5);
                int newBitrate = Math.Min(maxIncreaseTarget, current + increaseStep);
                newBitrate = Math.Min(MaxBitrateKbps, newBitrate);

                if (newBitrate > current)
                {
                    Console.WriteLine($"[AdaptiveBitrate] Conditions good, increasing bitrate");
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
        /// Reset controller to initial state.
        /// </summary>
        public void Reset()
        {
            TargetBitrateKbps = InitialBitrateKbps;
            _ewmaBandwidth = InitialBitrateKbps;
            _ewmaPacketLoss = 0;
            _ewmaRtt = 30;
            _lastAdjustmentTime = DateTime.MinValue;
            AdjustmentCount = 0;

            Console.WriteLine($"[AdaptiveBitrate] Reset to {InitialBitrateKbps}kbps");
        }

        /// <summary>
        /// Get current statistics for debugging.
        /// </summary>
        public string GetStats()
        {
            return $"Target={TargetBitrateKbps}kbps, EWMA(bw={_ewmaBandwidth:F0}, loss={_ewmaPacketLoss:P2}, rtt={_ewmaRtt:F0}ms), Adjustments={AdjustmentCount}";
        }
    }
}
