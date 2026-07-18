#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D11;
using Org.BouncyCastle.Security;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Application.Security;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    /// <summary>
    /// Build RTCConfiguration for the host's own PeerConnections — STUN ONLY.
    /// Feeding TURN servers to SIPSorcery silently breaks its candidate gathering
    /// (observed 2026-07-11: with TURN entries present it never emitted a single
    /// srflx candidate, leaving only LAN/IPv6 host candidates — cross-network ICE
    /// died because the client could not learn the host's public address and thus
    /// never opened a coturn permission for it). TURN relaying is done by the
    /// CLIENT side only: the full STUN+TURN list still goes to the client via
    /// config_complete (TurnCredentialProvider.GetSessionIceServers), and one
    /// relayed side is sufficient — client-relay ↔ host-srflx pairs connect.
    /// </summary>
    private static RTCConfiguration BuildIceConfiguration()
    {
        var servers = new List<RTCIceServer>
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
            new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
        };

        Logger.Info($"[SIPSorcery] Using {servers.Count} ICE servers (STUN-only; TURN is client-side)");
        var cfg = new RTCConfiguration { iceServers = servers };

        // Phase 1 pairing: pin the Host's persistent DTLS cert so its WebRTC fingerprint stays
        // stable across sessions/restarts (precondition for reconnect-without-reprompt). No-op
        // when RequirePairing is OFF — legacy behaviour (SIPSorcery's own ephemeral per-connection
        // cert) is unchanged. RTCCertificate2 has no public ctor from an X509Certificate2; it must
        // be built from BouncyCastle types (confirmed by compiling against SIPSorcery 8.0.23):
        // Certificate via DotNetUtilities.FromX509Certificate, PrivateKey via the ECDSA key pair
        // (X509Certificate2.PrivateKey does NOT support ECDsa certs — SIPSorcery's own
        // DtlsUtils.LoadPrivateKeyResource throws NotSupportedException for them).
        if (PairingPolicy.RequirePairing)
        {
            try
            {
                var hostCert = HostDtlsCertificate.Instance.Certificate;
                using var ecdsaPrivateKey = hostCert.GetECDsaPrivateKey();
                if (ecdsaPrivateKey == null)
                    throw new InvalidOperationException("Host DTLS certificate has no ECDSA private key");

                var keyPair = DotNetUtilities.GetECDsaKeyPair(ecdsaPrivateKey);
                var pinnedCert = new RTCCertificate2
                {
                    Certificate = DotNetUtilities.FromX509Certificate(hostCert),
                    PrivateKey = keyPair.Private
                };
                cfg.certificates2 = new List<RTCCertificate2> { pinnedCert };
                Logger.Info("[SIPSorcery] Pairing: pinned host DTLS certificate applied to RTCConfiguration");
            }
            catch (Exception ex)
            {
                Logger.Error($"[SIPSorcery] Pairing: failed to pin host DTLS certificate ({ex.Message}) — " +
                             "falling back to SIPSorcery's ephemeral per-connection cert (fingerprint will " +
                             "NOT stay stable across sessions; reconnect-without-reprompt will not hold)");
            }
        }

        // DEBUG-only, local-admin-set forced-path flag (default OFF; no-op in release builds
        // — see DebugForcedIcePolicy for why this is fail-closed by construction, and the
        // documented gap: STUN-only servers can't actually satisfy a relay-only policy today).
        if (DebugForcedIcePolicy.ForceRelayOnly)
        {
            cfg.iceTransportPolicy = RTCIceTransportPolicy.relay;
            Logger.Warn("[SIPSorcery] DEBUG forced-relay ICE policy ACTIVE (iceTransportPolicy=relay)");
        }

        return cfg;
    }

    /// <summary>
    /// Process single SDP offer (with N m= sections), create N tracks, return single answer.
    /// When perTrackPc=true: only audio track on main PC; video tracks on per-monitor PCs.
    /// When perTrackPc=false: legacy single-PC behavior (unchanged).
    /// </summary>
    public Task<string> ProcessOfferAsync(string offerSdp, List<(int w, int h)> dimensions, bool perTrackPc = false)
    {
        _perTrackPcMode = perTrackPc;

        // 1. CLEAR previous connection state but PRESERVE TrackInfo list for SSRC persistence!
        // (CloseConnection disposes _mainPc, _audioDc, etc. but not the underlying encoders/SSRCs)
        CloseConnection();

        // Ensure fresh sync state for Every new connection/reconnect.
        // Clears RTP offsets and capture start time to prevent client jitter buffer inflation.
        ResetSyncState();

        // Parse negotiated codec payload type from offer
        var (negotiatedPt, negotiatedFmtp) = TryGetCodecFromOfferSdp(offerSdp, _negotiatedCodec);
        Logger.Info($"[SIPSorcery] Offer {_negotiatedCodec} pt={negotiatedPt ?? 96}, fmtp={negotiatedFmtp ?? "default"}, perTrackPc={perTrackPc}");

        // Log client fingerprint from offer for DTLS debugging
        foreach (var line in offerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] Client SDP: {line}");
        }

        // Create Main PeerConnection with STUN + optional TURN
        var cfg = BuildIceConfiguration();
        _mainPc = new RTCPeerConnection(cfg);
        NextIceGeneration("main");
        Logger.Info("[SIPSorcery] Main PC: PeerConnection created (with STUN/TURN)");

        // 2. Manage Video Tracks — only added to main PC in legacy mode
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

            // Device mapping logic (simplified for clarity)
            ID3D11Device? deviceForTrack = _sharedDevice;
            if (_deviceMappings.TryGetValue(i, out var mappedDevice)) deviceForTrack = mappedDevice;
            else if (_pendingDevices.TryGetValue(i, out var pendingDevice))
            {
                deviceForTrack = pendingDevice;
                _pendingDevices.Remove(i);
            }
            ti.Device = deviceForTrack;

            if (!perTrackPc)
            {
                // Legacy: add video tracks to main PC
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

                _mainPc.addTrack(track);
                Logger.Info($"[SIPSorcery] Main PC: Added video track {i}: {w}x{h} (device={deviceForTrack?.GetHashCode():X8}), SSRC={ti.Ssrc}");
            }
            else
            {
                // Per-track mode: video tracks will be on dedicated video PCs
                Logger.Info($"[SIPSorcery] Main PC: Track {i} ({w}x{h}) will use dedicated video PC (per-track mode)");
            }
        }

        // Initialize per-monitor pause state (all monitors active initially)
        _monitorPaused = new bool[dimensions.Count];

        // Add SendOnly audio track for RTP Opus (matches client's RecvOnly audio transceiver).
        // Audio goes through RTP/UDP on the same ICE connection — NOT SCTP,
        // so no head-of-line blocking from H.265 video DataChannel traffic.
        var opusFormat = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.audio, 111, "opus", 48000,
            channels: 2, fmtp: "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1");
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio, false,
            new List<SDPAudioVideoMediaFormat> { opusFormat },
            MediaStreamStatusEnum.SendOnly);
        _mainPc.addTrack(audioTrack);
        Logger.Info("[SIPSorcery] Main PC: Added SendOnly Opus audio track (RTP transport)");

        // Wire DataChannels on main PC.
        // In per-track mode: only cursor + audio DCs (video DCs go on individual video PCs).
        // In legacy mode: all DCs including h265video-* and h265video.
        _mainPc.ondatachannel += (dc) =>
        {
            Logger.Info($"[SIPSorcery] Main PC: DataChannel received: label={dc.label}, id={dc.id}");
            if (dc.label == "audio")
            {
                _audioDc = dc;
                _audioDc.onopen += () => Logger.Info("[SIPSorcery] Main PC: Audio DataChannel opened");
                _audioDc.onclose += () => { Logger.Info("[SIPSorcery] Main PC: Audio DataChannel closed"); _audioDc = null; };
                Logger.Info("[SIPSorcery] Main PC: Audio DataChannel wired for sending");
            }
            else if (dc.label == "cursor")
            {
                _cursorDc = dc;
                _cursorDc.onopen += () =>
                {
                    Logger.Info("[SIPSorcery] Main PC: Cursor DataChannel opened");
                };
                _cursorDc.onclose += () => { Logger.Info("[SIPSorcery] Main PC: Cursor DataChannel closed"); _cursorDc = null; };
                Logger.Info("[SIPSorcery] Main PC: Cursor DataChannel wired for sending");
            }
            else if (dc.label == "input")
            {
                _inputDc = dc;
                _inputDc.onopen += () => Logger.Info("[SIPSorcery] Main PC: Input DataChannel opened (client mouse/keyboard)");
                _inputDc.onclose += () => { Logger.Info("[SIPSorcery] Main PC: Input DataChannel closed"); _inputDc = null; };
                _inputDc.onmessage += (_, _, data) => OnInputReceived?.Invoke(data);
                Logger.Info("[SIPSorcery] Main PC: Input DataChannel wired for receiving");
            }
            else if (!perTrackPc && dc.label == "h265video")
            {
                // Legacy single-DC mode (backward compat with older clients)
                _h265VideoDcLegacy = dc;
                _h265VideoDcLegacy.onopen += () =>
                {
                    Logger.Info("[SIPSorcery] Main PC: H265 Video DataChannel opened (legacy single-DC, unreliable, unordered) - forcing keyframe");
                    RequestKeyframe(-1, force: true);
                };
                _h265VideoDcLegacy.onclose += () => { Logger.Info("[SIPSorcery] Main PC: H265 Video DataChannel closed (legacy)"); _h265VideoDcLegacy = null; };
                Logger.Info("[SIPSorcery] Main PC: H265 Video DataChannel wired (legacy single-DC mode)");
            }
            else if (!perTrackPc && dc.label.StartsWith("h265video-") && int.TryParse(dc.label.Substring("h265video-".Length), out int trackIdx))
            {
                // Legacy per-track DC mode on shared PC: each track has its own buffer
                lock (_h265VideoDcs) { _h265VideoDcs[trackIdx] = dc; }
                dc.onopen += () =>
                {
                    Logger.Info($"[SIPSorcery] Main PC: H265 Video DataChannel opened for track {trackIdx} (per-track, unreliable, unordered)");
                    RequestKeyframe(trackIdx, force: true);
                };
                int closedIdx = trackIdx; // capture for closure
                dc.onclose += () =>
                {
                    Logger.Info($"[SIPSorcery] Main PC: H265 Video DataChannel closed for track {closedIdx}");
                    lock (_h265VideoDcs) { _h265VideoDcs.Remove(closedIdx); }
                };
                Logger.Info($"[SIPSorcery] Main PC: H265 Video DataChannel wired for track {trackIdx}");
            }
            else if (perTrackPc && dc.label.StartsWith("h265video"))
            {
                // Per-track PC mode: capture h265video DCs from main PC as FALLBACK
                // If video PCs fail to connect (client doesn't respond with video_answer),
                // we can fall back to using these DCs on the main PC instead.
                int fallbackIdx = 0;
                if (dc.label.StartsWith("h265video-") && int.TryParse(dc.label.Substring("h265video-".Length), out int fi))
                    fallbackIdx = fi;
                lock (_perTrackFallbackDcs) { _perTrackFallbackDcs[fallbackIdx] = dc; }
                int capturedFallbackIdx = fallbackIdx;
                dc.onopen += () =>
                {
                    Logger.Info($"[SIPSorcery] Main PC: Fallback H265 DC opened for track {capturedFallbackIdx}");
                    if (_perTrackFallbackActive)
                        RequestKeyframe(capturedFallbackIdx, force: true);
                };
                dc.onclose += () =>
                {
                    Logger.Info($"[SIPSorcery] Main PC: Fallback H265 DC closed for track {capturedFallbackIdx}");
                    lock (_perTrackFallbackDcs) { _perTrackFallbackDcs.Remove(capturedFallbackIdx); }
                };
                Logger.Info($"[SIPSorcery] Main PC: Captured DC '{dc.label}' as fallback (video PCs preferred)");
            }
            else if (perTrackPc)
            {
                Logger.Info($"[SIPSorcery] Main PC: Ignoring DC '{dc.label}' in per-track mode (video DCs live on video PCs)");
            }
        };
        _hasAudioTrack = true;

        if (perTrackPc)
            Logger.Info("[SIPSorcery] Main PC: Waiting for cursor/audio DCs (video DCs on dedicated video PCs)");
        else
            Logger.Info("[SIPSorcery] Main PC: Waiting for audio/cursor/h265video DataChannels (per-track or legacy)");

        // ICE candidate forwarding
        _mainPc.onicecandidate += (cand) =>
        {
            if (cand != null && !string.IsNullOrEmpty(cand.candidate))
            {
                Logger.Info($"[SIPSorcery] Main PC: Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
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
        _mainPc.oniceconnectionstatechange += (state) =>
        {
            // Suppress duplicate "connected" events from ICE consent checks during active streaming
            if (state == RTCIceConnectionState.connected && _connected)
                return;

            Logger.Info($"[SIPSorcery] Main PC: ICE state: {state}");
            if (state == RTCIceConnectionState.connected)
            {
                Logger.Info("[SIPSorcery] Main PC: ICE CONNECTED - waiting for DTLS...");
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
        _mainPc.onconnectionstatechange += (state) =>
        {
            Logger.Info($"[SIPSorcery] Main PC: Peer state: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Logger.Info("[SIPSorcery] Main PC: DTLS CONNECTED - initializing encoders now");
                _connected = true;
                InitializeEncoders();
                InitializeAudio();
                OnAllTracksReady?.Invoke();
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                Logger.Error("[SIPSorcery] Main PC: DTLS FAILED - check certificate/fingerprint");
                _connected = false;
                OnConnectionFailed?.Invoke();
                OnFatalError?.Invoke("DTLS Handshake Failed");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                // Connection was closed unexpectedly (DTLS timeout, network issue, etc.)
                // Client should send a reconnect offer to recover
                Logger.Info("[SIPSorcery] Main PC: Peer state CLOSED unexpectedly - awaiting client reconnect offer");
                _connected = false;
            }
            else if (state == RTCPeerConnectionState.disconnected)
            {
                // Temporary disconnection - may recover automatically
                // Don't fire OnConnectionFailed yet, give ICE time to recover
                Logger.Info("[SIPSorcery] Main PC: Peer state DISCONNECTED - may recover, waiting...");
                _connected = false;
            }
        };

        _mainPc.onsignalingstatechange += () =>
        {
            Logger.Info($"[SIPSorcery] Main PC: Signaling state: {_mainPc.signalingState}");
        };

        // Set remote offer and create answer
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _mainPc.setRemoteDescription(offer);
        Logger.Info($"[SIPSorcery] Main PC: After setRemoteDescription: signalingState={_mainPc.signalingState}");

        var answer = _mainPc.createAnswer(null);
        Logger.Info($"[SIPSorcery] Main PC: After createAnswer: signalingState={_mainPc.signalingState}, answer.type={answer.type}");

        // DO NOT call setLocalDescription(answer) — SIPSorcery 8.x signalingState is broken
        // (shows "closed" after setRemoteDescription), so setLocalDescription misinterprets
        // the answer as an offer, setting state to "have_local_offer" and corrupting DTLS config.
        // createAnswer() already configures DTLS internals correctly.

        // Per-track mode: create dedicated video PCs (DC-only, one per monitor)
        if (perTrackPc)
        {
            SetupVideoPeerConnections(dimensions.Count, cfg);
        }

        _running = true;

        // Start stats logging
        _ = Task.Run(LogStatsAsync);

        // RFC 5763 actpass->active + SAVPF fixes + private-candidate filtering.
        // When signalingState works correctly (Android), SIPSorcery generates "active" natively.
        // When signalingState is broken (Unity Editor), it falls back to "actpass" which
        // libwebrtc rejects — NormalizeSipSorceryAnswerSdp (shared with the ICE-restart path
        // in SIPSorceryStreamer.IceRestart.cs) fixes this for RFC compliance.
        var answerSdp = NormalizeSipSorceryAnswerSdp(answer.sdp ?? "");

        // Log DTLS-critical SDP attributes for debugging
        foreach (var line in answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:"))
                Logger.Info($"[SIPSorcery] Main PC SDP: {line}");
        }

        Logger.Info($"[SIPSorcery] Main PC: Answer ready, {answerSdp.Length} bytes");
        return Task.FromResult(answerSdp);
    }

    /// <summary>
    /// Create dedicated DC-only PeerConnections for each video track (per-track mode).
    /// Each video PC carries one DataChannel for H.265 frames → independent SCTP association.
    /// NOTE: SIPSorcery's createDataChannel returns Task<RTCDataChannel> — stored after await in GetVideoPcOffersAsync.
    /// </summary>
    private void SetupVideoPeerConnections(int monitorCount, RTCConfiguration cfg)
    {
        for (int i = 0; i < monitorCount; i++)
        {
            int capturedIndex = i; // capture for closures
            var vpc = new RTCPeerConnection(cfg);
            _videoPcs[i] = vpc;
            NextIceGeneration($"video-{i}");

            // Wire events on video PC before createOffer (which triggers DC creation)
            vpc.oniceconnectionstatechange += (state) =>
            {
                Logger.Info($"[SIPSorcery] Video PC {capturedIndex}: ICE state: {state}");
            };

            vpc.onconnectionstatechange += (state) =>
            {
                Logger.Info($"[SIPSorcery] Video PC {capturedIndex}: Peer state: {state}");
            };

            vpc.onicecandidate += (cand) =>
            {
                if (cand != null && !string.IsNullOrEmpty(cand.candidate))
                {
                    Logger.Info($"[SIPSorcery] Video PC {capturedIndex}: Local ICE: {cand.candidate.Substring(0, Math.Min(50, cand.candidate.Length))}...");
                    OnVideoIceCandidate?.Invoke(capturedIndex, cand.candidate);
                }
                else
                {
                    OnVideoIceCandidate?.Invoke(capturedIndex, "end-of-candidates");
                }
            };

            // DC is created async in GetVideoPcOffersAsync (after createOffer/setLocalDescription)
            Logger.Info($"[SIPSorcery] Video PC {i}: Created (DC-only, awaiting offer generation)");
        }
    }

    /// <summary>
    /// Generate SDP offers from all video PeerConnections (per-track mode).
    /// Also creates the per-track DataChannels (createDataChannel returns Task in SIPSorcery 8.x).
    /// The client must answer each offer to establish its own SCTP association.
    /// </summary>
    public async Task<List<(int monitorIndex, string offerSdp)>> GetVideoPcOffersAsync()
    {
        var offers = new List<(int, string)>();
        foreach (var kvp in _videoPcs)
        {
            int idx = kvp.Key;
            var vpc = kvp.Value;

            // Create DC on the video PC (server-initiated, unreliable + unordered for low latency)
            // SIPSorcery 8.x: createDataChannel returns Task<RTCDataChannel>
            var dcInit = new RTCDataChannelInit { ordered = false, maxRetransmits = 0 };
            var dc = await vpc.createDataChannel($"h265video-{idx}", dcInit);
            _videoDcs[idx] = dc;

            int capturedIdx = idx; // capture for closures
            dc.onopen += () =>
            {
                Logger.Info($"[SIPSorcery] Video PC {capturedIdx}: h265video-{capturedIdx} DataChannel opened - requesting keyframe");
                RequestKeyframe(capturedIdx, force: true);
                // Signal that this monitor needs initial frame (capture will force encode even on idle desktop)
                OnInitialFrameNeeded?.Invoke(capturedIdx);
            };
            dc.onclose += () =>
            {
                Logger.Info($"[SIPSorcery] Video PC {capturedIdx}: h265video-{capturedIdx} DataChannel closed");
            };

            var offer = vpc.createOffer();
            await vpc.setLocalDescription(offer);
            var offerSdp = offer.sdp ?? "";
            Logger.Info($"[SIPSorcery] Video PC {idx}: Offer generated ({offerSdp.Length} bytes)");
            offers.Add((idx, offerSdp));
        }
        return offers;
    }

    /// <summary>
    /// Generate SDP offer for a SINGLE video PeerConnection (used during reconnect).
    /// Only touches the specified monitor's PC — does NOT affect other video PCs.
    /// </summary>
    public async Task<(int monitorIndex, string offerSdp)?> GetSingleVideoPcOfferAsync(int monitorIndex)
    {
        if (!_videoPcs.TryGetValue(monitorIndex, out var vpc))
        {
            Logger.Warn($"[SIPSorcery] GetSingleVideoPcOfferAsync: No video PC for monitor {monitorIndex}");
            return null;
        }

        // Create DC on the video PC (server-initiated, unreliable + unordered for low latency)
        var dcInit = new RTCDataChannelInit { ordered = false, maxRetransmits = 0 };
        var dc = await vpc.createDataChannel($"h265video-{monitorIndex}", dcInit);
        _videoDcs[monitorIndex] = dc;

        int capturedIdx = monitorIndex;
        dc.onopen += () =>
        {
            Logger.Info($"[SIPSorcery] Video PC {capturedIdx}: h265video-{capturedIdx} DataChannel opened - requesting keyframe");
            RequestKeyframe(capturedIdx, force: true);
            OnInitialFrameNeeded?.Invoke(capturedIdx);
        };
        dc.onclose += () =>
        {
            Logger.Info($"[SIPSorcery] Video PC {capturedIdx}: h265video-{capturedIdx} DataChannel closed");
        };

        var offer = vpc.createOffer();
        await vpc.setLocalDescription(offer);
        var offerSdp = offer.sdp ?? "";
        Logger.Info($"[SIPSorcery] Video PC {monitorIndex}: Offer generated ({offerSdp.Length} bytes)");
        return (monitorIndex, offerSdp);
    }

    /// <summary>
    /// Close and recreate a single video PC for reconnection (per-track mode).
    /// The caller must call GetVideoPcOffersAsync() after this to get the new offer SDP.
    /// </summary>
    public void RecreateVideoPc(int monitorIndex)
    {
        lock (_lock)
        {
            // Close old video PC
            if (_videoPcs.TryGetValue(monitorIndex, out var oldVpc))
            {
                try { oldVpc.close(); } catch { }
                _videoPcs.TryRemove(monitorIndex, out _);
                Logger.Info($"[SIPSorcery] Video PC {monitorIndex}: Closed for reconnect");
            }
            _videoDcs.TryRemove(monitorIndex, out _);

            // Create new video PC with centralized ICE config
            var cfg = BuildIceConfiguration();

            int capturedIndex = monitorIndex;
            var vpc = new RTCPeerConnection(cfg);
            _videoPcs[monitorIndex] = vpc;
            NextIceGeneration($"video-{monitorIndex}");

            vpc.oniceconnectionstatechange += (state) =>
                Logger.Info($"[SIPSorcery] Video PC {capturedIndex}: ICE state: {state}");
            vpc.onconnectionstatechange += (state) =>
                Logger.Info($"[SIPSorcery] Video PC {capturedIndex}: Peer state: {state}");
            vpc.onicecandidate += (cand) =>
            {
                if (cand != null && !string.IsNullOrEmpty(cand.candidate))
                    OnVideoIceCandidate?.Invoke(capturedIndex, cand.candidate);
                else
                    OnVideoIceCandidate?.Invoke(capturedIndex, "end-of-candidates");
            };
            Logger.Info($"[SIPSorcery] Video PC {monitorIndex}: Recreated for reconnect");
        }
    }

    /// <summary>
    /// Apply the client's SDP answer to a video PeerConnection (per-track mode).
    /// </summary>
    public async Task SetVideoAnswerAsync(int monitorIndex, string answerSdp)
    {
        if (_videoPcs.TryGetValue(monitorIndex, out var vpc))
        {
            var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
            var result = vpc.setRemoteDescription(answer);
            Logger.Info($"[SIPSorcery] Video PC {monitorIndex}: setRemoteDescription result={result}");
        }
        else
        {
            Logger.Warn($"[SIPSorcery] Video PC {monitorIndex}: SetVideoAnswerAsync — PC not found");
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Add a remote ICE candidate to a video PeerConnection (per-track mode).
    /// </summary>
    public void AddVideoIceCandidate(int monitorIndex, string candidate)
    {
        if (_videoPcs.TryGetValue(monitorIndex, out var vpc))
        {
            try
            {
                var candStr = candidate.Trim();
                if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
                    candStr = candStr.Substring(2);
                if (!candStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                    candStr = "candidate:" + candStr;

                vpc.addIceCandidate(new RTCIceCandidateInit { candidate = candStr });
                Logger.Info($"[SIPSorcery] Video PC {monitorIndex}: Added remote ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
            }
            catch (Exception ex)
            {
                Logger.Error($"[SIPSorcery] Video PC {monitorIndex}: AddVideoIceCandidate error: {ex.Message}");
            }
        }
        else
        {
            Logger.Warn($"[SIPSorcery] Video PC {monitorIndex}: AddVideoIceCandidate — PC not found");
        }
    }

    public void AddIceCandidate(string candidate, string? mid = null)
    {
        if (_mainPc == null || string.IsNullOrEmpty(candidate)) return;

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
            _mainPc.addIceCandidate(init);
            Logger.Info($"[SIPSorcery] Main PC: Added remote ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
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
    /// DEPRECATED: Audio now goes through the main PC as an RTP track.
    /// This method is kept for backwards compatibility with older clients
    /// that still send audio_offer. Returns empty string to gracefully decline.
    /// </summary>
    public Task<string> ProcessAudioOfferAsync(string offerSdp)
    {
        Logger.Info("[SIPSorcery] Audio offer received but audio now uses main PC RTP track. Ignoring separate Audio PC.");
        return Task.FromResult("");

        // Legacy code below — kept for reference
        #pragma warning disable CS0162
        try
        {
            // Close previous audio PC if any
            try { _audioPc?.close(); } catch { }
            _audioPc = null;

            var config = BuildIceConfiguration();
            _audioPc = new RTCPeerConnection(config);
            // No DataChannel on Audio PC — SCTP is broken on Unity WebRTC for dedicated PCs.
            // Audio goes through RTP (Opus track) via ICE/DTLS/UDP instead.

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

            // Add a REAL Opus sendonly audio track (48kHz, 2ch).
            // This serves dual purpose:
            // 1. Forces ICE/DTLS transport initialization (SIPSorcery needs m=audio)
            // 2. Provides actual RTP transport for Opus audio — bypasses SCTP entirely
            //    RTP goes through ICE/DTLS/UDP directly, no SCTP head-of-line blocking
            var opusFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.audio, 111, "opus", 48000,
                channels: 2, fmtp: "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1");
            var audioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio, false,
                new List<SDPAudioVideoMediaFormat> { opusFormat },
                MediaStreamStatusEnum.SendOnly);
            _audioPc.addTrack(audioTrack);
            Logger.Info("[SIPSorcery] Audio PC: added Opus audio track (48kHz stereo, RTP transport)");

            // Set remote offer and create answer
            var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
            _audioPc.setRemoteDescription(offer);

            var answer = _audioPc.createAnswer(null);

            // DO NOT call setLocalDescription(answer) — same SIPSorcery 8.x bug as main PC.
            // signalingState shows "closed" after setRemoteDescription, so setLocalDescription
            // corrupts ICE credentials internally, causing ICE connectivity checks to fail
            // (client receives correct SDP but server sends STUN requests with different creds).
            // createAnswer() already configures DTLS/ICE internals correctly.

            var answerSdp = answer.sdp ?? "";

            // Log raw SDP for debugging compatibility issues
            Logger.Info($"[SIPSorcery] Audio PC raw answer SDP:\n{answerSdp}");

            // Fix actpass → active for RFC 5763 compliance (answerer must not use actpass)
            if (answerSdp.Contains("a=setup:actpass"))
                answerSdp = answerSdp.Replace("a=setup:actpass", "a=setup:active");

            // SIPSorcery generates "UDP/TLS/RTP/SAVP" but libwebrtc requires "SAVPF" (with feedback).
            // Without this fix, DTLS handshake fails because libwebrtc rejects non-SAVPF profiles.
            if (!answerSdp.Contains("SAVPF"))
                answerSdp = answerSdp.Replace("SAVP", "SAVPF");

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
        #pragma warning restore CS0162
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
