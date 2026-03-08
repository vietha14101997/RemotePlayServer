#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using SIPSorcery.Net;
using SIPSorcery.Media;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Infrastructure.Network;
using VideoCodec = RemotePlayServer.Core.VideoCodec;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    // H265 DataChannel flow control — ALL H265 frames (IDR + P) use DC.
    // Unity WebRTC Encoded Transform never fires for H.265 RTP packets.
    // With per-track DCs, each track has its own SCTP buffer → no cross-track congestion.
    private const ulong DC_BUFFER_LOW_WATER  = 256_000;  // 256KB — buffer drained
    private const ulong DC_BUFFER_HIGH_WATER = 1_048_576;  // 1MB — must accommodate periodic GOP IDR frames
    private bool _dcWasAboveHigh;    // legacy single-DC: track transition from HIGH→LOW for IDR resync
    private volatile bool _congestionBitrateReduced; // true while bitrate is temporarily reduced due to DC congestion
    // Soft congestion: proactive bitrate reduction when DC buffer stays elevated (before HIGH_WATER)
    private volatile bool _dcSoftCongestion;
    private long _dcSoftCongestionEntryTicks; // Sustained entry: when buffer first exceeded threshold (0 = not started)
    private long _dcSoftCongestionClearTicks; // Hold timer: when buffer first went below threshold (0 = not started)
    // Staggered IDR after drain: global cooldown ensures only 1 track gets IDR at a time
    private long _lastDrainIdrTicks;

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

        // H265 hybrid mode: Only need DC for first IDR bootstrap.
        // After that, P-frames go via RTP — no need to block on DC.
        if (_negotiatedCodec == VideoCodec.H265 && !IsH265DataChannelReady()
            && Interlocked.Read(ref track.SentFrames) == 0)
            return;

        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        // DC-aware bitrate reduction for H265/DataChannel:
        // Instead of skipping frames (causes visual discontinuity), reduce encoder bitrate
        // when SCTP buffer grows. This keeps all frames but makes them SMALLER.
        // Result: smooth motion at lower quality during high-motion, low latency maintained.
        //
        // KEY DESIGN: Sync with AdaptiveBitrateController via ForceTarget() instead of
        // manual restore. This prevents oscillation (old: 12000→6000→12000→6000 every few seconds).
        // ABC naturally recovers bitrate over 5-10 seconds, finding the sustainable ceiling.
        if (_negotiatedCodec == VideoCodec.H265 && Interlocked.Read(ref track.EncodedFrames) > 0)
        {
            // Use TOTAL buffered across all track DCs for congestion decision.
            // Per-track DCs share the same SCTP association, so total pressure matters.
            // Without this, Track 1 (buffer=0KB) would clear congestion while Track 0 is still full.
            ulong totalBuffered = 0;
            lock (_lock)
            {
                foreach (var t in _tracks)
                {
                    var tdc = GetH265VideoChannel(t.Index);
                    if (tdc != null) totalBuffered += tdc.bufferedAmount;
                }
            }

            // Soft congestion: total buffer sustained above threshold → reduce bitrate.
            // SUSTAINED CHECK: Buffer must stay above 500KB for 200ms to trigger.
            // This prevents transient buffer spikes (1-2 frames worth) from causing
            // unnecessary bitrate cuts. At 8Mbps×2tracks, a single above-average frame
            // pair can briefly hit 300-500KB but drains instantly — not real congestion.
            long now = Environment.TickCount64;
            if (totalBuffered > 500_000 && !_dcSoftCongestion)
            {
                if (_dcSoftCongestionEntryTicks == 0)
                {
                    _dcSoftCongestionEntryTicks = now; // Start sustained entry timer
                }
                else if (now - _dcSoftCongestionEntryTicks > 200) // 200ms sustained
                {
                    _dcSoftCongestion = true;
                    _dcSoftCongestionEntryTicks = 0;
                    _dcSoftCongestionClearTicks = 0;
                    int currentBitrate = _bitrateController.TargetBitrateKbps;
                    int reduced = Math.Max(_bitrateController.MinBitrateKbps, currentBitrate * 80 / 100); // -20%
                    if (reduced < currentBitrate)
                    {
                        // Tell ABC to remember this congestion point — caps recovery at 90% of trigger
                        _bitrateController.MarkDcCongestion(currentBitrate);
                        _bitrateController.ForceTarget(reduced);
                        ApplyBitrateToAllEncoders(reduced);
                        Logger.Info($"[SIPSorcery] DC soft congestion ({totalBuffered/1024}KB total, sustained) → bitrate {currentBitrate} → {reduced}kbps (-20%, ceiling set)");
                    }
                }
            }
            else if (!_dcSoftCongestion && totalBuffered <= 500_000)
            {
                _dcSoftCongestionEntryTicks = 0; // Buffer dropped before sustained — reset entry timer
            }
            // Recovery: total buffer must stay below 100KB for 500ms before clearing.
            else if (_dcSoftCongestion && totalBuffered < 100_000)
            {
                if (_dcSoftCongestionClearTicks == 0)
                {
                    _dcSoftCongestionClearTicks = now;
                }
                else if (now - _dcSoftCongestionClearTicks > 500) // 500ms hold
                {
                    _dcSoftCongestion = false;
                    _dcSoftCongestionClearTicks = 0;
                    // Reset ABC recovery timer from NOW (not from congestion start).
                    // Without this, ABC starts recovering immediately because _lastNetworkIssueTime
                    // was set when congestion started, and 3-5s has already elapsed during congestion.
                    _bitrateController.MarkCongestionCleared();
                    Logger.Info($"[SIPSorcery] DC soft congestion cleared ({totalBuffered/1024}KB total) → ABC will recover after cooldown");
                }
            }
            else if (_dcSoftCongestion && totalBuffered >= 100_000)
            {
                _dcSoftCongestionClearTicks = 0; // Buffer went back up, reset hold timer
            }
        }

        lock (track.EncodeLock)
        {
            try
            {
                // Ensure encoder matches incoming texture size
                EnsureEncoderMatchesResolution(track, width, height);
                if (track.Encoder == null) return;

                track.PendingFrame = null;
                track.PendingCaptureTimestampMs = captureTimestampMs;

                if (track.KeyframeStaggerCountdown > 0)
                {
                    track.KeyframeStaggerCountdown--;
                    if (track.KeyframeStaggerCountdown == 0)
                        track.ForceNextKeyframe = true;
                }

                long frameNum = Interlocked.Read(ref track.EncodedFrames);
                // Only force first frame as IDR to avoid flooding DataChannel with large keyframes.
                // NVENC has infinite GOP, so P-frames follow naturally after the first IDR.
                bool forceIdr = frameNum < 1 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
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

        // H265 hybrid mode: Only need DC for first IDR bootstrap.
        // After that, P-frames go via RTP — no need to block on DC.
        if (_negotiatedCodec == VideoCodec.H265 && !IsH265DataChannelReady()
            && Interlocked.Read(ref track.SentFrames) == 0)
            return;

        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        lock (track.EncodeLock)
        {
            try
            {
                var device = track.Device ?? _sharedDevice;
                if (device == null) return;

                // Ensure encoder matches incoming texture size
                EnsureEncoderMatchesResolution(track, width, height);
                if (track.Encoder == null) return;

                track.PendingFrame = null;
                track.PendingCaptureTimestampMs = captureTimestampMs;

                if (track.StagingNV12 != null && (track.StagingNV12.Description.Width != width || track.StagingNV12.Description.Height != height))
                {
                    track.StagingNV12.Dispose();
                    track.StagingNV12 = null;
                }

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
                // Only force first frame as IDR to avoid flooding DataChannel with large keyframes.
                bool forceIdr = frameNum < 1 || track.ForceNextKeyframe || track.KeyframeBurstRemaining > 0;
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
            {
                Logger.Info($"[SIPSorcery] Track {track.Index} OnEncodedData: {nalData.Length} bytes, keyframe={isKeyframe}, annexB={ContainsAnnexBStartCode(nalData)}");
                // Dump NAL types for debugging H265 client issue
                if (nalData.Length >= 4)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[SIPSorcery] Track {track.Index} NAL dump (key={isKeyframe}): ");
                    int hexLen = Math.Min(nalData.Length, 32);
                    for (int h = 0; h < hexLen; h++)
                        sb.Append(nalData[h].ToString("X2")).Append(' ');
                    sb.Append(" | NAL types: ");
                    // Parse Annex-B NAL types
                    for (int p = 0; p < nalData.Length - 4; p++)
                    {
                        int sc = 0;
                        if (p + 3 < nalData.Length && nalData[p] == 0 && nalData[p+1] == 0 && nalData[p+2] == 0 && nalData[p+3] == 1) sc = 4;
                        else if (p + 2 < nalData.Length && nalData[p] == 0 && nalData[p+1] == 0 && nalData[p+2] == 1) sc = 3;
                        if (sc > 0 && p + sc < nalData.Length)
                        {
                            int nalType = (nalData[p + sc] >> 1) & 0x3F;
                            string desc = nalType switch { 32 => "VPS", 33 => "SPS", 34 => "PPS", 19 => "IDR_W_RADL", 20 => "IDR_N_LP", 21 => "CRA", _ => nalType <= 9 ? $"TRAIL({nalType})" : $"T{nalType}" };
                            sb.Append($"{nalType}({desc}) ");
                            p += sc;
                        }
                    }
                    Logger.Info(sb.ToString());
                }
            }

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
                if (_negotiatedCodec == VideoCodec.H265)
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

                // H265 HYBRID MODE: Send IDR via reliable DataChannel for decoder bootstrap.
                // P-frames go via standard RTP (handled below in SendFrameImmediate).
                if (_negotiatedCodec == VideoCodec.H265)
                {
                    // If DataChannel isn't open yet (race on reconnect), skip this keyframe
                    // and force the encoder to produce another one.
                    if (!IsH265DataChannelReady())
                    {
                        long dcNotReadyCount = Interlocked.Increment(ref track.DcNotReadyCount);
                        if (dcNotReadyCount <= 3 || dcNotReadyCount % 120 == 0)
                            Logger.Warn($"[SIPSorcery] Track {track.Index}: DataChannel not ready, deferring IDR #{dcNotReadyCount} (will force next keyframe)");
                        track.ForceNextKeyframe = true;
                        Interlocked.Exchange(ref track.SentFrames, 0);
                        return;
                    }

                    // Send codec config on first IDR after session start/reconnect.
                    if (track.IdrViaDcCount == 0)
                    {
                        SendH265ParamSetsViaDataChannel(track, nalData);
                    }

                    if (SendH265IdrViaDataChannel(track, nalData))
                    {
                        track.IdrViaDcCount++;
                        if (track.IdrViaDcCount <= 5 || track.IdrViaDcCount % 20 == 0)
                            Logger.Info($"[SIPSorcery] Track {track.Index}: IDR via DataChannel #{track.IdrViaDcCount}");

                        // NOTE: Bootstrap IDR retry DISABLED.
                        // Original intent: retry IDR after 500ms in case SCTP dropped it during slow-start.
                        // Reality: 1080p H265 IDR = 200-450KB (not 60-100KB as assumed). SCTP delivers it
                        // reliably (just slowly during cwnd ramp-up). The retry IDR dumps another 200-450KB
                        // into the buffer → instant DC congestion → P-frame drops → prediction chain break
                        // → text smearing artifacts on initial connection.

                        // IDR sent via DC — P-frames also go via DC below.
                        Interlocked.Increment(ref track.SentFrames);
                        return;
                    }
                    // IDR deferred/failed — will retry on next keyframe
                    return;
                }
            }
            // H265 P-frames: Send via DataChannel (same as IDR).
            // Unity WebRTC's Encoded Transform NEVER fires for H.265 RTP packets,
            // so ALL H.265 frames must go through DataChannel for reliable delivery.
            if (_negotiatedCodec == VideoCodec.H265)
            {
                if (SendH265PFrameViaDataChannel(track, nalData))
                {
                    Interlocked.Increment(ref track.SentFrames);
                    return;
                }
                // P-frame send failed (DC not ready or congested) — drop it.
                // Next IDR will resync the decoder.
                return;
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

        if (_negotiatedCodec == VideoCodec.H265)
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
        else if (_negotiatedCodec == VideoCodec.H264)
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

    /// <summary>
    /// Check if a DataChannel for H265 frame delivery is open and ready.
    /// Checks per-track DC first, then legacy single DC, then cursor DC fallback.
    /// </summary>
    private bool IsH265DataChannelReady()
    {
        // Per-track DCs: if any exist, at least one should be open
        lock (_h265VideoDcs)
        {
            if (_h265VideoDcs.Count > 0)
                return _h265VideoDcs.Values.Any(dc => dc.readyState == RTCDataChannelState.open);
        }
        // Legacy single DC
        var legacyDc = _h265VideoDcLegacy;
        if (legacyDc?.readyState == RTCDataChannelState.open) return true;
        // Cursor DC fallback
        var cursorDc = _cursorDc;
        return cursorDc?.readyState == RTCDataChannelState.open;
    }

    /// <summary>
    /// Get the DataChannel for a specific track's H265 video frames.
    /// Priority: per-track DC → legacy single DC → cursor DC fallback.
    /// Per-track DCs give each track its own SCTP buffer, preventing cross-track congestion.
    /// </summary>
    private RTCDataChannel? GetH265VideoChannel(int trackIndex)
    {
        // 1. Per-track DC (best: isolated buffer per track)
        lock (_h265VideoDcs)
        {
            if (_h265VideoDcs.TryGetValue(trackIndex, out var perTrackDc) &&
                perTrackDc.readyState == RTCDataChannelState.open)
                return perTrackDc;
        }
        // 2. Legacy single DC
        var legacyDc = _h265VideoDcLegacy;
        if (legacyDc?.readyState == RTCDataChannelState.open) return legacyDc;
        // 3. Cursor DC fallback (reliable, ordered)
        var cursorDc = _cursorDc;
        return cursorDc?.readyState == RTCDataChannelState.open ? cursorDc : null;
    }

    /// <summary>
    /// Extract VPS/SPS/PPS from H265 keyframe and send via reliable DataChannel.
    /// Message format: [type=0x02][trackIndex(1)][annexB VPS+SPS+PPS bytes...]
    /// </summary>
    private void SendH265ParamSetsViaDataChannel(TrackInfo track, byte[] keyframeData)
    {
        var dc = GetH265VideoChannel(track.Index);
        if (dc == null) return;

        try
        {
            // Extract VPS, SPS, PPS NAL units from the Annex-B keyframe
            var paramSets = ExtractH265ParamSets(keyframeData);
            
            // Update cache/last seen if found
            if (paramSets != null && paramSets.Length > 0)
            {
                track.LastH265ParamSets = paramSets;
            }
            else
            {
                // Keyframe doesn't contain param sets (common on reconnects), use cached
                paramSets = track.LastH265ParamSets;
            }

            if (paramSets == null || paramSets.Length == 0)
            {
                // Only log if we've sent at least one frame, to avoid spam during startup
                if (Interlocked.Read(ref track.SentFrames) > 0)
                    Logger.Warn($"[SIPSorcery] Track {track.Index}: No VPS/SPS/PPS found in keyframe AND no cache available");
                return;
            }

            // Build message: [type=0x02][trackIndex][paramSets Annex-B bytes]
            var msg = new byte[2 + paramSets.Length];
            msg[0] = 0x02; // message type: h265_codec_config
            msg[1] = (byte)track.Index;
            Buffer.BlockCopy(paramSets, 0, msg, 2, paramSets.Length);

            dc.send(msg);
            
            // Log once per session or on change
            if (track.IdrViaDcCount == 0)
                Logger.Info($"[SIPSorcery] Track {track.Index}: Sent H265 codec config via DataChannel ({paramSets.Length} bytes)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 config: {ex.Message}");
        }
    }

    /// <summary>
    /// Send IDR keyframe data via reliable DataChannel.
    /// The Encoded Transform API on the client NEVER delivers large IDR frames
    /// because they're fragmented into 30-150+ RTP FU packets that get lost.
    /// DataChannel (SCTP) delivers reliably.
    /// 
    /// Message format: [type=0x03][trackIndex(1)][chunkIndex(1)][totalChunks(1)][IDR Annex-B data...]
    /// For single-chunk: chunkIndex=0, totalChunks=1
    /// For multi-chunk: client reassembles all chunks before feeding to decoder.
    /// </summary>
    /// <returns>true if IDR was sent, false if deferred or failed</returns>
    private bool SendH265IdrViaDataChannel(TrackInfo track, byte[] keyframeData)
    {
        var dc = GetH265VideoChannel(track.Index);
        if (dc == null) return false;

        ulong buffered = dc.bufferedAmount;
        bool perTrackMode = IsPerTrackDcMode();
        // Per-track drain detection: this track's DC was congested, now drained.
        // Queue IDR to repair prediction chain, BUT only if:
        // 1. Buffer is draining (< DC_BUFFER_LOW_WATER) — SCTP is catching up
        // 2. No other track is also queuing IDR (stagger to avoid 2× IDR = death spiral)
        // 3. Cooldown: at least 2s since last congestion IDR on this track
        if (track.PFramesDroppedDuringCongestion && buffered < DC_BUFFER_LOW_WATER)
        {
            track.PFramesDroppedDuringCongestion = false;
            _congestionBitrateReduced = false;
            long now = Environment.TickCount64;
            bool bufferSafe = buffered < DC_BUFFER_LOW_WATER; // ~256KB — drain detection already confirmed buffer is dropping
            bool cooldownOk = (now - Interlocked.Read(ref _lastDrainIdrTicks)) > 2000;
            if (bufferSafe && cooldownOk)
            {
                Interlocked.Exchange(ref _lastDrainIdrTicks, now);
                track.ForceNextKeyframe = true;
                Logger.Info($"[SIPSorcery] Track {track.Index}: DC drained ({buffered/1024}KB) — queued staggered IDR (prediction chain repair)");
            }
            else
            {
                Logger.Info($"[SIPSorcery] Track {track.Index}: DC drained ({buffered/1024}KB) — skipping IDR (buf={bufferSafe}, cd={cooldownOk})");
            }
        }
        // Legacy single-DC drain detection (only in legacy mode)
        if (!perTrackMode && _dcWasAboveHigh && buffered < DC_BUFFER_LOW_WATER)
        {
            _dcWasAboveHigh = false;
            _congestionBitrateReduced = false;
            Logger.Info($"[SIPSorcery] DC buffer drained ({buffered/1024}KB) — resuming (no IDR)");
        }

        // Flow control: Block IDR when buffer is already congested.
        // Sending a large IDR into a full buffer makes congestion WORSE.
        if (buffered > DC_BUFFER_HIGH_WATER)
        {
            Logger.Warn($"[SIPSorcery] Track {track.Index}: DC buffer HIGH ({buffered/1024}KB), deferring IDR");
            track.PFramesDroppedDuringCongestion = true; // IDR deferred = track also needs resync
            if (!perTrackMode) _dcWasAboveHigh = true;
            return false;
        }

        try
        {
            // Extract IDR NAL data (skip VPS/SPS/PPS — those are sent separately as type=0x02)
            var idrData = ExtractH265IdrData(keyframeData);
            if (idrData == null || idrData.Length == 0)
            {
                Logger.Warn($"[SIPSorcery] Track {track.Index}: No IDR NAL found in keyframe ({keyframeData.Length} bytes)");
                return false;
            }

            // SCTP message size limit is typically ~256KB, but some implementations
            // have lower limits. Chunk at 60KB to be safe and avoid blocking.
            const int MAX_CHUNK = 60_000;
            int totalChunks = (idrData.Length + MAX_CHUNK - 1) / MAX_CHUNK;
            if (totalChunks > 255) totalChunks = 255; // Protocol limit (1 byte)

            for (int chunk = 0; chunk < totalChunks; chunk++)
            {
                int offset = chunk * MAX_CHUNK;
                int len = Math.Min(MAX_CHUNK, idrData.Length - offset);

                // Header: [type=0x03][trackIndex][chunkIndex][totalChunks]
                var msg = new byte[4 + len];
                msg[0] = 0x03; // message type: h265_idr_data
                msg[1] = (byte)track.Index;
                msg[2] = (byte)chunk;
                msg[3] = (byte)totalChunks;
                Buffer.BlockCopy(idrData, offset, msg, 4, len);

                dc.send(msg);
            }

            Logger.Info($"[SIPSorcery] Track {track.Index}: Sent H265 IDR via DataChannel ({idrData.Length} bytes, {totalChunks} chunks)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 IDR: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send H265 P-frame data via reliable DataChannel.
    /// The Encoded Transform API in Unity WebRTC NEVER fires for H.265 RTP packets,
    /// so P-frames must also go through DataChannel (like IDR keyframes).
    ///
    /// Message format: [type=0x04][trackIndex(1)][chunkIndex(1)][totalChunks(1)][P-frame Annex-B data...]
    /// Most P-frames fit in a single chunk (typically 1-35KB).
    /// </summary>
    /// <returns>true if P-frame was sent, false if deferred or failed</returns>
    private bool SendH265PFrameViaDataChannel(TrackInfo track, byte[] pframeData)
    {
        var dc = GetH265VideoChannel(track.Index);
        if (dc == null) return false;

        ulong buffered = dc.bufferedAmount;

        bool perTrackMode = IsPerTrackDcMode();
        long now = Environment.TickCount64;
        // Flow control: Skip P-frame when buffer is congested.
        // Unlike IDR, dropping a P-frame is acceptable — next IDR will resync.
        if (buffered > DC_BUFFER_HIGH_WATER)
        {
            track.PFramesDroppedDuringCongestion = true;
            if (!perTrackMode) _dcWasAboveHigh = true;
            // Temporarily reduce bitrate so the next IDR frame is smaller and doesn't
            // immediately re-fill the SCTP buffer (prevents IDR storm cycle).
            if (!_congestionBitrateReduced)
            {
                _congestionBitrateReduced = true;
                int currentBitrate = _bitrateController.TargetBitrateKbps;
                int reducedBitrate = Math.Max(_bitrateController.MinBitrateKbps, currentBitrate * 80 / 100); // -20%
                if (reducedBitrate < currentBitrate)
                {
                    _bitrateController.MarkDcCongestion(currentBitrate);
                    _bitrateController.ForceTarget(reducedBitrate);
                    ApplyBitrateToAllEncoders(reducedBitrate);
                    Logger.Info($"[SIPSorcery] DC congestion → bitrate reduced {currentBitrate} → {reducedBitrate}kbps (-20%, ceiling set)");
                }
            }
            if (Interlocked.Read(ref track.SentFrames) % 60 == 0)
                Logger.Warn($"[SIPSorcery] Track {track.Index}: DC buffer HIGH ({buffered/1024}KB), dropping P-frame");
            return false;
        }

        // Per-track drain detection: this track's DC was congested, now drained.
        // Queue staggered IDR: only 1 track at a time, buffer must be very low, 2s cooldown.
        if (track.PFramesDroppedDuringCongestion && buffered < DC_BUFFER_LOW_WATER)
        {
            track.PFramesDroppedDuringCongestion = false;
            _congestionBitrateReduced = false;
            bool bufferSafe = buffered < DC_BUFFER_LOW_WATER; // ~256KB — drain detection already confirmed buffer is dropping
            bool cooldownOk = (now - Interlocked.Read(ref _lastDrainIdrTicks)) > 2000;
            if (bufferSafe && cooldownOk)
            {
                Interlocked.Exchange(ref _lastDrainIdrTicks, now);
                track.ForceNextKeyframe = true;
                Logger.Info($"[SIPSorcery] Track {track.Index}: DC drained ({buffered/1024}KB) — queued staggered IDR (prediction chain repair)");
            }
            else
            {
                Logger.Info($"[SIPSorcery] Track {track.Index}: DC drained ({buffered/1024}KB) — skipping IDR (buf={bufferSafe}, cd={cooldownOk})");
            }
        }
        // Legacy single-DC drain detection (only in legacy mode)
        if (!perTrackMode && _dcWasAboveHigh && buffered < DC_BUFFER_LOW_WATER)
        {
            _dcWasAboveHigh = false;
            _congestionBitrateReduced = false;
            Logger.Info($"[SIPSorcery] DC buffer drained ({buffered/1024}KB) — resuming (no IDR)");
        }

        try
        {
            const int MAX_CHUNK = 60_000;
            int totalChunks = (pframeData.Length + MAX_CHUNK - 1) / MAX_CHUNK;
            if (totalChunks > 255) totalChunks = 255;

            for (int chunk = 0; chunk < totalChunks; chunk++)
            {
                int offset = chunk * MAX_CHUNK;
                int len = Math.Min(MAX_CHUNK, pframeData.Length - offset);

                // Header: [type=0x04][trackIndex][chunkIndex][totalChunks]
                var msg = new byte[4 + len];
                msg[0] = 0x04; // message type: h265_pframe_data
                msg[1] = (byte)track.Index;
                msg[2] = (byte)chunk;
                msg[3] = (byte)totalChunks;
                Buffer.BlockCopy(pframeData, offset, msg, 4, len);

                dc.send(msg);
            }

            if (Interlocked.Read(ref track.SentFrames) < 5 || Interlocked.Read(ref track.SentFrames) % 300 == 0)
                Logger.Info($"[SIPSorcery] Track {track.Index}: Sent H265 P-frame via DataChannel ({pframeData.Length} bytes, {totalChunks} chunks)");

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 P-frame: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply bitrate to all track encoders. Used for congestion-based bitrate reduction.
    /// </summary>
    private void ApplyBitrateToAllEncoders(int bitrateKbps)
    {
        TrackInfo[] snapshot;
        lock (_lock) { snapshot = _tracks.ToArray(); }
        foreach (var t in snapshot)
        {
            try
            {
                lock (t.EncodeLock) { t.Encoder?.SetBitrate(bitrateKbps); }
            }
            catch (Exception ex)
            {
                // AMF SetBitrate can throw SEH exception intermittently.
                // Don't let one track's failure prevent other tracks from being updated.
                Logger.Error($"[SIPSorcery] Track {t.Index} ApplyBitrate({bitrateKbps}) failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Check if per-track DataChannels are active (vs legacy single DC).
    /// When per-track DCs are active, we must NOT use the global _dcWasAboveHigh flag
    /// because it causes cross-track contamination (Track 1 congestion forces Track 0 IDR).
    /// </summary>
    private bool IsPerTrackDcMode()
    {
        lock (_h265VideoDcs) { return _h265VideoDcs.Count > 0; }
    }

    /// <summary>
    /// After DC buffer drains from congestion, force IDR for every track that had P-frames
    /// silently dropped. Only used in legacy single-DC mode.
    /// In per-track DC mode, each track handles its own drain detection independently.
    /// </summary>
    private void ForceIdrForDroppedTracks()
    {
        lock (_lock)
        {
            foreach (var t in _tracks)
            {
                if (t.PFramesDroppedDuringCongestion)
                {
                    t.PFramesDroppedDuringCongestion = false;
                    t.ForceNextKeyframe = true;
                    Logger.Info($"[SIPSorcery] Track {t.Index}: DC congestion cleared — forcing IDR resync (P-frames were dropped)");
                }
            }
        }
    }

    /// <summary>
    /// Extract VPS(32), SPS(33), PPS(34) NAL units from Annex-B bitstream.
    /// Returns concatenated Annex-B bytes containing only parameter set NALs.
    /// </summary>
    private static byte[] ExtractH265ParamSets(byte[] annexB)
    {
        using var result = new System.IO.MemoryStream();
        int i = 0;
        while (i < annexB.Length - 4)
        {
            // Find start code
            int scLen = 0;
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                scLen = 4;
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                scLen = 3;

            if (scLen == 0) { i++; continue; }

            int nalStart = i;
            int headerPos = i + scLen;
            if (headerPos >= annexB.Length) break;

            int nalType = (annexB[headerPos] >> 1) & 0x3F;

            // Find end of this NAL (next start code or end of data)
            int nalEnd = annexB.Length;
            for (int j = headerPos + 1; j < annexB.Length - 3; j++)
            {
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 0 && annexB[j + 3] == 1)
                { nalEnd = j; break; }
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 1)
                { nalEnd = j; break; }
            }

            // Keep only VPS(32), SPS(33), PPS(34)
            if (nalType >= 32 && nalType <= 34)
            {
                result.Write(annexB, nalStart, nalEnd - nalStart);
            }

            // Stop after we've passed the parameter sets (IDR starts at type 19/20)
            if (nalType <= 21 && nalType >= 16) break; // IRAP NAL, no more param sets

            i = nalEnd;
        }
        return result.ToArray();
    }

    /// <summary>
    /// Extract IDR NAL units (type 19, 20) from Annex-B bitstream.
    /// Returns concatenated Annex-B bytes containing only IDR slice NALs.
    /// </summary>
    private static byte[] ExtractH265IdrData(byte[] annexB)
    {
        using var result = new System.IO.MemoryStream();
        int i = 0;
        while (i < annexB.Length - 4)
        {
            int scLen = 0;
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                scLen = 4;
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                scLen = 3;

            if (scLen == 0) { i++; continue; }

            int nalStart = i;
            int headerPos = i + scLen;
            if (headerPos >= annexB.Length) break;

            int nalType = (annexB[headerPos] >> 1) & 0x3F;

            int nalEnd = annexB.Length;
            for (int j = headerPos + 1; j < annexB.Length - 3; j++)
            {
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 0 && annexB[j + 3] == 1)
                { nalEnd = j; break; }
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 1)
                { nalEnd = j; break; }
            }

            // Keep IDR_W_RADL(19), IDR_N_LP(20), and CRA(21)
            if (nalType == 19 || nalType == 20 || nalType == 21)
            {
                result.Write(annexB, nalStart, nalEnd - nalStart);
            }

            i = nalEnd;
        }
        return result.ToArray();
    }
}
