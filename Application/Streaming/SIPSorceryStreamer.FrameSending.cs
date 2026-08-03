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
    // Per-track DCs: each track has its own SCTP buffer → independent congestion detection.
    private const ulong DC_BUFFER_LOW_WATER  = 256_000;  // 256KB — buffer drained
    private const ulong DC_BUFFER_MID_WATER  = 768_000;  // 768KB — partially drained, allow IDR for resync
    private const ulong DC_BUFFER_HIGH_WATER = 2_097_152;  // 2MB — accommodate scroll/animation bursts on WiFi

    // Frame padding: pad small SCTP messages to MIN_FRAME_MSG_SIZE to avoid Nagle batching delay.
    // SCTP batches messages <~1KB waiting for MTU fill, adding 5-15ms latency to small P-frames.
    // Protocol v2 header: [type(1)][trackIdx(1)][chunkIdx(1)][totalChunks(1)][originalSize(2 BE)][data...][padding...]
    // Must exceed typical SCTP path MTU (~1200 IPv6, ~1400 IPv4) to force immediate flush.
    // At 1024 the message still fits in one MTU → Nagle can delay it waiting for ACK.
    private const int MIN_FRAME_MSG_SIZE = 1400;
    private const int PADDED_HEADER_SIZE = 6; // type + trackIdx + chunkIdx + totalChunks + originalSize(2)
    private bool _dcWasAboveHigh;    // legacy single-DC: track transition from HIGH→LOW for IDR resync
    // Per-track congestion state moved to TrackInfo (DcSoftCongestion, CongestionBitrateReduced, etc.)
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
    /// <param name="isSceneChange">
    /// Heuristic: large dirty area (>= 25% monitor). Forces IDR with 250ms throttle.
    /// </param>
    /// <param name="forceKeyframe">
    /// Discrete input event detected (Tab / Alt-Tab / click / Ctrl-key combo).
    /// Forces IDR with 400ms throttle. This is the PRIMARY signal that fixes the
    /// tab-switch bug — it fires on the very next frame after the input arrives,
    /// so the client never sees a P-frame referencing the pre-switch keyframe.
    /// </param>
    public void PushBgraTexture(int monitorIndex, ID3D11Texture2D bgraTexture, int width, int height, long captureTimestampMs = 0, bool isSceneChange = false, bool forceKeyframe = false)
    {
        if (!_running || _disposed || _isPaused) return;
        // Relay-media mode has no PeerConnection/DTLS — frames flow via OnRelayVideoFrame,
        // so the WebRTC-connected gate must not block the encode pipeline.
        if (!_connected && !RelayMediaMode) return;
        if (!_phase3Active) return;

        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        var track = _tracks[monitorIndex];
        // Per-track PC mode: video goes via dedicated video DC, not RTP MediaStreamTrack.
        // Relay-media mode needs no WebRTC track/DC — the WS relay carries the frames.
        bool trackReady = RelayMediaMode
            || (_perTrackPcMode ? (_videoDcs.ContainsKey(track.Index)) : (track.Track != null));
        if (!trackReady || track.Encoder == null) return;

        // H265 hybrid mode: Only need DC for first IDR bootstrap.
        // After that, P-frames go via RTP — no need to block on DC.
        // (Relay mode never opens a DC — skip the gate or no frame would ever pass.)
        if (!RelayMediaMode && _negotiatedCodec == VideoCodec.H265 && !IsH265DataChannelReady()
            && Interlocked.Read(ref track.SentFrames) == 0)
            return;

        if (monitorIndex < _monitorPaused.Length && _monitorPaused[monitorIndex]) return;

        if (captureTimestampMs > 0 && Interlocked.Read(ref _streamStartMs) < 0)
            Interlocked.CompareExchange(ref _streamStartMs, captureTimestampMs, -1);

        // PER-TRACK DC congestion detection for H265/DataChannel:
        // Each track monitors ONLY its own DataChannel buffer, not the total across all tracks.
        // This prevents Track 1 (video, heavy data) from triggering bitrate reduction on Track 0 (VSCode, light data).
        // Previously: totalBuffered = sum(all tracks) → Track 1 congestion reduced ALL encoders' bitrate.
        if (_negotiatedCodec == VideoCodec.H265 && Interlocked.Read(ref track.EncodedFrames) > 0)
        {
            // Per-track buffer: only check THIS track's DataChannel
            ulong trackBuffered = 0;
            var trackDc = GetH265VideoChannel(monitorIndex);
            if (trackDc != null) trackBuffered = trackDc.bufferedAmount;

            // Soft congestion: THIS track's buffer sustained above threshold → reduce THIS track's bitrate only.
            // Threshold lowered from 300KB→200KB to trigger earlier, before buffer spirals to 1MB.
            long now = Environment.TickCount64;
            // Soft congestion: 400KB sustained for 500ms. Previous 200KB/200ms was too sensitive
            // for scroll content where SCTP buffers naturally spike during scene changes on WiFi.
            if (trackBuffered > 400_000 && !track.DcSoftCongestion)
            {
                if (track.DcSoftCongestionEntryTicks == 0)
                {
                    track.DcSoftCongestionEntryTicks = now;
                }
                else if (now - track.DcSoftCongestionEntryTicks > 500) // 500ms sustained
                {
                    track.DcSoftCongestion = true;
                    track.DcSoftCongestionEntryTicks = 0;
                    track.DcSoftCongestionClearTicks = 0;
                    int currentBitrate = track.TrackBitrateKbps > 0 ? track.TrackBitrateKbps : _bitrateController.TargetBitrateKbps;
                    // Escalating reduction: first time -20%, second -40%, third+ -50%.
                    // Single -20% isn't enough for high-motion content (YouTube) that keeps
                    // generating data faster than SCTP can drain.
                    int reductionPercent = track.DcCongestionEscalation == 0 ? 80
                                         : track.DcCongestionEscalation == 1 ? 60
                                         : 50;
                    int reduced = Math.Max(_bitrateController.MinBitrateKbps, currentBitrate * reductionPercent / 100);
                    if (reduced < currentBitrate)
                    {
                        track.TrackBitrateKbps = reduced;
                        track.CongestionBitrateReduced = true;
                        lock (track.EncodeLock) { track.Encoder?.SetBitrate(reduced); }
                        // Also inform global ABC about congestion ceiling (for recovery limiting)
                        _bitrateController.MarkDcCongestion(currentBitrate);
                        track.DcCongestionEscalation = Math.Min(track.DcCongestionEscalation + 1, 3);
                        Logger.Debug($"[SIPSorcery] Track {monitorIndex} DC soft congestion → {currentBitrate} → {reduced}kbps (escalation={track.DcCongestionEscalation})");
                    }
                }
            }
            else if (!track.DcSoftCongestion && trackBuffered <= 400_000)
            {
                track.DcSoftCongestionEntryTicks = 0;
            }
            // Recovery: THIS track's buffer must stay below 200KB for 500ms before clearing.
            else if (track.DcSoftCongestion && trackBuffered < 200_000)
            {
                if (track.DcSoftCongestionClearTicks == 0)
                {
                    track.DcSoftCongestionClearTicks = now;
                }
                else if (now - track.DcSoftCongestionClearTicks > 500)
                {
                    if (track.DcSoftCongestion)
                    {
                        track.DcSoftCongestion = false;
                        track.DcSoftCongestionClearTicks = 0;
                        track.CongestionBitrateReduced = false;
                        // Reset escalation counter on successful recovery
                        track.DcCongestionEscalation = 0;
                        // DON'T restore bitrate immediately — this caused oscillation:
                        // restore → congestion → reduce → clear → restore → congestion...
                        // Keep the reduced bitrate and let ABC naturally recover over time.
                        _bitrateController.MarkCongestionCleared();
                        Logger.Debug($"[SIPSorcery] Track {monitorIndex} DC congestion cleared → {track.TrackBitrateKbps}kbps");
                    }
                }
            }
            else if (track.DcSoftCongestion && trackBuffered >= 200_000)
            {
                track.DcSoftCongestionClearTicks = 0;
            }
        }

        lock (track.EncodeLock)
        {
            try
            {
                // Ensure encoder matches incoming texture size
                EnsureEncoderMatchesResolution(track, width, height);
                if (track.Encoder == null) return;

                // Apply deferred bitrate change from OnEncodedData callback.
                // SetBitrate MUST be called OUTSIDE the encode call (not from callback),
                // otherwise AMF native handle throws SEH exception.
                int pendingBr = track.PendingBitrateKbps;
                if (pendingBr > 0)
                {
                    track.PendingBitrateKbps = 0;
                    track.Encoder.SetBitrate(pendingBr);
                }

                track.PendingFrame = null;
                track.PendingCaptureTimestampMs = captureTimestampMs;

                // INPUT-DRIVEN KEYFRAME REQUEST (PRIMARY FIX for tab-switch bug):
                // When the client sends input (Tab / Alt-Tab / click / Ctrl-key combo /
                // gamepad press), the next encoded frame must be an IDR — otherwise the
                // P-frame references the PRE-input keyframe and the client reconstructs
                // "pre-switch desktop + post-switch video area" for up to GOP seconds.
                //
                // Throttle: 400ms between forced IDRs per track. This allows:
                //   - Discrete events (click, tab, key press) → fire IDR within 1 frame (~17ms @ 60fps).
                //   - Continuous mouse motion (no discrete events) → IDR every 400ms (~2.5/s).
                // NVENC HEVC IDR is 200-450KB; 2.5/s = ~0.5-1.1Mbps extra on 1080p, well within
                // our 8Mbps cap on WiFi. LAN is unaffected.
                if (forceKeyframe)
                {
                    long now = Environment.TickCount64;
                    if (now - track.LastKeyframeRequestTicks >= 400)
                    {
                        track.ForceNextKeyframe = true;
                        track.LastKeyframeRequestTicks = now;
                        // Info level (not Debug) so we can verify the path fires in production logs.
                        Logger.Info($"[SIPSorcery] Track {monitorIndex} input-driven IDR request (input/wake-from-idle)");
                    }
                }

                // SCENE-CHANGE IDR REQUEST (HEURISTIC):
                // Secondary signal for the case where dirty metadata is large but no
                // discrete input event fires (e.g., a window appears from a system event).
                // Throttle 250ms; scene-change is rarer than input events so tighter is OK.
                if (isSceneChange)
                {
                    long now = Environment.TickCount64;
                    if (now - track.LastKeyframeRequestTicks >= 250)
                    {
                        track.ForceNextKeyframe = true;
                        track.LastKeyframeRequestTicks = now;
                        Logger.Info($"[SIPSorcery] Track {monitorIndex} scene-change → forcing IDR (dirty metadata large)");
                    }
                }

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

    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height, long captureTimestampMs = 0, bool isSceneChange = false, bool forceKeyframe = false)
    {
        if (!_running || _disposed || _isPaused) return;
        // Relay-media mode has no PeerConnection/DTLS — frames flow via OnRelayVideoFrame,
        // so the WebRTC-connected gate must not block the encode pipeline.
        if (!_connected && !RelayMediaMode) return;
        if (!_phase3Active) return;

        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;
        var track = _tracks[monitorIndex];
        // Per-track PC mode: video goes via dedicated video DC, not RTP MediaStreamTrack.
        // Relay-media mode needs no WebRTC track/DC — the WS relay carries the frames.
        bool trackReady = RelayMediaMode
            || (_perTrackPcMode ? (_videoDcs.ContainsKey(track.Index)) : (track.Track != null));
        if (!trackReady || track.Encoder == null) return;

        // H265 hybrid mode: Only need DC for first IDR bootstrap.
        // After that, P-frames go via RTP — no need to block on DC.
        // (Relay mode never opens a DC — skip the gate or no frame would ever pass.)
        if (!RelayMediaMode && _negotiatedCodec == VideoCodec.H265 && !IsH265DataChannelReady()
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

                // INPUT-DRIVEN KEYFRAME REQUEST (PRIMARY FIX for tab-switch bug).
                // See PushBgraTexture for full rationale. 400ms throttle.
                if (forceKeyframe)
                {
                    long now = Environment.TickCount64;
                    if (now - track.LastKeyframeRequestTicks >= 400)
                    {
                        track.ForceNextKeyframe = true;
                        track.LastKeyframeRequestTicks = now;
                        Logger.Info($"[SIPSorcery] Track {monitorIndex} input-driven IDR request (input/wake-from-idle)");
                    }
                }

                // SCENE-CHANGE IDR REQUEST (HEURISTIC). 250ms throttle.
                if (isSceneChange)
                {
                    long now = Environment.TickCount64;
                    if (now - track.LastKeyframeRequestTicks >= 250)
                    {
                        track.ForceNextKeyframe = true;
                        track.LastKeyframeRequestTicks = now;
                        Logger.Info($"[SIPSorcery] Track {monitorIndex} scene-change → forcing IDR (dirty metadata large)");
                    }
                }

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

    private void OnEncodedData(TrackInfo track, ArraySegment<byte> nalData, bool isKeyframe, long pts100ns)
    {
        if (!_running) return;
        // Relay-media mode has no PeerConnection/DTLS — skip the WebRTC-connected gate.
        if (!RelayMediaMode)
        {
            if (_mainPc == null || !_connected) return;
            if (_mainPc.connectionState != RTCPeerConnectionState.connected) return;
        }

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
            long earlyFrameNum = Interlocked.Read(ref track.SentFrames);

            // CRITICAL: Drop P-frames that arrive before the first IDR of this session.
            // AMF/NVENC encoders have a hardware pipeline — stale P-frames from the
            // previous session can drain AFTER forceKeyframe=true is requested,
            // arriving before the actual IDR. Sending a P-frame as the first frame
            // causes the client decoder to fail and trigger a reconnect loop.
            if (!isKeyframe && Interlocked.Read(ref track.SentFrames) == 0)
            {
                if (earlyFrameNum % 10 == 0)
                    Logger.Info($"[SIPSorcery] Track {track.Index}: Dropping stale P-frame before first IDR ({nalData.Count} bytes) - requesting keyframe");
                
                // Force IDR on next encode opportunity
                track.ForceNextKeyframe = true;

                return;
            }

            // Materialize ArraySegment to byte[] for send infrastructure.
            // Dropped frames (above) skip this — saving one allocation per drop.
            byte[] nalBytes = nalData.ToArray();

            // Fan-out: notify viewers with pre-encoded frame (no re-encode needed)
            OnEncodedFrameAvailable?.Invoke(track.Index, nalBytes, isKeyframe, track.LastH265ParamSets);

            // Relay-media fallback: emit protocol-v2 framed chunks to the relay path
            // (same wire format the client's VideoFrameParser expects) instead of DC/RTP.
            if (RelayMediaMode)
            {
                EmitFrameToRelay(track, nalBytes, isKeyframe);
                return;
            }

            if (isKeyframe)
            {
                track.IsDecodable = true;
                // H265 HYBRID MODE: Send IDR via reliable DataChannel for decoder bootstrap.
                // P-frames go via standard RTP (handled below in SendFrameImmediate).
                if (_negotiatedCodec == VideoCodec.H265)
                {
                    Interlocked.Increment(ref track.IdrReceivedFromEncoder);

                    // If DataChannel isn't open yet (race on reconnect), skip this keyframe
                    // and force the encoder to produce another one.
                    if (!IsH265DataChannelReady())
                    {
                        long dcNotReadyCount = Interlocked.Increment(ref track.DcNotReadyCount);
                        Interlocked.Increment(ref track.IdrDeferredDcNotReady);
                        if (dcNotReadyCount <= 3 || dcNotReadyCount % 120 == 0)
                            Logger.Warn($"[SIPSorcery] Track {track.Index}: DataChannel not ready, deferring IDR #{dcNotReadyCount} (will force next keyframe)");
                        track.ForceNextKeyframe = true;
                        Interlocked.Exchange(ref track.SentFrames, 0);

                        EmitIdrDiagIfDue(track);
                        return;
                    }

                    // Send codec config on first IDR after session start/reconnect/decoder_ready.
                    if (track.IdrViaDcCount == 0)
                    {
                        SendH265ParamSetsViaDataChannel(track, nalBytes);
                    }

                    if (SendH265IdrViaDataChannel(track, nalBytes, out var idrChunks))
                    {
                        track.IdrViaDcCount++;
                        Interlocked.Increment(ref track.IdrSentViaDc);

                        // NOTE: Bootstrap IDR retry DISABLED.
                        // Original intent: retry IDR after 500ms in case SCTP dropped it during slow-start.
                        // Reality: 1080p H265 IDR = 200-450KB (not 60-100KB as assumed). SCTP delivers it
                        // reliably (just slowly during cwnd ramp-up). The retry IDR dumps another 200-450KB
                        // into the buffer → instant DC congestion → P-frame drops → prediction chain break
                        // → text smearing artifacts on initial connection.

                        // IDR sent via DC — P-frames also go via DC below.
                        IncrementSentFrames(track);
                        EmitIdrDiagIfDue(track);
                        return;
                    }
                    // IDR deferred/failed — diagnose which path rejected it.
                    // SendH265IdrViaDataChannel already logged Warn/Debug for HIGH/MID/no-NAL paths.
                    EmitIdrDiagIfDue(track);
                    return;
                }
            }
            // H265 P-frames: Send via DataChannel (same as IDR).
            // Unity WebRTC's Encoded Transform NEVER fires for H.265 RTP packets,
            // so ALL H.265 frames must go through DataChannel for reliable delivery.
            if (_negotiatedCodec == VideoCodec.H265)
            {
                if (SendH265PFrameViaDataChannel(track, nalBytes))
                {
                    IncrementSentFrames(track);
                    return;
                }
                // P-frame send failed (DC not ready or congested) — drop it.
                // Next IDR will resync the decoder.
                return;
            }

            // H264 via DataChannel: same transport as H265 to bypass RTP jitter buffer.
            // Client now creates AVC MediaCodec decoder for h265video-N DCs when codec=H264.
            // Falls back to RTP below if DataChannels are not available.
            if (_negotiatedCodec == VideoCodec.H264 && IsH265DataChannelReady())
            {
                if (isKeyframe)
                {
                    // Send SPS/PPS config on first IDR after session start/reconnect/decoder_ready
                    if (track.IdrViaDcCount == 0)
                        SendCodecConfigViaDataChannel(track, nalBytes);

                    if (SendH265IdrViaDataChannel(track, nalBytes, out _))
                    {
                        track.IdrViaDcCount++;
                        IncrementSentFrames(track);
                        return;
                    }
                    // DC send failed — fall through to RTP
                }
                else
                {
                    if (SendH265PFrameViaDataChannel(track, nalBytes))
                    {
                        IncrementSentFrames(track);
                        return;
                    }
                    // DC send failed — fall through to RTP
                }
            }

            byte[] au = nalBytes;
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

        IncrementSentFrames(track);
    }

    private void IncrementSentFrames(TrackInfo track)
    {
        long prev = Interlocked.Read(ref track.SentFrames);
        Interlocked.Increment(ref track.SentFrames);
        // Notify capture that initial frame has been delivered — stops forcing frames on idle desktops
        if (prev == 0)
            OnInitialFrameSent?.Invoke(track.Index);
    }

    private void SendRtpPacket(TrackInfo track, byte[] payload, uint rtpTimestamp, int markerBit, long frameNum)
    {
        if (_mainPc == null) return;

        // Increment sequence number for each RTP packet
        track.SequenceNumber++;

        // Session startup diagnostic: log details for the first packet of each session
        if (frameNum == 0 && !track.IsSessionStarted)
        {
            track.IsSessionStarted = true;
            Logger.Info($"[SIPSorcery] Track {track.Index} SESSION START: SSRC={track.Ssrc}, First Seq={track.SequenceNumber}, First TS={rtpTimestamp}");
        }

        // 1. Preferred: Multi-track send using MediaStream.SendRtpRaw (supports manual seqNum)
        if (_mainPc.VideoStreamList != null && track.Index < _mainPc.VideoStreamList.Count)
        {
            var videoStream = _mainPc.VideoStreamList[track.Index];
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
                _mainPc.SendRtpRaw(SDPMediaTypesEnum.video, payload, rtpTimestamp, markerBit, track.PayloadType);
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
        if (!_running || _mainPc == null || !_connected) return;

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
    /// Per-track PC mode: checks _videoDcs on dedicated video PCs.
    /// Legacy mode: checks per-track DCs on shared PC, then single DC, then cursor DC fallback.
    /// </summary>
    private bool IsH265DataChannelReady()
    {
        if (_perTrackPcMode)
        {
            // Per-track PC mode: at least one video PC's DC must be open
            if (_videoDcs.Values.Any(dc => dc.readyState == RTCDataChannelState.open))
                return true;
            // Fallback: check main PC's h265video DCs (when video PCs failed to connect)
            if (_perTrackFallbackActive)
            {
                lock (_perTrackFallbackDcs)
                {
                    return _perTrackFallbackDcs.Values.Any(dc => dc.readyState == RTCDataChannelState.open);
                }
            }
            return false;
        }

        // Legacy mode:
        // Per-track DCs on shared PC: if any exist, at least one should be open
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
    /// In per-track PC mode: uses _videoDcs[trackIndex] (DC on dedicated video PC).
    /// In legacy mode priority: per-track DC on shared PC → legacy single DC → cursor DC fallback.
    /// Per-track DCs (both modes) give each track its own SCTP buffer → no cross-track congestion.
    /// </summary>
    private RTCDataChannel? GetH265VideoChannel(int trackIndex)
    {
        // Per-track PC mode: use the DC on the dedicated video PC
        if (_perTrackPcMode)
        {
            if (_videoDcs.TryGetValue(trackIndex, out var videoPerPcDc) &&
                videoPerPcDc.readyState == RTCDataChannelState.open)
                return videoPerPcDc;
            // Fallback: use main PC's h265video DC when video PCs failed
            if (_perTrackFallbackActive)
            {
                lock (_perTrackFallbackDcs)
                {
                    if (_perTrackFallbackDcs.TryGetValue(trackIndex, out var fallbackDc) &&
                        fallbackDc.readyState == RTCDataChannelState.open)
                        return fallbackDc;
                }
            }
            return null;
        }

        // Legacy mode:
        // 1. Per-track DC on shared PC (best: isolated buffer per track)
        lock (_h265VideoDcs)
        {
            if (_h265VideoDcs.TryGetValue(trackIndex, out var perTrackDc) &&
                perTrackDc.readyState == RTCDataChannelState.open)
                return perTrackDc;

            // 2. Shared DC fallback: when perTrackPc=false (H264), client creates only h265video-0.
            // All tracks must share this single DC. The trackIdx in the protocol header
            // tells the client which monitor the frame belongs to.
            if (trackIndex != 0 && _h265VideoDcs.TryGetValue(0, out var sharedDc) &&
                sharedDc.readyState == RTCDataChannelState.open)
                return sharedDc;
        }
        // 3. Legacy single DC
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
    /// <returns>true if IDR was sent, false if deferred or failed. chunks sent via out param.</returns>
    private bool SendH265IdrViaDataChannel(TrackInfo track, byte[] keyframeData, out int chunksSent)
    {
        chunksSent = 0;
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
            track.CongestionBitrateReduced = false;
            track.DcCongestionEscalation = 0; // Reset escalation on successful drain
            long now = Environment.TickCount64;
            bool cooldownOk = (now - Interlocked.Read(ref _lastDrainIdrTicks)) > 2000;
            if (cooldownOk)
            {
                Interlocked.Exchange(ref _lastDrainIdrTicks, now);
                track.ForceNextKeyframe = true;
                // DON'T restore bitrate — keep reduced rate, let ABC recover naturally
                Logger.Info($"[SIPSorcery] Track {track.Index}: DC drained ({buffered/1024}KB) — queued IDR (keeping {track.TrackBitrateKbps}kbps, ABC will recover)");
            }
            else
            {
                Logger.Debug($"[SIPSorcery] Track {track.Index}: DC drained — cooldown");
            }
        }
        // Legacy single-DC drain detection (only in legacy mode)
        if (!perTrackMode && _dcWasAboveHigh && buffered < DC_BUFFER_LOW_WATER)
        {
            _dcWasAboveHigh = false;
            Logger.Debug($"[SIPSorcery] DC buffer drained — resuming");
        }

        // Flow control: Block IDR when buffer is severely congested.
        // When buffer is between MID and HIGH, allow IDR if P-frames were dropped (track needs resync).
        // Previously blocking at HIGH (1MB) created a death spiral: no IDR → can't resync → more drops.
        if (buffered > DC_BUFFER_HIGH_WATER)
        {
            Logger.Warn($"[SIPSorcery] Track {track.Index}: DC buffer HIGH ({buffered/1024}KB), deferring IDR");
            track.PFramesDroppedDuringCongestion = true; // IDR deferred = track also needs resync
            if (!perTrackMode) _dcWasAboveHigh = true;
            Interlocked.Increment(ref track.IdrDeferredDcHigh);
            return false;
        }
        // Buffer between MID and HIGH: allow IDR only if track needs resync (P-frames were dropped).
        // This breaks the congestion death spiral by allowing recovery before full drain.
        if (buffered > DC_BUFFER_MID_WATER && !track.PFramesDroppedDuringCongestion)
        {
            Logger.Debug($"[SIPSorcery] Track {track.Index}: DC buffer MID ({buffered/1024}KB), deferring non-critical IDR");
            Interlocked.Increment(ref track.IdrDeferredDcMid);
            return false;
        }

        try
        {
            // Extract IDR NAL data (skip param sets — those are sent separately as type=0x02)
            var idrData = _negotiatedCodec == VideoCodec.H264
                ? ExtractH264IdrData(keyframeData)
                : ExtractH265IdrData(keyframeData);
            if (idrData == null || idrData.Length == 0)
            {
                Logger.Warn($"[SIPSorcery] Track {track.Index}: No IDR NAL found in keyframe ({keyframeData.Length} bytes)");
                Interlocked.Increment(ref track.IdrNoNalFound);
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

                var msg = BuildPaddedMessage(0x03, track.Index, chunk, totalChunks, idrData, offset, len);
                dc.send(msg);
            }
            chunksSent = totalChunks;

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 IDR: {ex.Message}");
            Interlocked.Increment(ref track.IdrSendException);
            return false;
        }
    }

    /// <summary>
    /// Periodically log per-track IDR pipeline counters so we can see exactly where
    /// IDRs are being lost between encoder output and client reception.
    /// </summary>
    private void EmitIdrDiagIfDue(TrackInfo track)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref track.LastIdrDiagTicks);
        if (now - last > 5_000 && Interlocked.CompareExchange(ref track.LastIdrDiagTicks, now, last) == last)
        {
            long recv = Interlocked.Read(ref track.IdrReceivedFromEncoder);
            long sent = Interlocked.Read(ref track.IdrSentViaDc);
            long dNotReady = Interlocked.Read(ref track.IdrDeferredDcNotReady);
            long dHigh = Interlocked.Read(ref track.IdrDeferredDcHigh);
            long dMid = Interlocked.Read(ref track.IdrDeferredDcMid);
            long noNal = Interlocked.Read(ref track.IdrNoNalFound);
            long exc = Interlocked.Read(ref track.IdrSendException);
            long totalDeferred = dNotReady + dHigh + dMid + noNal + exc;
            Logger.Info($"[SIPSorcery][idr-diag] Track {track.Index}: recvFromEncoder={recv} sentViaDc={sent} " +
                        $"deferred(notReady={dNotReady} high={dHigh} mid={dMid} noNal={noNal} exc={exc} total={totalDeferred})");
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
            // Per-track bitrate reduction: DEFER SetBitrate to next PushBgraTexture call.
            // OnEncodedData runs from WITHIN EncodeLock → calling SetBitrate here causes
            // AMF SEH exception (native handle in use during encode).
            // Escalating reduction: each consecutive HIGH event reduces more aggressively.
            {
                track.CongestionBitrateReduced = true;
                int currentBitrate = track.TrackBitrateKbps > 0 ? track.TrackBitrateKbps : _bitrateController.TargetBitrateKbps;
                int reductionPercent = track.DcCongestionEscalation == 0 ? 80
                                     : track.DcCongestionEscalation == 1 ? 60
                                     : 50;
                int reducedBitrate = Math.Max(_bitrateController.MinBitrateKbps, currentBitrate * reductionPercent / 100);
                if (reducedBitrate < currentBitrate)
                {
                    track.TrackBitrateKbps = reducedBitrate;
                    track.PendingBitrateKbps = reducedBitrate; // Deferred: applied by next PushBgraTexture
                    _bitrateController.MarkDcCongestion(currentBitrate);
                    track.DcCongestionEscalation = Math.Min(track.DcCongestionEscalation + 1, 3);
                    Logger.Debug($"[SIPSorcery] Track {track.Index} DC HIGH congestion → {currentBitrate} → {reducedBitrate}kbps (escalation={track.DcCongestionEscalation})");
                }
            }
            if (Interlocked.Read(ref track.SentFrames) % 60 == 0)
                Logger.Warn($"[SIPSorcery] Track {track.Index}: DC buffer HIGH ({buffered/1024}KB), dropping P-frame");
            return false;
        }

        // Per-track drain detection: this track's DC was congested, now drained.
        if (track.PFramesDroppedDuringCongestion && buffered < DC_BUFFER_LOW_WATER)
        {
            track.PFramesDroppedDuringCongestion = false;
            track.CongestionBitrateReduced = false;
            track.DcCongestionEscalation = 0; // Reset escalation on successful drain
            bool cooldownOk = (now - Interlocked.Read(ref _lastDrainIdrTicks)) > 2000;
            if (cooldownOk)
            {
                Interlocked.Exchange(ref _lastDrainIdrTicks, now);
                track.ForceNextKeyframe = true;
                // Defer bitrate restore to next PushBgraTexture
                int globalBitrate = _bitrateController.TargetBitrateKbps;
                track.TrackBitrateKbps = globalBitrate;
                track.PendingBitrateKbps = globalBitrate; // Deferred: applied by next PushBgraTexture
                Logger.Debug($"[SIPSorcery] Track {track.Index}: DC drained → IDR + restore {globalBitrate}kbps");
            }
            else
            {
                Logger.Debug($"[SIPSorcery] Track {track.Index}: DC drained — cooldown");
            }
        }
        // Legacy single-DC drain detection (only in legacy mode)
        if (!perTrackMode && _dcWasAboveHigh && buffered < DC_BUFFER_LOW_WATER)
        {
            _dcWasAboveHigh = false;
            Logger.Debug($"[SIPSorcery] DC buffer drained — resuming");
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

                var msg = BuildPaddedMessage(0x04, track.Index, chunk, totalChunks, pframeData, offset, len);
                dc.send(msg);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send H265 P-frame: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply bitrate to all track encoders, respecting per-track congestion state.
    /// Tracks that have their own congestion-reduced bitrate are NOT overridden,
    /// keeping the congested track at its reduced rate while non-congested tracks
    /// get the new global bitrate.
    /// </summary>
    private void ApplyBitrateToAllEncoders(int bitrateKbps)
    {
        TrackInfo[] snapshot;
        lock (_lock) { snapshot = _tracks.ToArray(); }
        foreach (var t in snapshot)
        {
            try
            {
                // Skip tracks that are in per-track congestion — they manage their own bitrate
                if (t.CongestionBitrateReduced)
                {
                    Logger.Debug($"[SIPSorcery] Track {t.Index} skipped ApplyBitrate({bitrateKbps}) — track has own congestion bitrate {t.TrackBitrateKbps}kbps");
                    continue;
                }
                t.TrackBitrateKbps = bitrateKbps;
                lock (t.EncodeLock) { t.Encoder?.SetBitrate(bitrateKbps); }
            }
            catch (Exception ex)
            {
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
        // Per-track PC mode always uses per-track DCs (one per video PC)
        if (_perTrackPcMode) return true;
        // Legacy: per-track DCs on shared PC when client opened h265video-N channels
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
    /// Send codec config (SPS/PPS for H264, VPS/SPS/PPS for H265) via DataChannel.
    /// Codec-agnostic wrapper that extracts the right parameter sets based on negotiated codec.
    /// Message format: [type=0x02][trackIndex(1)][annexB param set bytes...]
    /// </summary>
    private void SendCodecConfigViaDataChannel(TrackInfo track, byte[] keyframeData)
    {
        var dc = GetH265VideoChannel(track.Index);
        if (dc == null) return;

        try
        {
            byte[] paramSets;
            if (_negotiatedCodec == VideoCodec.H264)
                paramSets = ExtractH264ParamSets(keyframeData);
            else
                paramSets = ExtractH265ParamSets(keyframeData);

            if (paramSets.Length > 0)
                track.LastH265ParamSets = paramSets;
            else
                paramSets = track.LastH265ParamSets ?? Array.Empty<byte>();

            if (paramSets.Length == 0)
            {
                if (Interlocked.Read(ref track.SentFrames) > 0)
                    Logger.Warn($"[SIPSorcery] Track {track.Index}: No param sets found in keyframe AND no cache available");
                return;
            }

            var msg = new byte[2 + paramSets.Length];
            msg[0] = 0x02; // message type: codec_config
            msg[1] = (byte)track.Index;
            Buffer.BlockCopy(paramSets, 0, msg, 2, paramSets.Length);

            dc.send(msg);

            if (track.IdrViaDcCount == 0)
                Logger.Info($"[SIPSorcery] Track {track.Index}: Sent {_negotiatedCodec} codec config via DataChannel ({paramSets.Length} bytes)");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: Failed to send codec config: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract SPS(7), PPS(8) NAL units from H264 Annex-B bitstream.
    /// Returns concatenated Annex-B bytes containing only parameter set NALs.
    /// </summary>
    private static byte[] ExtractH264ParamSets(byte[] annexB)
    {
        using var result = new System.IO.MemoryStream();
        int i = 0;
        while (i < annexB.Length - 3)
        {
            int scLen = 0;
            if (i < annexB.Length - 3 && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                scLen = 4;
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                scLen = 3;

            if (scLen == 0) { i++; continue; }

            int nalStart = i;
            int headerPos = i + scLen;
            if (headerPos >= annexB.Length) break;

            int nalType = annexB[headerPos] & 0x1F; // H264: lower 5 bits

            int nalEnd = annexB.Length;
            for (int j = headerPos + 1; j < annexB.Length - 3; j++)
            {
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 0 && annexB[j + 3] == 1)
                { nalEnd = j; break; }
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 1)
                { nalEnd = j; break; }
            }

            // Keep SPS(7), PPS(8)
            if (nalType == 7 || nalType == 8)
            {
                result.Write(annexB, nalStart, nalEnd - nalStart);
            }

            // Stop after IDR slice (type 5) — no more param sets after this
            if (nalType == 5) break;

            i = nalEnd;
        }
        return result.ToArray();
    }

    /// <summary>
    /// Relay-media fallback: emit one encoded frame as protocol-v2 chunks to
    /// OnRelayVideoFrame (carried over the WebSocket relay). Mirrors the DataChannel
    /// framing (param-sets 0x02, IDR 0x03, P-frame 0x04) so the client's existing
    /// VideoFrameParser handles it unchanged. Reuses BuildPaddedMessage + the 60KB
    /// chunk size. All chunks of a frame go out as ONE atomic ordered list — the
    /// relay sender (PhaseProtocolHandler.RelayMedia) drops whole frames under TCP
    /// backpressure, never individual chunks (a missing chunk corrupts the NAL and
    /// poisons every following P-frame → macroblock garbage on the client).
    /// </summary>
    private void EmitFrameToRelay(TrackInfo track, byte[] frameData, bool isKeyframe)
    {
        try
        {
            var chunks = new System.Collections.Generic.List<byte[]>(4);

            if (isKeyframe)
            {
                var paramSets = ExtractH265ParamSets(frameData);
                if (paramSets != null && paramSets.Length > 0)
                    track.LastH265ParamSets = paramSets;
                else
                    paramSets = track.LastH265ParamSets;

                if (paramSets != null && paramSets.Length > 0)
                {
                    var cfg = new byte[2 + paramSets.Length];
                    cfg[0] = 0x02; // h265_codec_config
                    cfg[1] = (byte)track.Index;
                    Buffer.BlockCopy(paramSets, 0, cfg, 2, paramSets.Length);
                    chunks.Add(cfg);
                }
            }

            byte frameType = isKeyframe ? (byte)0x03 : (byte)0x04;
            const int MAX_CHUNK = 60_000;
            int totalChunks = (frameData.Length + MAX_CHUNK - 1) / MAX_CHUNK;
            if (totalChunks > 255) totalChunks = 255;

            for (int chunk = 0; chunk < totalChunks; chunk++)
            {
                int offset = chunk * MAX_CHUNK;
                int len = Math.Min(MAX_CHUNK, frameData.Length - offset);
                chunks.Add(BuildPaddedMessage(frameType, track.Index, chunk, totalChunks, frameData, offset, len));
            }

            OnRelayVideoFrame?.Invoke(track.Index, chunks, isKeyframe);
            IncrementSentFrames(track);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Track {track.Index}: relay emit error: {ex.Message}");
        }
    }

    /// <summary>
    /// Build a padded video frame message (protocol v2).
    /// Format: [type(1)][trackIdx(1)][chunkIdx(1)][totalChunks(1)][originalSize(2 BE)][NAL data...][zero padding...]
    /// Small messages (&lt;1KB) are padded to MIN_FRAME_MSG_SIZE to force immediate SCTP flush,
    /// avoiding Nagle-like batching that adds 5-15ms latency to small P-frames.
    /// Large messages (&gt;= MIN_FRAME_MSG_SIZE) are sent at actual size (no padding needed).
    /// </summary>
    private static byte[] BuildPaddedMessage(byte type, int trackIndex, int chunkIndex, int totalChunks,
        byte[] data, int dataOffset, int dataLen)
    {
        int msgSize = Math.Max(PADDED_HEADER_SIZE + dataLen, MIN_FRAME_MSG_SIZE);
        var msg = new byte[msgSize];

        // Header
        msg[0] = type;
        msg[1] = (byte)trackIndex;
        msg[2] = (byte)chunkIndex;
        msg[3] = (byte)totalChunks;

        // Original payload size (uint16 big-endian) — client uses this to strip padding
        msg[4] = (byte)((dataLen >> 8) & 0xFF);
        msg[5] = (byte)(dataLen & 0xFF);

        // NAL data
        Buffer.BlockCopy(data, dataOffset, msg, PADDED_HEADER_SIZE, dataLen);

        // Remaining bytes (if any) are already zeroed by new byte[msgSize]
        return msg;
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

    /// <summary>
    /// Extract IDR slice NAL (type 5) from H264 Annex-B bitstream.
    /// Skips SPS(7) and PPS(8) since those are sent separately as codec config.
    /// </summary>
    private static byte[] ExtractH264IdrData(byte[] annexB)
    {
        using var result = new System.IO.MemoryStream();
        int i = 0;
        while (i < annexB.Length - 3)
        {
            int scLen = 0;
            if (i < annexB.Length - 3 && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                scLen = 4;
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                scLen = 3;

            if (scLen == 0) { i++; continue; }

            int nalStart = i;
            int headerPos = i + scLen;
            if (headerPos >= annexB.Length) break;

            int nalType = annexB[headerPos] & 0x1F; // H264: lower 5 bits

            int nalEnd = annexB.Length;
            for (int j = headerPos + 1; j < annexB.Length - 3; j++)
            {
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 0 && annexB[j + 3] == 1)
                { nalEnd = j; break; }
                if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 1)
                { nalEnd = j; break; }
            }

            // Keep IDR slice (type 5) and SEI (type 6) — skip SPS(7)/PPS(8)
            if (nalType == 5 || nalType == 6)
            {
                result.Write(annexB, nalStart, nalEnd - nalStart);
            }

            i = nalEnd;
        }
        return result.ToArray();
    }
}
