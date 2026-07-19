#nullable enable
using System;
using System.Threading;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Capture;

namespace RemotePlayServer.Application.Streaming
{
    /// <summary>
    /// Thin per-session coordinator that drives the pure <see cref="AdaptiveFpsController"/>.
    ///
    /// Once per ~1s window it samples the busiest monitor's delivered active-frame count, asks
    /// the controller for a new target FPS, and — only when the feature flag is ON and the
    /// controller says Changed — applies it through the EXISTING serialized FPS path
    /// (<see cref="SIPSorceryStreamer.UpdateConfig"/> + <see cref="PerMonitorCapture.SetTargetFps"/>),
    /// so QSV/LibAv recreate-fallback and NVENC/AMF live-reconfig are all reused (DRY).
    ///
    /// Precedence (plan Q5): the client-chosen FPS is the CEILING. The controller only ramps
    /// within [floorFps, clientCeiling]. When the client changes FPS, <see cref="OnClientCeilingChanged"/>
    /// resets the controller and snaps to the new ceiling.
    ///
    /// Flag OFF ⇒ pure no-op (FPS stays at the client/target value = current behaviour). On the
    /// ON→OFF edge the coordinator restores the ceiling once so disabling the feature = full FPS.
    /// Uses a single lightweight <see cref="Timer"/> (no dedicated thread).
    /// </summary>
    public sealed class AdaptiveFpsCoordinator : IDisposable
    {
        private const int WindowMs = 1000;
        private const int MinFps = 10;
        private const int MaxFps = 60;

        private readonly PerMonitorCapture _capture;
        private readonly SIPSorceryStreamer _streamer;
        private readonly Func<int> _ceilingProvider; // client-chosen max FPS
        private readonly AdaptiveFpsController _controller = new();
        private readonly object _lock = new(); // serialize tick vs client-ceiling change

        private Timer? _timer;
        private long _lastTickTicks;
        private int _currentTargetFps;
        private bool _wasEnabled;
        private volatile bool _running;

        public AdaptiveFpsCoordinator(
            PerMonitorCapture capture,
            SIPSorceryStreamer streamer,
            Func<int> ceilingProvider,
            int initialFps)
        {
            _capture = capture;
            _streamer = streamer;
            _ceilingProvider = ceilingProvider;
            _currentTargetFps = Math.Clamp(initialFps, MinFps, MaxFps);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _lastTickTicks = Environment.TickCount64;
            _timer = new Timer(OnTick, null, WindowMs, WindowMs);
            Logger.Info($"[AdaptiveFps] Coordinator started (window={WindowMs}ms, flag-gated, initial={_currentTargetFps}fps)");
        }

        private void OnTick(object? state)
        {
            if (!_running) return;
            lock (_lock)
            {
                if (!_running) return; // re-check under lock: Dispose may have run concurrently
                try
                {
                    long now = Environment.TickCount64;
                    int windowMs = (int)Math.Max(1, now - _lastTickTicks);
                    _lastTickTicks = now;

                    // Read the mode profile as ONE atomic snapshot — never field-by-field —
                    // so a concurrent mode toggle can't mix an old ceiling with a new flag.
                    var profile = EfficiencyConfig.Active;

                    // Effective ceiling = min(client-chosen FPS, current mode's ceiling).
                    // Efficiency mode caps at StreamModeProfile.EfficiencyCeilFps (30); Gaming = 60.
                    int ceil = Math.Min(
                        Math.Clamp(_ceilingProvider(), MinFps, MaxFps),
                        Math.Clamp(profile.CeilFps, MinFps, MaxFps));

                    // Always drain the counter so a stale window is never carried across toggles.
                    int activeFrames = _capture.TakeMaxActiveFrameCount();

                    if (!profile.AdaptiveFpsEnabled)
                    {
                        // ON→OFF edge: restore full ceiling so disabling the feature = full FPS.
                        if (_wasEnabled && _currentTargetFps != ceil)
                        {
                            Apply(ceil);
                            _controller.Reset();
                        }
                        _wasEnabled = false;
                        return;
                    }
                    _wasEnabled = true;

                    // Ceiling may have dropped (client lowered FPS) — snap down and resync.
                    if (_currentTargetFps > ceil)
                    {
                        Apply(ceil);
                        _controller.Reset();
                        return;
                    }

                    int floor = Math.Clamp(profile.FloorFps, MinFps, ceil);
                    var decision = _controller.Decide(activeFrames, windowMs, _currentTargetFps, floor, ceil);
                    if (decision.Changed)
                        Apply(decision.NewFps);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[AdaptiveFps] Coordinator tick error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Client changed FPS: it becomes the new ceiling and we snap to it (plan Q5).
        /// The existing protocol path already applied it to streamer+capture; this only
        /// resyncs the coordinator's internal target so the controller ramps from the new ceiling.
        /// </summary>
        public void OnClientCeilingChanged(int newCeiling)
        {
            lock (_lock)
            {
                _currentTargetFps = Math.Clamp(newCeiling, MinFps, MaxFps);
                _controller.Reset();
            }
        }

        // Always called under _lock (from OnTick). Preserves the adaptive bitrate controller —
        // an FPS-only ramp must never reset bitrate to max / restart warmup (see UpdateConfig).
        private void Apply(int fps)
        {
            _currentTargetFps = fps;
            try { _streamer.UpdateConfig(fps, null, preserveBitrateController: true); }
            catch (Exception ex) { Logger.Warn($"[AdaptiveFps] UpdateConfig({fps}) failed: {ex.Message}"); }
            try { _capture.SetTargetFps(fps); }
            catch (Exception ex) { Logger.Warn($"[AdaptiveFps] SetTargetFps({fps}) failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            _running = false;
            // Blocking dispose: wait for any in-flight tick to finish so we never call
            // UpdateConfig/SetFps into a streamer that CleanupAsync is about to dispose.
            try
            {
                var timer = _timer;
                _timer = null;
                if (timer != null)
                {
                    using var done = new ManualResetEvent(false);
                    if (timer.Dispose(done))
                        done.WaitOne(2000);
                }
            }
            catch { }
        }
    }
}
