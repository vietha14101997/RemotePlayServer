#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D11;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    /// <summary>
    /// Process single SDP offer (with N m= sections), create N tracks, return single answer.
    /// </summary>
    public Task<string> ProcessOfferAsync(string offerSdp, List<(int w, int h)> dimensions)
    {
        // 1. CLEAR previous connection state but PRESERVE TrackInfo list for SSRC persistence!
        // (CloseConnection disposes _pc, _audioDc, etc. but not the underlying encoders/SSRCs)
        CloseConnection();

        // Ensure fresh sync state for Every new connection/reconnect.
        // Clears RTP offsets and capture start time to prevent client jitter buffer inflation.
        ResetSyncState();

        // Parse negotiated codec payload type from offer
        var (negotiatedPt, negotiatedFmtp) = TryGetCodecFromOfferSdp(offerSdp, _negotiatedCodec);
        Logger.Info($"[SIPSorcery] Offer {_negotiatedCodec} pt={negotiatedPt ?? 96}, fmtp={negotiatedFmtp ?? "default"}");

        // Log client fingerprint from offer for DTLS debugging
        foreach (var line in offerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] Client SDP: {line}");
        }

        // Create PeerConnection with STUN for better ICE reliability
        var cfg = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };
        _pc = new RTCPeerConnection(cfg);
        Logger.Info("[SIPSorcery] PeerConnection created (with STUN)");

        // 2. Manage Video Tracks - Reuse SSRC from previous session if possible
        for (int i = 0; i < dimensions.Count; i++)
        {
            var (w, h) = dimensions[i];

            // Setup or reuse TrackInfo
            TrackInfo ti;
            lock (_tracks)
            {
                if (i < _tracks.Count)
                {
                    ti = _tracks[i];
                    // If dimensions changed, we might need a new encoder (managed later in InitializeEncoders)
                    ti.Width = w;
                    ti.Height = h;
                }
                else
                {
                    ti = new TrackInfo
                    {
                        Index = i,
                        Width = w,
                        Height = h,
                        // Generate a persistent SSRC for this monitor index
                        Ssrc = (uint)new Random().Next(100000000, 2000000000),
                        // Initialize sequence number to a random value per RFC 3550.
                        // Continuity is maintained across reconnections as TrackInfo is reused.
                        SequenceNumber = (ushort)new Random().Next(0, ushort.MaxValue)
                    };
                    _tracks.Add(ti);
                }
            }

            // Negotiated codec format
            string defaultFmtp = _negotiatedCodec == VideoCodec.H264
                ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f"
                : (_negotiatedCodec == VideoCodec.H265 ? "profile-id=1;tier-flag=0;level-id=123" : ""); 

            var codecFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                id: negotiatedPt ?? (96 + i),
                name: _negotiatedCodec.ToString(),
                clockRate: 90000,
                fmtp: MergeFmtp(defaultFmtp, negotiatedFmtp));

            var track = new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                isRemote: false,
                capabilities: new List<SDPAudioVideoMediaFormat> { codecFormat },
                streamStatus: MediaStreamStatusEnum.SendOnly);
            
            // Assign the PERSISTENT SSRC to this track
            track.Ssrc = ti.Ssrc;
            ti.PayloadType = codecFormat.ID;
            ti.Track = track;
            
            _pc.addTrack(track);

            // Device mapping logic (simplified for clarity)
            ID3D11Device? deviceForTrack = _sharedDevice;
            if (_deviceMappings.TryGetValue(i, out var mappedDevice)) deviceForTrack = mappedDevice;
            else if (_pendingDevices.TryGetValue(i, out var pendingDevice))
            {
                deviceForTrack = pendingDevice;
                _pendingDevices.Remove(i);
            }
            ti.Device = deviceForTrack;

            Logger.Info($"[SIPSorcery] Added track {i}: {w}x{h} (device={deviceForTrack?.GetHashCode():X8}), SSRC={ti.Ssrc}");
        }

        // Initialize per-monitor pause state (all monitors active initially)
        _monitorPaused = new bool[dimensions.Count];

        // Receive client-created DataChannel for low-latency audio.
        // Client creates DC "audio" (libwebrtc manages SCTP), server just receives and sends Opus via it.
        // Opus frames sent as binary messages: [type(1)][timestamp(8)][opus_data]
        _pc.ondatachannel += (dc) =>
        {
            Logger.Info($"[SIPSorcery] DataChannel received: label={dc.label}, id={dc.id}");
            if (dc.label == "audio")
            {
                _audioDc = dc;
                _audioDc.onopen += () => Logger.Info("[SIPSorcery] Audio DataChannel opened");
                _audioDc.onclose += () => { Logger.Info("[SIPSorcery] Audio DataChannel closed"); _audioDc = null; };
                Logger.Info("[SIPSorcery] Audio DataChannel wired for sending");
            }
            else if (dc.label == "cursor")
            {
                _cursorDc = dc;
                _cursorDc.onopen += () =>
                {
                    Logger.Info("[SIPSorcery] Cursor DataChannel opened");
                };
                _cursorDc.onclose += () => { Logger.Info("[SIPSorcery] Cursor DataChannel closed"); _cursorDc = null; };
                Logger.Info("[SIPSorcery] Cursor DataChannel wired for sending");
            }
            else if (dc.label == "h265video")
            {
                _h265VideoDc = dc;
                _h265VideoDc.onopen += () =>
                {
                    Logger.Info("[SIPSorcery] H265 Video DataChannel opened (unreliable, unordered) - forcing keyframe for bootstrap");
                    // Force keyframe on all tracks once H265 video DC is ready
                    RequestKeyframe(-1, force: true);
                };
                _h265VideoDc.onclose += () => { Logger.Info("[SIPSorcery] H265 Video DataChannel closed"); _h265VideoDc = null; };
                Logger.Info("[SIPSorcery] H265 Video DataChannel wired for sending");
            }
        };
        _hasAudioTrack = true;
        Logger.Info("[SIPSorcery] Waiting for client audio/cursor/h265video DataChannels");

        // ICE candidate forwarding
        _pc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Logger.Info($"[SIPSorcery] Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                OnIceCandidate?.Invoke(cand.candidate);
            }
            else
            {
                OnIceCandidate?.Invoke("end-of-candidates");
            }
        };

        // Connection state changes
        // NOTE: Do NOT initialize encoders here - it blocks DTLS handshake!
        // Encoder init moved to onconnectionstatechange (after DTLS complete)
        _pc.oniceconnectionstatechange += (state) =>
        {
            // Suppress duplicate "connected" events from ICE consent checks during active streaming
            if (state == RTCIceConnectionState.connected && _connected)
                return;

            Logger.Info($"[SIPSorcery] ICE state: {state}");
            if (state == RTCIceConnectionState.connected)
            {
                Logger.Info("[SIPSorcery] ICE CONNECTED - waiting for DTLS...");
                // Don't initialize encoders here - let DTLS complete first
            }
            else if (state == RTCIceConnectionState.failed)
            {
                _connected = false;
                OnConnectionFailed?.Invoke();
                OnFatalError?.Invoke("ICE Connection Failed");
            }
            else if (state == RTCIceConnectionState.disconnected || state == RTCIceConnectionState.closed)
            {
                _connected = false;
            }
        };

        // DTLS/SRTP connection state - initialize encoders AFTER DTLS completes
        _pc.onconnectionstatechange += (state) =>
        {
            Logger.Info($"[SIPSorcery] Peer state: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Logger.Info("[SIPSorcery] DTLS CONNECTED - initializing encoders now");
                _connected = true;
                InitializeEncoders();
                InitializeAudio();
                OnAllTracksReady?.Invoke();
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                Logger.Error("[SIPSorcery] DTLS FAILED - check certificate/fingerprint");
                _connected = false;
                OnConnectionFailed?.Invoke();
                OnFatalError?.Invoke("DTLS Handshake Failed");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                // Connection was closed unexpectedly (DTLS timeout, network issue, etc.)
                // Client should send a reconnect offer to recover
                Logger.Info("[SIPSorcery] Peer state CLOSED unexpectedly - awaiting client reconnect offer");
                _connected = false;
            }
            else if (state == RTCPeerConnectionState.disconnected)
            {
                // Temporary disconnection - may recover automatically
                // Don't fire OnConnectionFailed yet, give ICE time to recover
                Logger.Info("[SIPSorcery] Peer state DISCONNECTED - may recover, waiting...");
                _connected = false;
            }
        };

        _pc.onsignalingstatechange += () =>
        {
            Logger.Info($"[SIPSorcery] Signaling state: {_pc.signalingState}");
        };

        // Set remote offer and create answer
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _pc.setRemoteDescription(offer);
        Logger.Info($"[SIPSorcery] After setRemoteDescription: signalingState={_pc.signalingState}");

        var answer = _pc.createAnswer(null);
        Logger.Info($"[SIPSorcery] After createAnswer: signalingState={_pc.signalingState}, answer.type={answer.type}");

        // DO NOT call setLocalDescription(answer) — SIPSorcery 8.x signalingState is broken
        // (shows "closed" after setRemoteDescription), so setLocalDescription misinterprets
        // the answer as an offer, setting state to "have_local_offer" and corrupting DTLS config.
        // createAnswer() already configures DTLS internals correctly.

        _running = true;

        // Start stats logging
        _ = Task.Run(LogStatsAsync);

        var answerSdp = answer.sdp ?? "";

        // RFC 5763: Answerer MUST use "active" or "passive", NOT "actpass".
        // When signalingState works correctly (Android), SIPSorcery generates "active" natively.
        // When signalingState is broken (Unity Editor), it falls back to "actpass" which
        // libwebrtc rejects. Fix: replace with "active" for RFC compliance.
        if (answerSdp.Contains("a=setup:actpass"))
        {
            answerSdp = answerSdp.Replace("a=setup:actpass", "a=setup:active");
            Logger.Info("[SIPSorcery] SDP: fixed actpass -> active in answer (RFC 5763)");
        }

        if (!answerSdp.Contains("SAVPF"))
            answerSdp = answerSdp.Replace("SAVP", "SAVPF");
        answerSdp = FilterAnswerSdpIceCandidates(answerSdp);

        // Log DTLS-critical SDP attributes for debugging
        foreach (var line in answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] SDP: {line}");
        }

        Logger.Info($"[SIPSorcery] Answer ready, {answerSdp.Length} bytes");
        return Task.FromResult(answerSdp);
    }

    public void AddIceCandidate(string candidate, string? mid = null)
    {
        if (_pc == null || string.IsNullOrEmpty(candidate)) return;

        try
        {
            var candStr = candidate.Trim();
            if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring(2);
            if (candStr.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring("candidate:".Length);
            if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = "candidate:" + candStr;

            var init = new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = mid ?? "0" };
            _pc.addIceCandidate(init);
            Logger.Info($"[SIPSorcery] Added remote ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] AddIceCandidate error: {ex.Message}");
        }
    }

    // Buffer for audio ICE candidates that arrive before the audio PC is created
    // (trickle ICE: client sends candidates immediately, but audio_offer may arrive later)
    private readonly List<string> _pendingAudioIceCandidates = new();

    // Buffer for server-side audio ICE candidates generated during createAnswer()
    // These must be sent AFTER the answer SDP to avoid client receiving candidates before remote description
    private readonly List<string> _pendingAudioLocalCandidates = new();
    private volatile bool _audioAnswerDelivered;

    /// <summary>
    /// Process an audio offer from the client's dedicated audio PeerConnection.
    /// Creates a second RTCPeerConnection with its own SCTP association,
    /// isolating audio DataChannel traffic from H.265 video congestion.
    /// </summary>
    public Task<string> ProcessAudioOfferAsync(string offerSdp)
    {
        try
        {
            // Close previous audio PC if any
            try { _audioPc?.close(); } catch { }
            _audioPc = null;
            try { _audioPcAudioDc?.close(); } catch { }
            _audioPcAudioDc = null;

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };
            _audioPc = new RTCPeerConnection(config);

            // Wire DataChannel handler - client creates "audio" DC on this PC
            _audioPc.ondatachannel += (dc) =>
            {
                Logger.Info($"[SIPSorcery] Audio PC DataChannel received: label={dc.label}, id={dc.id}");
                if (dc.label == "audio")
                {
                    _audioPcAudioDc = dc;
                    dc.onopen += () => Logger.Info("[SIPSorcery] Audio PC audio DC opened (dedicated SCTP - low latency)");
                    dc.onclose += () =>
                    {
                        Logger.Info("[SIPSorcery] Audio PC audio DC closed");
                        _audioPcAudioDc = null;
                    };
                }
            };

            // ICE candidate forwarding for audio PC
            // Buffer candidates until answer is delivered to client, then send directly
            _audioAnswerDelivered = false;
            _audioPc.onicecandidate += (cand) =>
            {
                if (cand != null && !string.IsNullOrEmpty(cand.candidate))
                {
                    Logger.Info($"[SIPSorcery] Audio PC Local ICE: {cand.candidate.Substring(0, Math.Min(60, cand.candidate.Length))}...");
                    if (_audioAnswerDelivered)
                    {
                        OnAudioIceCandidate?.Invoke(cand.candidate);
                    }
                    else
                    {
                        lock (_pendingAudioLocalCandidates)
                        {
                            _pendingAudioLocalCandidates.Add(cand.candidate);
                        }
                    }
                }
            };

            _audioPc.oniceconnectionstatechange += (state) =>
            {
                Logger.Info($"[SIPSorcery] Audio PC ICE state: {state}");
            };

            _audioPc.onconnectionstatechange += (state) =>
            {
                Logger.Info($"[SIPSorcery] Audio PC peer state: {state}");
            };

            // Add a dummy sendonly audio track so the SDP includes m=audio.
            // SIPSorcery's ICE agent doesn't perform connectivity checks for
            // data-channel-only PCs (m=application only). Adding a media section
            // forces the full ICE/DTLS transport initialization.
            var dummyAudioFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.audio, 0, "PCMU", 8000);
            var dummyAudioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio, false,
                new List<SDPAudioVideoMediaFormat> { dummyAudioFormat },
                MediaStreamStatusEnum.SendOnly);
            _audioPc.addTrack(dummyAudioTrack);
            Logger.Info("[SIPSorcery] Audio PC: added dummy audio track for ICE compatibility");

            // Set remote offer and create answer
            var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
            _audioPc.setRemoteDescription(offer);

            var answer = _audioPc.createAnswer(null);

            // Unlike the main PC, the audio PC (data-channel-only) NEEDS setLocalDescription
            // for the ICE agent to properly respond to STUN binding requests.
            // Main PC skips this due to SIPSorcery 8.x signalingState bug corrupting DTLS config,
            // but audio PC has no media tracks so the DTLS config issue doesn't apply.
            try
            {
                _audioPc.setLocalDescription(answer);
                Logger.Info("[SIPSorcery] Audio PC setLocalDescription(answer) OK");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SIPSorcery] Audio PC setLocalDescription failed (non-fatal): {ex.Message}");
            }

            var answerSdp = answer.sdp ?? "";

            // Log raw SDP for debugging compatibility issues
            Logger.Info($"[SIPSorcery] Audio PC raw answer SDP:\n{answerSdp}");

            // Fix actpass → active for RFC 5763 compliance (answerer must not use actpass)
            if (answerSdp.Contains("a=setup:actpass"))
                answerSdp = answerSdp.Replace("a=setup:actpass", "a=setup:active");

            // SIPSorcery may generate "DTLS/SCTP" instead of "UDP/DTLS/SCTP" for the
            // m=application line. libwebrtc (used by Unity WebRTC) requires "UDP/DTLS/SCTP".
            answerSdp = answerSdp.Replace("m=application 9 DTLS/SCTP", "m=application 9 UDP/DTLS/SCTP");

            // SIPSorcery includes "ice2" in ice-options which libwebrtc may not understand.
            // Replace with just "trickle" which is universally supported.
            answerSdp = answerSdp.Replace("a=ice-options:ice2,trickle", "a=ice-options:trickle");
            answerSdp = answerSdp.Replace("a=ice-options:ice2", "a=ice-options:trickle");

            // Remove "a=end-of-candidates" - RFC 8838 line not recognized by all libwebrtc versions
            answerSdp = System.Text.RegularExpressions.Regex.Replace(
                answerSdp, @"a=end-of-candidates\r?\n?", "");

            // Extract embedded a=candidate lines before stripping them from the SDP.
            // SIPSorcery uses vanilla ICE (candidates embedded in SDP), but libwebrtc's
            // data-channel-only SDP parser can choke on embedded candidates.
            // We extract them and send via trickle ICE (OnAudioIceCandidate) after the answer.
            var extractedCandidates = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                answerSdp, @"a=(candidate:[^\r\n]*)"))
            {
                extractedCandidates.Add(m.Groups[1].Value);
            }
            answerSdp = System.Text.RegularExpressions.Regex.Replace(
                answerSdp, @"a=candidate:[^\r\n]*\r?\n?", "");

            // SIPSorcery may use old sctpmap format instead of sctp-port.
            if (answerSdp.Contains("a=sctpmap:") && !answerSdp.Contains("a=sctp-port:"))
            {
                var sctpMapMatch = System.Text.RegularExpressions.Regex.Match(
                    answerSdp, @"a=sctpmap:(\d+)");
                if (sctpMapMatch.Success)
                {
                    var port = sctpMapMatch.Groups[1].Value;
                    answerSdp = answerSdp.Replace(sctpMapMatch.Value,
                        $"a=sctp-port:{port}\r\n{sctpMapMatch.Value}");
                }
            }

            Logger.Info($"[SIPSorcery] Audio PC fixed answer SDP ({answerSdp.Length} bytes):\n{answerSdp}");

            // Flush any ICE candidates that arrived before the audio PC was created
            lock (_pendingAudioIceCandidates)
            {
                if (_pendingAudioIceCandidates.Count > 0)
                {
                    Logger.Info($"[SIPSorcery] Flushing {_pendingAudioIceCandidates.Count} pending audio ICE candidates");
                    foreach (var cand in _pendingAudioIceCandidates)
                    {
                        try { AddAudioIceCandidateInternal(cand); }
                        catch (Exception ex) { Logger.Error($"[SIPSorcery] Flush audio ICE error: {ex.Message}"); }
                    }
                    _pendingAudioIceCandidates.Clear();
                }
            }

            // Extracted candidates are added to the local buffer (they may duplicate
            // onicecandidate events, but dedup happens at the ICE agent level).
            lock (_pendingAudioLocalCandidates)
            {
                foreach (var cand in extractedCandidates)
                {
                    if (!_pendingAudioLocalCandidates.Contains(cand))
                        _pendingAudioLocalCandidates.Add(cand);
                }
                Logger.Info($"[SIPSorcery] Audio PC: {_pendingAudioLocalCandidates.Count} local ICE candidates buffered (will flush after answer sent)");
            }

            return Task.FromResult(answerSdp);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Audio PC offer processing failed: {ex.Message}");
            return Task.FromResult("");
        }
    }

    /// <summary>
    /// Flush buffered server-side audio ICE candidates via OnAudioIceCandidate.
    /// Must be called AFTER the audio answer SDP has been sent to the client.
    /// </summary>
    public void FlushAudioLocalCandidates()
    {
        _audioAnswerDelivered = true;

        List<string> toSend;
        lock (_pendingAudioLocalCandidates)
        {
            toSend = new List<string>(_pendingAudioLocalCandidates);
            _pendingAudioLocalCandidates.Clear();
        }

        if (toSend.Count > 0)
        {
            Logger.Info($"[SIPSorcery] Flushing {toSend.Count} audio local ICE candidates");
            foreach (var cand in toSend)
            {
                Logger.Info($"[SIPSorcery] Audio PC trickle ICE: {cand.Substring(0, Math.Min(60, cand.Length))}...");
                OnAudioIceCandidate?.Invoke(cand);
            }
        }
    }

    /// <summary>
    /// Add ICE candidate for the dedicated audio PeerConnection.
    /// Buffers candidates if the audio PC hasn't been created yet (trickle ICE race).
    /// </summary>
    public void AddAudioIceCandidate(string candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return;

        if (_audioPc == null)
        {
            // Buffer: audio PC not created yet (audio_candidate arrived before audio_offer)
            lock (_pendingAudioIceCandidates)
            {
                _pendingAudioIceCandidates.Add(candidate);
                Logger.Info($"[SIPSorcery] Buffered audio ICE candidate (PC not ready, {_pendingAudioIceCandidates.Count} pending)");
            }
            return;
        }

        AddAudioIceCandidateInternal(candidate);
    }

    private void AddAudioIceCandidateInternal(string candidate)
    {
        try
        {
            var candStr = candidate.Trim();
            if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring(2);
            if (candStr.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = candStr.Substring("candidate:".Length);
            if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                candStr = "candidate:" + candStr;

            var init = new RTCIceCandidateInit { candidate = candStr, sdpMLineIndex = 0, sdpMid = "0" };
            _audioPc!.addIceCandidate(init);
            Logger.Info($"[SIPSorcery] Audio PC added ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SIPSorcery] Audio PC AddIceCandidate error: {ex.Message}");
        }
    }

    private static (int? pt, string? fmtp) TryGetCodecFromOfferSdp(string sdp, VideoCodec codec)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return (null, null);

        var codecName = codec.ToString();
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var matchingPts = new HashSet<int>();

        foreach (var line in lines)
        {
            if (!line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring("a=rtpmap:".Length);
            var sp = rest.IndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(rest.Substring(0, sp), out var candPt)) continue;
            var codecPart = rest.Substring(sp + 1);
            if (codecPart.IndexOf(codecName + "/", StringComparison.OrdinalIgnoreCase) >= 0)
                matchingPts.Add(candPt);
        }

        if (matchingPts.Count == 0) return (null, null);

        int chosenPt = matchingPts.First();
        string? fmtp = null;
        var needle = "a=fmtp:" + chosenPt + " ";
        foreach (var line in lines)
        {
            if (line.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            {
                fmtp = line.Substring(needle.Length).Trim();
                break;
            }
        }

        return (chosenPt, fmtp);
    }

    private static bool ContainsAnnexBStartCode(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) return true;
        if (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) return true;
        return false;
    }

    private static byte[] TryConvertAvccToAnnexB(byte[] avcc)
    {
        if (avcc.Length < 8) return avcc;

        try
        {
            byte[] outBuf = new byte[avcc.Length + 32];
            int outPos = 0;
            int pos = 0;

            while (pos + 4 <= avcc.Length)
            {
                int nalLen = (avcc[pos] << 24) | (avcc[pos + 1] << 16) | (avcc[pos + 2] << 8) | avcc[pos + 3];
                pos += 4;
                if (nalLen <= 0 || pos + nalLen > avcc.Length) return avcc;

                int needed = outPos + 4 + nalLen;
                if (needed > outBuf.Length)
                {
                    var newBuf = new byte[Math.Max(outBuf.Length * 2, needed)];
                    Buffer.BlockCopy(outBuf, 0, newBuf, 0, outPos);
                    outBuf = newBuf;
                }

                outBuf[outPos++] = 0;
                outBuf[outPos++] = 0;
                outBuf[outPos++] = 0;
                outBuf[outPos++] = 1;

                Buffer.BlockCopy(avcc, pos, outBuf, outPos, nalLen);
                outPos += nalLen;
                pos += nalLen;
            }

            if (outPos <= 0) return avcc;
            var res = new byte[outPos];
            Buffer.BlockCopy(outBuf, 0, res, 0, outPos);
            return res;
        }
        catch
        {
            return avcc;
        }
    }

    private string MergeFmtp(string defaultFmtp, string? negotiatedFmtp)
    {
        if (string.IsNullOrWhiteSpace(negotiatedFmtp)) return defaultFmtp;
        if (string.IsNullOrWhiteSpace(defaultFmtp)) return negotiatedFmtp;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Parse defaults
        foreach (var part in defaultFmtp.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) result[kv[0].Trim()] = kv[1].Trim();
            else result[kv[0].Trim()] = "";
        }

        // Merge negotiated (overwrites defaults)
        foreach (var part in negotiatedFmtp.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) result[kv[0].Trim()] = kv[1].Trim();
            else result[kv[0].Trim()] = "";
        }

        return string.Join(";", result.Select(x => string.IsNullOrEmpty(x.Value) ? x.Key : $"{x.Key}={x.Value}"));
    }

    private byte[] StripLeadingAud(byte[] au)
    {
        if (au.Length < 4) return au;

        int pos;
        if (au.Length >= 4 && au[0] == 0 && au[1] == 0 && au[2] == 0 && au[3] == 1) { pos = 4; }
        else if (au.Length >= 3 && au[0] == 0 && au[1] == 0 && au[2] == 1) { pos = 3; }
        else return au;

        if (pos >= au.Length) return au;

        int nalType;
        if (_negotiatedCodec == VideoCodec.H265)
        {
            // H265: type is in (header[0] >> 1) & 0x3F
            nalType = (au[pos] >> 1) & 0x3F;
            if (nalType != 35) return au; // H265 AUD is 35
        }
        else
        {
            // H264: type is in header[0] & 0x1F
            nalType = au[pos] & 0x1F;
            if (nalType != 9) return au; // H264 AUD is 9
        }

        // Find next start code to strip the AUD NAL
        for (int i = pos + 1; i + 3 < au.Length; i++)
        {
            if ((au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ||
                (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1))
            {
                var trimmed = new byte[au.Length - i];
                Buffer.BlockCopy(au, i, trimmed, 0, trimmed.Length);
                
                // For H265, log if we see VPS/SPS/PPS after stripping AUD to debug no-frame issue
                if (_negotiatedCodec == VideoCodec.H265)
                {
                    int nextPos = (trimmed[0] == 0 && trimmed[1] == 0 && trimmed[2] == 0 && trimmed[3] == 1) ? 4 : 3;
                    if (nextPos < trimmed.Length)
                    {
                        int nextType = (trimmed[nextPos] >> 1) & 0x3F;
                        if (nextType == 32 || nextType == 33 || nextType == 34)
                            Logger.Info($"[SIPSorcery] H265 Meta after AUD: Type={nextType} (32=VPS, 33=SPS, 34=PPS)");
                    }
                }
                
                return trimmed;
            }
        }

        return au;
    }

    private static bool IsPrivateV4(System.Net.IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    private static bool CandidateLineIsHostPrivateV4(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var s = line.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!s.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!s.Contains(" typ host ", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return false;
        var addr = parts[4];
        if (addr.Contains(':')) return false;
        if (!System.Net.IPAddress.TryParse(addr, out var ip)) return false;
        return IsPrivateV4(ip);
    }

    private static string FilterAnswerSdpIceCandidates(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return sdp;
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var kept = new List<string>(lines.Length);

        foreach (var raw in lines)
        {
            var l = raw ?? string.Empty;
            var t = l.TrimStart();

            if (t.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                if (!CandidateLineIsHostPrivateV4(t))
                    continue;
            }
            else if (t.StartsWith("a=end-of-candidates", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(l);
        }

        return string.Join("\r\n", kept);
    }
}
