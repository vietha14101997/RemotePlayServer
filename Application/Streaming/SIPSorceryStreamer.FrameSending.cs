#nullable enable
using System;
using System.Threading;
using Vortice.Direct3D11;
using SIPSorcery.Net;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    /// <summary>
    /// Check if a track requires BGRA input (no color conversion needed)
    /// </summary>
    public bool RequiresBgraInput(int monitorIndex)
    {
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return false;
        var track = _tracks[monitorIndex];
        return track.Encoder?.UsingBgraMode ?? false;
    }

    /// <summary>
    /// Check if any track requires BGRA input
    /// </summary>
    public bool AnyTrackRequiresBgraInput()
    {
        lock (_lock)
        {
            return _tracks.Exists(t => t.Encoder?.UsingBgraMode ?? false);
        }
    }

    /// <summary>
    /// Push BGRA texture directly (for NVENC BGRA mode - no color conversion)
    /// </summary>
    public void PushBgraTexture(int monitorIndex, ID3D11Texture2D bgraTexture, int width, int height, long captureTimestampMs = 0)
    {
        if (!_running || _disposed || !_connected || _isPaused) return;
        // During early capture (before Phase 3), drop frames to prevent WiFi congestion.
        // Capture hardware stays warm but no encoding/sending — saves bandwidth for ICE/DTLS.
        if (!_phase3Active) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        // Check per-monitor pause
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null) return;

        // Set shared stream start from barrier-synced capture timestamp.
        // Moved here from CalculateRtpStepFromCaptureTime to ensure _streamStartMs
        // is always set from a barrier-synced timestamp, not from callback timing.
        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        // Per-track lock: allows parallel NVENC encoding across tracks.
        // Previously used shared _lock which serialized all encoding,
        // causing the second track to accumulate transport delay on the client.
        lock (track.EncodeLock)
        {
            try
            {
                // Clear NAL accumulator for new encode cycle.
                // AMF's drain loop may fire 0-2+ callbacks per encode —
                // accumulate all NAL data, then create PendingFrame after encode returns.
                track.NalAccumulator = null;
                track.NalAccumulatorIsKeyframe = false;

                // Store capture timestamp for use in OnEncodedData callback
                track.PendingCaptureTimestampMs = captureTimestampMs;

                // Stagger countdown: decrement and trigger keyframe when it reaches 0
                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                // Force keyframe for first 5 frames, explicit request, or burst
                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
                track.ForceNextKeyframe = false;
                if (track.KeyframeBurstRemaining > 0) track.KeyframeBurstRemaining--;

                // Encode BGRA directly - no staging texture or copy needed
                track.LastEncodeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                bool encodeSuccess = track.Encoder.EncodeBgraTexture(bgraTexture, forceKeyframe: forceIdr);

                if (encodeSuccess)
                    Interlocked.Increment(ref track.EncodedFrames);

                // Create PendingFrame from accumulated callback data (if any).
                // This ensures ALL NAL data from multiple callbacks is merged into one frame.
                if (DeferredSendEnabled)
                {
                    if (track.NalAccumulator != null)
                    {
                        track.PendingFrame = new TrackInfo.PendingFrameData(
                            track.NalAccumulator, track.NalAccumulatorRtpStep);
                    }
                    else if (encodeSuccess)
                    {
                        // AMF produced no output (VCN contention / pipeline buffering)
                        long frames = Interlocked.Read(ref track.EncodedFrames);
                        if (frames <= 3 || frames % 600 == 0)
                            Logger.Warn($"[SIPSorcery] Track {monitorIndex}: encoder produced no output (frame {frames})");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.EncodedFrames) % 60 == 0)
                    Logger.Error($"[SIPSorcery] Track {monitorIndex} BGRA encode error: {ex.Message}");
            }
        }
    }

    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height, long captureTimestampMs = 0)
    {
        if (!_running || _disposed || !_connected || _isPaused) return;
        // During early capture (before Phase 3), drop frames to prevent WiFi congestion
        if (!_phase3Active) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        // Check per-monitor pause
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null) return;

        // Set shared stream start from barrier-synced capture timestamp
        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        // Per-track lock: allows parallel encoding across tracks
        lock (track.EncodeLock)
        {
            try
            {
                var device = track.Device ?? _sharedDevice;
                if (device == null) return;

                // Clear NAL accumulator for new encode cycle
                track.NalAccumulator = null;
                track.NalAccumulatorIsKeyframe = false;

                // Store capture timestamp for use in OnEncodedData callback
                track.PendingCaptureTimestampMs = captureTimestampMs;

                if (track.StagingNV12 == null)
                {
                    var desc = new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.NV12,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    track.StagingNV12 = device.CreateTexture2D(desc);
                }

                device.ImmediateContext.CopyResource(track.StagingNV12, nv12Texture);

                // Stagger countdown: decrement and trigger keyframe when it reaches 0
                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                // Force keyframe for first 5 frames, explicit request, or burst
                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
                track.ForceNextKeyframe = false;
                if (track.KeyframeBurstRemaining > 0) track.KeyframeBurstRemaining--;

                track.LastEncodeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                bool encodeSuccess = track.Encoder.EncodeTexture(track.StagingNV12, forceKeyframe: forceIdr);

                if (encodeSuccess)
                    Interlocked.Increment(ref track.EncodedFrames);

                // Create PendingFrame from accumulated callback data (if any)
                if (DeferredSendEnabled)
                {
                    if (track.NalAccumulator != null)
                    {
                        track.PendingFrame = new TrackInfo.PendingFrameData(
                            track.NalAccumulator, track.NalAccumulatorRtpStep);
                    }
                    else if (encodeSuccess)
                    {
                        long frames = Interlocked.Read(ref track.EncodedFrames);
                        if (frames <= 3 || frames % 600 == 0)
                            Logger.Warn($"[SIPSorcery] Track {monitorIndex}: encoder produced no output (frame {frames})");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.EncodedFrames) % 60 == 0)
                    Logger.Error($"[SIPSorcery] Track {monitorIndex} encode error: {ex.Message}");
            }
        }
    }

    private void OnEncodedData(TrackInfo track, byte[] nalData, bool isKeyframe, long pts100ns)
    {
        if (!_running || _pc == null || !_connected) return;
        if (_pc.connectionState != RTCPeerConnectionState.connected) return;

        // Track encode latency for diagnostics
        long encodeStartTicks = track.LastEncodeStartTicks;
        if (encodeStartTicks > 0)
        {
            long encodeEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long latencyUs = (encodeEndTicks - encodeStartTicks) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            Interlocked.Add(ref track.EncodeLatencySum, latencyUs);
            Interlocked.Increment(ref track.EncodeLatencyCount);
        }

        try
        {
            // Convert to Annex B if needed and strip AUD
            byte[] au = nalData;
            if (!ContainsAnnexBStartCode(au))
                au = TryConvertAvccToAnnexB(au);
            au = StripLeadingAud(au);

            // Use capture timestamp if available, fall back to encoder PTS
            long captureMs = track.PendingCaptureTimestampMs;
            uint rtpStep = captureMs > 0
                ? CalculateRtpStepFromCaptureTime(track, captureMs)
                : CalculateRtpStep(track, pts100ns);

            // Log per-track diagnostics on keyframe sends
            if (isKeyframe)
            {
                long encCount = Interlocked.Read(ref track.EncodeLatencyCount);
                long avgEncUs = encCount > 0 ? Interlocked.Read(ref track.EncodeLatencySum) / encCount : 0;
                Logger.Debug($"[SIPSorcery] Track {track.Index} KEYFRAME: rtpStep={rtpStep}, captureMs={captureMs}, " +
                    $"avgEncLatency={avgEncUs}us, sentFrames={Interlocked.Read(ref track.SentFrames)}");
            }

            // DEBUG: Log NAL types for first few frames only (avoid spam)
            long frameNum = Interlocked.Read(ref track.SentFrames);
            if (frameNum < 5)
            {
                var nalTypes = GetNalTypes(au);
                Logger.Debug($"[SIPSorcery] Track {track.Index} frame #{frameNum}: {au.Length}B, NAL types=[{string.Join(",", nalTypes)}], key={isKeyframe}");
            }

            // DEBUG: Log VideoStreamList info on first frame of each track
            if (frameNum == 0)
            {
                var streamCount = _pc.VideoStreamList?.Count ?? 0;
                Logger.Info($"[SIPSorcery] Track {track.Index} first frame: VideoStreamList.Count={streamCount}, rtpStep={rtpStep}");

                // Log SSRC info for each video stream
                if (_pc.VideoStreamList != null)
                {
                    for (int i = 0; i < Math.Min(3, _pc.VideoStreamList.Count); i++)
                    {
                        var vs = _pc.VideoStreamList[i];
                        Logger.Info($"[SIPSorcery] VideoStream[{i}]: SSRC={vs.LocalTrack?.Ssrc ?? 0}");
                    }
                }
            }

            if (DeferredSendEnabled)
            {
                // Accumulate: AMF's drain loop may fire 0-2+ callbacks per encode.
                // First callback sets rtpStep; subsequent callbacks append NAL data.
                // PendingFrame is created AFTER encode returns in PushBgraTexture/PushTexture.
                if (track.NalAccumulator == null)
                {
                    track.NalAccumulator = au;
                    track.NalAccumulatorRtpStep = rtpStep;
                }
                else
                {
                    // Merge: [existing NALs] + [new NALs]
                    var merged = new byte[track.NalAccumulator.Length + au.Length];
                    Buffer.BlockCopy(track.NalAccumulator, 0, merged, 0, track.NalAccumulator.Length);
                    Buffer.BlockCopy(au, 0, merged, track.NalAccumulator.Length, au.Length);
                    track.NalAccumulator = merged;
                }
                if (isKeyframe) track.NalAccumulatorIsKeyframe = true;
            }
            else
            {
                // Immediate send (single-monitor or no barrier sync)
                SendFrameImmediate(track, au, rtpStep, frameNum);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                Logger.Error($"[SIPSorcery] Track {track.Index} send error: {ex.Message}");
        }
    }

    private void SendFrameImmediate(TrackInfo track, byte[] au, uint rtpStep, long frameNum)
    {
        if (_pc!.VideoStreamList != null && track.Index < _pc.VideoStreamList.Count)
        {
            var videoStream = _pc.VideoStreamList[track.Index];
            videoStream.SendVideo(rtpStep, au);
            Interlocked.Increment(ref track.SentFrames);
        }
        else if (track.Index == 0)
        {
            _pc!.SendVideo(rtpStep, au);
            Interlocked.Increment(ref track.SentFrames);
        }
        else
        {
            if (frameNum < 5)
                Logger.Info($"[SIPSorcery] Track {track.Index} SKIP: VideoStreamList null or index out of range");
        }
    }

    /// <summary>
    /// Send all buffered frames in alternating track order.
    /// Called by the post-encode barrier after all tracks finish encoding.
    /// Alternating order prevents one track's RTP packets from consistently
    /// arriving before the other's, which causes client jitter buffer asymmetry.
    /// </summary>
    public void FlushAllPendingFrames()
    {
        if (!_running || _pc == null || !_connected) return;

        // Snapshot tracks under lock to prevent race with CloseConnection._tracks.Clear()
        TrackInfo[] snapshot;
        lock (_lock) { snapshot = _tracks.ToArray(); }

        long frame = Interlocked.Increment(ref _flushFrameCounter);
        bool reverse = (frame % 2) == 0;
        int count = snapshot.Length;

        for (int iter = 0; iter < count; iter++)
        {
            int i = reverse ? (count - 1 - iter) : iter;
            var track = snapshot[i];
            var pending = track.PendingFrame;
            if (pending == null) continue;
            track.PendingFrame = null;

            try
            {
                long frameNum = Interlocked.Read(ref track.SentFrames);
                SendFrameImmediate(track, pending.Au, pending.RtpStep, frameNum);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                    Logger.Error($"[SIPSorcery] Track {i} flush error: {ex.Message}");
            }
        }
    }
}
