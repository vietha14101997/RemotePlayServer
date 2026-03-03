#nullable enable
using System;
using System.Threading;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    private uint CalculateRtpStep(TrackInfo track, long pts100ns)
    {
        const int ClockRate = 90000;
        uint fallback = (uint)Math.Max(1, ClockRate / Math.Max(1, _fps));

        lock (track)
        {
            if (!track.TimestampInitialized)
            {
                track.LastPts100ns = pts100ns;
                track.TimestampInitialized = true;
                // Seed with standard increment instead of absolute value
                return fallback;
            }

            long delta = pts100ns - track.LastPts100ns;
            if (delta <= 0 || delta > 5_000_000)
            {
                track.LastPts100ns = pts100ns;
                return fallback;
            }

            track.LastPts100ns = pts100ns;
            uint step = (uint)Math.Max(1, ClockRate * delta / 10_000_000L);
            return step;
        }
    }

    /// <summary>
    /// Calculate RTP step from capture wallclock time instead of encoder PTS.
    /// All tracks sharing the same _streamStartMs produce identical absolute RTP
    /// for simultaneously-captured frames (barrier-synced), fixing multi-track desync.
    /// Audio also uses the same _streamStartMs, fixing A/V desync.
    /// </summary>
    private uint CalculateRtpStepFromCaptureTime(TrackInfo track, long captureTimestampMs)
    {
        const int ClockRate = 90000;
        uint fallback = (uint)Math.Max(1, ClockRate / Math.Max(1, _fps));

        // _streamStartMs is initialized in PushBgraTexture/PushTexture from barrier-synced
        // capture timestamp, ensuring all tracks share the same origin.
        long startMs = Interlocked.Read(ref _streamStartMs);
        long elapsedMs = captureTimestampMs - startMs;
        if (elapsedMs < 0) elapsedMs = 0;

        // Convert elapsed wallclock to absolute RTP timestamp (90kHz)
        uint absoluteRtp = (uint)((long)ClockRate * elapsedMs / 1000L);

        lock (track)
        {
            if (!track.CaptureClockInitialized)
            {
                track.CaptureClockInitialized = true;
                track.LastAbsoluteRtp = absoluteRtp;
                // Return normal increment instead of absolute timestamp.
                // SIPSorcery's VideoStream.SendVideo adds this step to current timestamp.
                // A massive step here causes jitter-buffer overflow on client.
                return fallback;
            }

            // Step = difference from last sent absolute RTP
            uint step;
            if (absoluteRtp > track.LastAbsoluteRtp)
            {
                step = absoluteRtp - track.LastAbsoluteRtp;
            }
            else
            {
                // Same or older timestamp (duplicate frame, or clock wraparound)
                step = fallback;
            }

            track.LastAbsoluteRtp = absoluteRtp;

            // Clamp to reasonable range: 1 tick to 500ms worth of ticks
            step = Math.Clamp(step, 1, (uint)(ClockRate / 2));
            return step;
        }
    }

    /// <summary>
    /// Calculate the INITIAL audio RTP timestamp offset to anchor audio relative to video.
    /// Called ONLY for the first audio frame, under _audioSyncLock.
    /// All subsequent frames use fixed 480 increment.
    /// This ensures audio and video RTP timestamps share the same _streamStartMs origin,
    /// allowing the client to correctly align them via RTCP Sender Reports.
    /// </summary>
    /// <remarks>Caller must hold _audioSyncLock.</remarks>
    private uint CalculateAudioRtpStep(long audioTimestampMs)
    {
        const int AudioClockRate = 48000;
        const uint DefaultStep = 480; // 10ms at 48kHz

        // Initialize shared stream start time (first frame from any track sets this)
        if (Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, audioTimestampMs, -1);

        long startMs = Interlocked.Read(ref _streamStartMs);
        long elapsedMs = audioTimestampMs - startMs;
        if (elapsedMs < 0) elapsedMs = 0;

        // Convert elapsed wallclock to absolute audio RTP timestamp (48kHz)
        uint absoluteRtp = (uint)((long)AudioClockRate * elapsedMs / 1000L);

        _audioClockInitialized = true;
        _lastAbsoluteAudioRtp = absoluteRtp;
        return absoluteRtp > 0 ? absoluteRtp : DefaultStep;
    }

    /// <summary>
    /// Reset sync clocks so next frames start from a fresh time origin.
    /// Call when Phase 3 starts to clear stale state from early capture.
    /// This prevents the client's jitter buffer from inflating due to
    /// RTP timestamp gaps between early-capture and real streaming.
    /// </summary>
    public void ResetSyncState()
    {
        Interlocked.Exchange(ref _streamStartMs, -1);

        // Reset pause state — client may have paused before reconnecting.
        // If _isPaused is not cleared, PushBgraTexture/PushTexture returns early
        // and no frames reach the encoder, causing 0 frames sent (silent starvation).
        _isPaused = false;
        _monitorPaused = new bool[_monitorCount]; // Reset per-monitor pause too

        lock (_audioSyncLock)
        {
            _audioClockInitialized = false;
            _lastAbsoluteAudioRtp = 0;
        }
        lock (_lock)
        {
            foreach (var track in _tracks)
            {
                track.CaptureClockInitialized = false;
                track.LastAbsoluteRtp = 0;
                track.TimestampInitialized = false;
                track.LastPts100ns = -1;
                track.IsSessionStarted = false;

                // Reset frame counters for new session context (fixes missing logs on reconnect)
                Interlocked.Exchange(ref track.SentFrames, 0);
                Interlocked.Exchange(ref track.EncodedFrames, 0);

                // Force next encode to be a keyframe — ensures first frame of new session is IDR.
                // Works in tandem with the P-frame guard in OnEncodedData which drops stale
                // pipeline frames that arrive before the IDR.
                track.ForceNextKeyframe = true;
                track.KeyframeBurstRemaining = 0; // Clear any pending burst from old session

                // Flush encoder HW pipeline to drain stale frames from previous session.
                // AMF/NVENC have async pipelines — without flush, old P-frames arrive
                // AFTER forceKeyframe=true is set, causing client decoder failure.
                if (track.Encoder != null)
                {
                    try { track.Encoder.Flush(); }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[SIPSorcery] Track {track.Index}: Encoder flush error (non-fatal): {ex.Message}");
                    }
                }
            }
        }
        // Reset deferred send counter for clean alternation on reconnect
        Interlocked.Exchange(ref _flushFrameCounter, 0);
        // Mark Phase 3 as pending — actual activation happens via barrier sync
        // (OnNextBarrierSync callback) to ensure all tracks start from the same
        // barrier cycle. If no barrier is available, activate immediately.
        _phase3PendingActivation = true;
        Logger.Info("[SIPSorcery] Sync state reset, Phase 3 pending activation");
    }
}
