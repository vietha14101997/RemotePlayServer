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
        if (_running)
        {
            Logger.Info("[SIPSorcery] Closing existing connection for reconnect...");
            CloseConnection();
        }

        Logger.Info($"[SIPSorcery] Processing offer for {dimensions.Count} monitors");

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

        // Create N video tracks - one per monitor
        for (int i = 0; i < dimensions.Count; i++)
        {
            var (w, h) = dimensions[i];

            // Negotiated codec format (H264, H265, etc.)
            string defaultFmtp = _negotiatedCodec == VideoCodec.H264
                ? "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f"
                : ""; // H265 usually doesn't need complex FMTP for base support

            var codecFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                id: negotiatedPt ?? (96 + i),
                name: _negotiatedCodec.ToString(),
                clockRate: 90000,
                channels: 0,
                fmtp: string.IsNullOrWhiteSpace(negotiatedFmtp) ? defaultFmtp : negotiatedFmtp);

            var track = new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                isRemote: false,
                capabilities: new List<SDPAudioVideoMediaFormat> { codecFormat },
                streamStatus: MediaStreamStatusEnum.SendOnly);

            _pc.addTrack(track);

            // Get device for track - priority: permanent mappings > pending > shared
            ID3D11Device? deviceForTrack = _sharedDevice;
            if (_deviceMappings.TryGetValue(i, out var mappedDevice))
            {
                // Use permanent mapping (survives reconnection)
                deviceForTrack = mappedDevice;
            }
            else if (_pendingDevices.TryGetValue(i, out var pendingDevice))
            {
                deviceForTrack = pendingDevice;
                _pendingDevices.Remove(i);
            }

            _tracks.Add(new TrackInfo
            {
                Index = i,
                Track = track,
                Mid = i.ToString(),
                Width = w,
                Height = h,
                Device = deviceForTrack
            });

            Logger.Info($"[SIPSorcery] Added track {i}: {w}x{h} (device={deviceForTrack?.GetHashCode():X8})");
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
                _cursorDc.onopen += () => Logger.Info("[SIPSorcery] Cursor DataChannel opened");
                _cursorDc.onclose += () => { Logger.Info("[SIPSorcery] Cursor DataChannel closed"); _cursorDc = null; };
                Logger.Info("[SIPSorcery] Cursor DataChannel wired for sending");
            }
        };
        _hasAudioTrack = true;
        Logger.Info("[SIPSorcery] Waiting for client audio/cursor DataChannels");

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
