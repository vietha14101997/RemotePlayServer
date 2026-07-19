#nullable enable
using System;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming
{
    /// <summary>
    /// Result of an adaptive-FPS decision.
    /// </summary>
    public class FpsDecision
    {
        public int NewFps { get; set; }
        public bool Changed { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Pure host-side adaptive frame-rate controller.
    ///
    /// Lowers the effective target FPS on sustained low-motion (desktop) content and ramps
    /// back up when the content saturates the current cap. Operates on the DELIVERED active-frame
    /// rate — frames where the desktop actually changed AND were sent — NOT wall-clock FPS.
    ///
    /// Key insight (self-correcting saturation): the observed active rate can never exceed the
    /// current cap. So "active rate near the cap" means the content wants MORE than we're giving
    /// it → ramp up; "active rate well below the cap" means the content isn't filling the cap
    /// → ramp down. This lets us recover to the ceiling even after ramping to the floor.
    ///
    /// Asymmetric timing mirrors <see cref="AdaptiveBitrateController"/>: ramp DOWN fast
    /// (short cooldown) to save load quickly, ramp UP slow (longer cooldown) to avoid flicker.
    /// Coarse buckets + a hysteresis dead-band between the up/down thresholds prevent oscillation.
    ///
    /// Pure + deterministic apart from the internal cooldown clock (same pattern as ABC; tests
    /// bypass it by setting <c>_lastChangeTime</c> via reflection). Only moves FPS — never bitrate
    /// (ABC already treats low-FPS-no-drops as static content and will not cut bitrate).
    /// </summary>
    public class AdaptiveFpsController
    {
        // Coarse FPS ladder. The effective ladder is this set clamped into [floor, ceil],
        // always including floor and ceil as valid stops.
        private static readonly int[] Buckets = { 15, 20, 30, 45, 60 };

        // Content saturating >=85% of the current cap → wants more → ramp up.
        private const double SATURATION_RATIO = 0.85;
        // Content filling <50% of the current cap → not motion-bound → ramp down.
        private const double IDLE_RATIO = 0.50;
        // Between IDLE and SATURATION = hysteresis dead-band → hold.

        private const int DOWN_COOLDOWN_MS = 1000; // ramp down fast
        private const int UP_COOLDOWN_MS = 2000;   // ramp up slow

        private DateTime _lastChangeTime = DateTime.MinValue;

        /// <summary>Number of FPS changes this controller has made (diagnostics/tests).</summary>
        public int ChangeCount { get; private set; }

        /// <summary>
        /// Decide the next target FPS from the delivered active-frame rate over a window.
        /// </summary>
        /// <param name="activeFramesInWindow">Frames delivered where the desktop changed, this window.</param>
        /// <param name="windowMs">Window length in ms (used to derive active rate).</param>
        /// <param name="currentTargetFps">Current effective target FPS.</param>
        /// <param name="floorFps">Lowest FPS allowed (static content floor).</param>
        /// <param name="ceilFps">Highest FPS allowed (the client-chosen ceiling).</param>
        public FpsDecision Decide(int activeFramesInWindow, int windowMs, int currentTargetFps, int floorFps, int ceilFps)
        {
            // Normalize the operating range. A degenerate range (floor >= ceil) pins to ceil.
            floorFps = Math.Clamp(floorFps, 1, ceilFps);
            int current = Math.Clamp(currentTargetFps, floorFps, ceilFps);

            if (windowMs <= 0 || floorFps >= ceilFps)
                return NoChange(current, "no_range");

            double activeRate = activeFramesInWindow * 1000.0 / windowMs;

            bool wantUp = activeRate >= current * SATURATION_RATIO && current < ceilFps;
            bool wantDown = activeRate < current * IDLE_RATIO && current > floorFps;

            var elapsed = (DateTime.UtcNow - _lastChangeTime).TotalMilliseconds;

            if (wantDown)
            {
                if (elapsed < DOWN_COOLDOWN_MS) return NoChange(current, "cooldown_down");
                int next = NextLowerBucket(current, floorFps, ceilFps);
                if (next < current) return Change(next, $"static_{activeRate:F0}fps→down");
                return NoChange(current, "at_floor");
            }

            if (wantUp)
            {
                if (elapsed < UP_COOLDOWN_MS) return NoChange(current, "cooldown_up");
                int next = NextHigherBucket(current, floorFps, ceilFps);
                if (next > current) return Change(next, $"saturated_{activeRate:F0}fps→up");
                return NoChange(current, "at_ceil");
            }

            return NoChange(current, "stable");
        }

        /// <summary>Reset cooldown/counters (e.g. when the client ceiling changes and we snap to it).</summary>
        public void Reset()
        {
            _lastChangeTime = DateTime.MinValue;
            ChangeCount = 0;
        }

        private FpsDecision Change(int newFps, string reason)
        {
            _lastChangeTime = DateTime.UtcNow;
            ChangeCount++;
            Logger.Info($"[AdaptiveFps] {reason}: → {newFps}fps");
            return new FpsDecision { NewFps = newFps, Changed = true, Reason = reason };
        }

        private static FpsDecision NoChange(int fps, string reason) =>
            new FpsDecision { NewFps = fps, Changed = false, Reason = reason };

        /// <summary>Largest ladder stop strictly below <paramref name="current"/>, floored.</summary>
        private static int NextLowerBucket(int current, int floor, int ceil)
        {
            int best = floor; // floor is always a valid stop
            foreach (int b in Buckets)
            {
                if (b >= floor && b <= ceil && b < current && b > best)
                    best = b;
            }
            return best;
        }

        /// <summary>Smallest ladder stop strictly above <paramref name="current"/>, capped.</summary>
        private static int NextHigherBucket(int current, int floor, int ceil)
        {
            int best = ceil;
            foreach (int b in Buckets)
            {
                if (b >= floor && b <= ceil && b > current && b < best)
                    best = b;
            }
            return best;
        }
    }
}
