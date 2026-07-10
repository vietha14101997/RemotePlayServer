#nullable enable
using System;
using System.Threading.Tasks;
using SIPSorcery.Net;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Streaming;

public partial class SIPSorceryStreamer
{
    /// <summary>
    /// Apply a mid-session ICE-restart offer to the LIVE main PeerConnection (Phase 5). The
    /// F11 DTLS-fingerprint MITM guard is enforced by the caller (PhaseProtocolHandler) BEFORE
    /// this is invoked — this method assumes the offer has already been verified as coming from
    /// the same DTLS peer as the current live session.
    ///
    /// Unlike <see cref="ProcessOfferAsync"/>, this deliberately does NOT call CloseConnection(),
    /// ResetSyncState(), or re-create tracks/DataChannels/encoders — the whole point of an ICE
    /// restart (vs. Phase2.cs's restart_phase2 full teardown) is that only the ICE transport
    /// re-negotiates (new ice-ufrag/ice-pwd, fresh candidate gathering); encoder pipeline, RTP
    /// sequence/SSRC state, and DataChannels stay untouched.
    ///
    /// Per Phase-0 spike gate G7 (PASS with caveat): SIPSorcery's setRemoteDescription+
    /// createAnswer accept a remote ICE-restart offer on a live PeerConnection. SIPSorcery does
    /// NOT itself rotate its own ICE ufrag on this path (RestartIce() is a NotImplementedException
    /// in SIPSorcery 8.x) — that is acceptable because Android is always the offerer (F10 glare
    /// avoidance): the host only ever answers a restart, it never has to initiate one.
    /// </summary>
    public Task<string> ProcessIceRestartOfferAsync(string offerSdp)
    {
        if (_mainPc == null)
        {
            Logger.Error("[SIPSorcery] ProcessIceRestartOfferAsync: no live main PC to restart");
            return Task.FromResult("");
        }

        Logger.Info($"[SIPSorcery] Main PC: applying ICE-restart offer ({offerSdp.Length} bytes) — encoder/session stays alive");

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        _mainPc.setRemoteDescription(offer);
        Logger.Info($"[SIPSorcery] Main PC: ICE-restart setRemoteDescription done, signalingState={_mainPc.signalingState}");

        var answer = _mainPc.createAnswer(null);
        Logger.Info($"[SIPSorcery] Main PC: ICE-restart createAnswer done, signalingState={_mainPc.signalingState}");

        // Same DO-NOT-call-setLocalDescription caveat as the initial offer path in
        // SIPSorceryStreamer.Setup.cs (SIPSorcery 8.x signalingState bug — createAnswer()
        // already configures the DTLS/ICE internals correctly on its own).
        var answerSdp = NormalizeSipSorceryAnswerSdp(answer.sdp ?? "");

        foreach (var line in answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=fingerprint:") || line.StartsWith("a=setup:") || line.StartsWith("a=ice-ufrag:"))
                Logger.Info($"[SIPSorcery] Main PC ICE-restart answer SDP: {line}");
        }

        Logger.Info($"[SIPSorcery] Main PC: ICE-restart answer ready, {answerSdp.Length} bytes");
        return Task.FromResult(answerSdp);
    }

    /// <summary>
    /// Apply the RFC 5763 (actpass→active) + SAVPF SDP fixes SIPSorcery's createAnswer() needs
    /// for libwebrtc compatibility, plus private-candidate filtering. Shared by the initial
    /// offer path (<see cref="ProcessOfferAsync"/> in SIPSorceryStreamer.Setup.cs) and the
    /// ICE-restart path above — the same createAnswer() quirks apply whether or not ICE
    /// credentials changed.
    /// </summary>
    private static string NormalizeSipSorceryAnswerSdp(string answerSdp)
    {
        // RFC 5763: Answerer MUST use "active" or "passive", NOT "actpass".
        if (answerSdp.Contains("a=setup:actpass"))
        {
            answerSdp = answerSdp.Replace("a=setup:actpass", "a=setup:active");
            Logger.Info("[SIPSorcery] SDP: fixed actpass -> active in answer (RFC 5763)");
        }

        if (!answerSdp.Contains("SAVPF"))
            answerSdp = answerSdp.Replace("SAVP", "SAVPF");

        return FilterAnswerSdpIceCandidates(answerSdp);
    }
}
