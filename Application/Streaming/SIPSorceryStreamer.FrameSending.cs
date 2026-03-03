#nullable enable
using System;
using System.Linq;
using System.Threading;
using Vortice.Direct3D11;
using SIPSorcery.Net;
using SIPSorcery.Media;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Infrastructure.Network;

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
        if (!_phase3Active) return;
        
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        var track = _tracks[monitorIndex];
        if (track.Track == null || track.Encoder == null) return;
        
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        lock (track.EncodeLock)
        {
            try
            {
                track.PendingFrame = null;
                track.PendingCaptureTimestampMs = captureTimestampMs;

                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
                track.ForceNextKeyframe = false;
                if (track.KeyframeBurstRemaining > 0) track.KeyframeBurstRemaining--;

                track.LastEncodeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                bool encodeSuccess = track.Encoder.EncodeBgraTexture(bgraTexture, forceKeyframe: forceIdr);

                if (encodeSuccess)
                    Interlocked.Increment(ref track.EncodedFrames);
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
        if (!_phase3Active) return;

        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        var track = _tracks[monitorIndex];
        if (track.Track == null || track.Encoder == null) return;
        
        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        lock (track.EncodeLock)
        {
            try
            {
                var device = track.Device ?? _sharedDevice;
                if (device == null) return;

                track.PendingFrame = null;
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

                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                bool forceIdr = frameNum < 5 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
                track.ForceNextKeyframe = false;
                if (track.KeyframeBurstRemaining > 0) track.KeyframeBurstRemaining--;

                track.LastEncodeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                bool encodeSuccess = track.Encoder.EncodeTexture(track.StagingNV12, forceKeyframe: forceIdr);

                if (encodeSuccess)
                    Interlocked.Increment(ref track.EncodedFrames);
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

        // Track encode latency (from PushTexture/PushBgraTexture start to callback)
        long startTicks = track.LastEncodeStartTicks;
        if (startTicks > 0)
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
            long us = elapsed * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
            Interlocked.Add(ref track.EncodeLatencySum, us);
            Interlocked.Increment(ref track.EncodeLatencyCount);
        }

        try
        {
            // First-frame logging for H265 pipeline debugging
            long earlyFrameNum = Interlocked.Read(ref track.SentFrames);
            if (earlyFrameNum < 3)
                Logger.Info($"[SIPSorcery] Track {track.Index} OnEncodedData: {nalData.Length} bytes, keyframe={isKeyframe}, annexB={ContainsAnnexBStartCode(nalData)}");

            // CRITICAL: Drop P-frames that arrive before the first IDR of this session.
            // AMF/NVENC encoders have a hardware pipeline — stale P-frames from the
            // previous session can drain AFTER forceKeyframe=true is requested,
            // arriving before the actual IDR. Sending a P-frame as the first frame
            // causes the client decoder to fail and trigger a reconnect loop.
            if (!isKeyframe && Interlocked.Read(ref track.SentFrames) == 0)
            {
                if (earlyFrameNum % 10 == 0)
                    Logger.Info($"[SIPSorcery] Track {track.Index}: Dropping stale P-frame before first IDR ({nalData.Length} bytes) - requesting keyframe");
                
                // Force IDR on next encode opportunity
                track.ForceNextKeyframe = true;

                // Track failures in H265 mode
                if (_negotiatedCodec == RemotePlayServer.Infrastructure.Encoding.VideoCodec.H265)
                {
                    track.H265FailureStreak++;
                    if (track.H265FailureStreak == 60) // Threshold for fallback (approx 1-2s @ 30-60fps)
                    {
                        Logger.Warn($"[SIPSorcery] Track {track.Index} H265 stability threshold reached ({track.H265FailureStreak} drops). Suggesting H.264 fallback.");
                        // Force a reconnect with H.264 preference if many tracks fail
                        RequestH264Fallback();
                    }
                }
                return;
            }

            if (isKeyframe)
            {
                track.IsDecodable = true;
                track.H265FailureStreak = 0; // Reset streak on successful keyframe sent
            }

            byte[] au = nalData;
            if (!ContainsAnnexBStartCode(au))
                au = TryConvertAvccToAnnexB(au);
            au = StripLeadingAud(au);

            long captureMs = track.PendingCaptureTimestampMs;
            uint rtpStep = captureMs > 0
                ? CalculateRtpStepFromCaptureTime(track, captureMs)
                : CalculateRtpStep(track, pts100ns);

            long frameNum = Interlocked.Read(ref track.SentFrames);
            if (DeferredSendEnabled)
            {
                if (track.PendingFrame == null)
                {
                    track.PendingFrame = new TrackInfo.PendingFrameData(au, rtpStep);
                }
                else
                {
                    var merged = new byte[track.PendingFrame.Au.Length + au.Length];
                    Buffer.BlockCopy(track.PendingFrame.Au, 0, merged, 0, track.PendingFrame.Au.Length);
                    Buffer.BlockCopy(au, 0, merged, track.PendingFrame.Au.Length, au.Length);
                    track.PendingFrame = new TrackInfo.PendingFrameData(merged, track.PendingFrame.RtpStep);
                }
                // (isKeyframe flag is no longer used for session-start guard here to avoid race with SendRtpPacket)
            }
            else
            {
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
        // Update absolute timestamp for this frame
        track.RtpTimestamp += rtpStep;

        if (_negotiatedCodec == RemotePlayServer.Infrastructure.Encoding.VideoCodec.H265)
        {
            var fragments = RemotePlayServer.Infrastructure.Network.H265Fragmenter.FragmentAnnexB(au);
            if (frameNum < 3)
                Logger.Info($"[SIPSorcery] Track {track.Index} H265 frame #{frameNum}: {au.Length} bytes → {fragments.Count} RTP packets, ts={track.RtpTimestamp}, seq={track.SequenceNumber + 1}");
            for (int i = 0; i < fragments.Count; i++)
            {
                bool isLast = (i == fragments.Count - 1);
                SendRtpPacket(track, fragments[i], track.RtpTimestamp, isLast ? 1 : 0, frameNum);
            }
        }
        else if (_negotiatedCodec == RemotePlayServer.Infrastructure.Encoding.VideoCodec.H264)
        {
            var fragments = RemotePlayServer.Infrastructure.Network.H264Fragmenter.FragmentAnnexB(au);
            if (frameNum < 3)
                Logger.Info($"[SIPSorcery] Track {track.Index} H264 frame #{frameNum}: {au.Length} bytes → {fragments.Count} RTP packets, ts={track.RtpTimestamp}, seq={track.SequenceNumber + 1}");
            for (int i = 0; i < fragments.Count; i++)
            {
                bool isLast = (i == fragments.Count - 1);
                SendRtpPacket(track, fragments[i], track.RtpTimestamp, isLast ? 1 : 0, frameNum);
            }
        }
        else
        {
            SendRtpPacket(track, au, track.RtpTimestamp, 1, frameNum);
        }

        Interlocked.Increment(ref track.SentFrames);
    }

    private void SendRtpPacket(TrackInfo track, byte[] payload, uint rtpTimestamp, int markerBit, long frameNum)
    {
        if (_pc == null) return;

        // Increment sequence number for each RTP packet
        track.SequenceNumber++;

        // Session startup diagnostic: log details for the first packet of each session
        if (frameNum == 0 && !track.IsSessionStarted)
        {
            track.IsSessionStarted = true;
            Logger.Info($"[SIPSorcery] Track {track.Index} SESSION START: SSRC={track.Ssrc}, First Seq={track.SequenceNumber}, First TS={rtpTimestamp}");
        }

        // 1. Preferred: Multi-track send using MediaStream.SendRtpRaw (supports manual seqNum)
        if (_pc.VideoStreamList != null && track.Index < _pc.VideoStreamList.Count)
        {
            var videoStream = _pc.VideoStreamList[track.Index];
            // Use manual sequence number whenever possible
            if (videoStream != null)
            {
                videoStream.SendRtpRaw(payload, rtpTimestamp, markerBit, track.PayloadType, track.SequenceNumber);
                return;
            }
        }

        // 2. Legacy/Fallback: Single-track send using RTPSession.SendRtpRaw (manages seqNum automatically)
        // Warning: This may cause sequence jump/collision if fallback occurs mid-stream
        if (track.Index == 0)
        {
            try
            {
                _pc.SendRtpRaw(SDPMediaTypesEnum.video, payload, rtpTimestamp, markerBit, track.PayloadType);
            }
            catch (Exception ex)
            {
                if (frameNum % 100 == 0)
                    Logger.Warn($"[SIPSorcery] Track 0 fallback send failed: {ex.Message}");
            }
        }
    }

    public void FlushAllPendingFrames()
    {
        if (!_running || _pc == null || !_connected) return;

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
                long currentSent = Interlocked.Read(ref track.SentFrames);
                SendFrameImmediate(track, pending.Au, pending.RtpStep, currentSent);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                    Logger.Error($"[SIPSorcery] Track {i} flush error: {ex.Message}");
            }
        }
    }
}
