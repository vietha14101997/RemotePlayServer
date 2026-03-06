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
using VideoCodec = RemotePlayServer.Core.VideoCodec;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    // H265 DataChannel flow control
    // SCTP message buffer can overflow when pushing 120+ msgs/sec through a single DC.
    // Graduated dropping: start early to prevent congestion collapse.
    private const ulong DC_BUFFER_LOW_WATER  =  64_000;  //  64KB — normal, all frames pass
    private const ulong DC_BUFFER_MED_WATER  = 128_000;  // 128KB — drop 50% of P-frames
    private const ulong DC_BUFFER_HIGH_WATER = 256_000;  // 256KB — drop all P-frames
    private const ulong DC_BUFFER_CRITICAL   = 512_000;  // 512KB — drop everything except periodic IDR
    private long _dcDroppedPFrames;  // counter for diagnostics
    private long _dcDroppedTotal;
    private long _dcPFrameSeq;       // monotonic P-frame counter for 50% drop logic
    private bool _dcWasAboveHigh;    // track transition from HIGH→LOW for IDR resync

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

                    SendH265IdrViaDataChannel(track, nalData);
                    track.IdrViaDcCount++;
                    if (track.IdrViaDcCount <= 5 || track.IdrViaDcCount % 20 == 0)
                    {
                        Logger.Info($"[SIPSorcery] Track {track.Index}: IDR via DataChannel #{track.IdrViaDcCount}");
                    }

                    // IDR already sent via DC — skip RTP send for keyframes to avoid
                    // double-sending large frames. P-frames fall through to RTP below.
                    Interlocked.Increment(ref track.SentFrames);
                    return;
                }
            }
            // H265 P-frames: Send via DataChannel (type 0x04).
            // The Encoded Transform API delivers broken FU fragments for H265, not reassembled NALs.
            // RTP path via SendRtpRaw never triggers client's Encoded Transform callback.
            // DataChannel with flow control (drop when buffer high) is the only reliable path.
            if (_negotiatedCodec == VideoCodec.H265 && !isKeyframe)
            {
                if (IsH265DataChannelReady())
                {
                    SendH265PFrameViaDataChannel(track, nalData);
                    Interlocked.Increment(ref track.SentFrames);
                    return;
                }
                // DC not ready — skip this P-frame, it's undecodable without RTP path anyway
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
    /// Check if the DataChannel used for H265 frame delivery is open and ready.
    /// On reconnect, there's a race between encoder producing the first keyframe
    /// and the client-created DataChannel completing SCTP negotiation.
    /// </summary>
    private bool IsH265DataChannelReady()
    {
        var dc = _cursorDc;
        return dc?.readyState == SIPSorcery.Net.RTCDataChannelState.open;
    }

    /// <summary>
    /// Extract VPS/SPS/PPS from H265 keyframe and send via reliable DataChannel.
    /// Message format: [type=0x02][trackIndex(1)][annexB VPS+SPS+PPS bytes...]
    /// </summary>
    private void SendH265ParamSetsViaDataChannel(TrackInfo track, byte[] keyframeData)
    {
        var dc = _cursorDc;
        if (dc?.readyState != SIPSorcery.Net.RTCDataChannelState.open) return;

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
    private void SendH265IdrViaDataChannel(TrackInfo track, byte[] keyframeData)
    {
        var dc = _cursorDc;
        if (dc?.readyState != SIPSorcery.Net.RTCDataChannelState.open) return;

        // Flow control: Only drop IDR at critical buffer level.
        // IDRs are essential for decoder sync, so use higher threshold.
        ulong buffered = dc.bufferedAmount;
        if (buffered > DC_BUFFER_CRITICAL)
        {
            Logger.Warn($"[SIPSorcery] Track {track.Index}: DC buffer CRITICAL ({buffered/1024}KB), deferring IDR");
            track.ForceNextKeyframe = true;
            return;
        }

        try
        {
            // Extract IDR NAL data (skip VPS/SPS/PPS — those are sent separately as type=0x02)
            var idrData = ExtractH265IdrData(keyframeData);
            if (idrData == null || idrData.Length == 0)
            {
                Logger.Warn($"[SIPSorcery] Track {track.Index}: No IDR NAL found in keyframe ({keyframeData.Length} bytes)");
                return;
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
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 IDR: {ex.Message}");
        }
    }

    /// <summary>
    /// Send H265 P-frame data via reliable DataChannel.
    /// The Encoded Transform API delivers broken FU fragments for H265 — not reassembled NALs.
    /// All H265 frames (IDR + P) must go through DataChannel for reliable delivery.
    ///
    /// Message format: [type=0x04][trackIndex(1)][chunkIndex(1)][totalChunks(1)][P-frame Annex-B data...]
    /// Same chunking as IDR (type=0x03) but with type=0x04 to distinguish.
    /// </summary>
    private void SendH265PFrameViaDataChannel(TrackInfo track, byte[] nalData)
    {
        var dc = _cursorDc;
        if (dc?.readyState != SIPSorcery.Net.RTCDataChannelState.open) return;

        // Graduated flow control: Drop P-frames progressively as SCTP buffer grows.
        // This prevents congestion collapse where the reliable DC queues up
        // hundreds of frames, causing multi-second delivery stalls.
        ulong buffered = dc.bufferedAmount;
        long pSeq = Interlocked.Increment(ref _dcPFrameSeq);

        if (buffered > DC_BUFFER_HIGH_WATER)
        {
            // HIGH: Drop all P-frames, force IDR for resync
            _dcWasAboveHigh = true;
            long dropped = Interlocked.Increment(ref _dcDroppedPFrames);
            Interlocked.Increment(ref _dcDroppedTotal);
            if (dropped % 60 == 1)
                Logger.Warn($"[SIPSorcery] Track {track.Index}: DC buffer HIGH ({buffered/1024}KB), dropping P-frame (total dropped: {dropped})");
            track.ForceNextKeyframe = true;
            return;
        }

        if (buffered > DC_BUFFER_MED_WATER)
        {
            // MEDIUM: Drop 50% of P-frames to slow the bleed
            if (pSeq % 2 == 0)
            {
                Interlocked.Increment(ref _dcDroppedPFrames);
                Interlocked.Increment(ref _dcDroppedTotal);
                return;
            }
        }

        // Buffer drained after being high → force IDR for decoder resync
        if (_dcWasAboveHigh && buffered < DC_BUFFER_LOW_WATER)
        {
            _dcWasAboveHigh = false;
            track.ForceNextKeyframe = true;
            Logger.Info($"[SIPSorcery] Track {track.Index}: DC buffer drained ({buffered/1024}KB), forcing IDR for resync");
        }

        try
        {
            // SCTP message size limit — chunk at 60KB to be safe
            const int MAX_CHUNK = 60_000;
            int totalChunks = (nalData.Length + MAX_CHUNK - 1) / MAX_CHUNK;
            if (totalChunks > 255)
            {
                Logger.Error($"[SIPSorcery] Track {track.Index}: P-frame too large for DC chunking ({nalData.Length} bytes, {totalChunks} chunks needed). Dropping.");
                return;
            }

            for (int chunk = 0; chunk < totalChunks; chunk++)
            {
                int offset = chunk * MAX_CHUNK;
                int len = Math.Min(MAX_CHUNK, nalData.Length - offset);

                // Header: [type=0x04][trackIndex][chunkIndex][totalChunks]
                var msg = new byte[4 + len];
                msg[0] = 0x04; // message type: h265_pframe_data
                msg[1] = (byte)track.Index;
                msg[2] = (byte)chunk;
                msg[3] = (byte)totalChunks;
                Buffer.BlockCopy(nalData, offset, msg, 4, len);

                dc.send(msg);
            }

            long sentFrames = Interlocked.Read(ref track.SentFrames);
            if (sentFrames < 10 || sentFrames % 300 == 0)
                Logger.Info($"[SIPSorcery] Track {track.Index}: P-frame via DataChannel ({nalData.Length} bytes, {totalChunks} chunks)");
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref track.SentFrames) % 120 == 0)
                Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 P-frame via DC: {ex.Message}");
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
