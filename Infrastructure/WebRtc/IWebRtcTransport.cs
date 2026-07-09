#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RemotePlayServer.Infrastructure.WebRtc;

// ============================================================================
// Phase 4 (4a) — WebRTC transport abstraction.
//
// Lets StreamerOrchestration be transport-agnostic: SIPSorcery (existing,
// fallback) and libdatachannel/DataChannelDotnet (new) are two implementations
// selected by the WEBRTC_TRANSPORT flag. Interface derived from the ACTUAL
// SIPSorceryStreamer surface — see plans/.../reports/analysis-260710-0214-
// sipsorcery-transport-surface.md.
//
// STATUS: 4a scaffold authored on macOS — RemotePlayServer is net9.0-windows
// (WPF) + native DLLs and CANNOT compile on macOS. **VERIFY/ADJUST ON THE
// WINDOWS VM (compile-in-the-loop).** Signatures capture only what the code
// uses today; refine as the SIPSorceryTransport wrapper is written.
//
// Design notes (from the surface analysis):
//  - Model = ONE peer connection per instance; the streamer owns a collection
//    (_mainPc + per-track _videoPcs). The dead _audioPc is not modeled.
//  - No RestartIce(): today reconnect = close + recreate PC. Omitted here.
//  - bufferedAmount is POLL-based (synchronous ulong), no threshold callbacks.
//  - Answer SDP munging (actpass->active, SAVP->SAVPF, candidate filter) stays
//    in the streamer, operating on the raw string returned by CreateAnswer().
//  - main PC deliberately skips setLocalDescription (SIPSorcery 8.x bug); video
//    PCs call it. Kept asymmetric via SetLocalDescriptionAsync (video-only).
// ============================================================================

// --- Value types -----------------------------------------------------------

public sealed record WebRtcIceServer(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

public sealed record WebRtcConfiguration(IReadOnlyList<WebRtcIceServer> IceServers);

public sealed record WebRtcDataChannelInit(bool Ordered = true, int? MaxRetransmits = null);

public sealed record WebRtcVideoTrackSpec(int PayloadType, string CodecName, int ClockRate, string Fmtp, uint Ssrc);

public sealed record WebRtcAudioTrackSpec(int PayloadType, string CodecName, int ClockRate, int Channels, string Fmtp);

public enum WebRtcSdpType { Offer, Answer }

public enum WebRtcConnectionState { New, Connecting, Connected, Disconnected, Failed, Closed }

public enum WebRtcIceState { New, Checking, Connected, Completed, Disconnected, Failed, Closed }

public enum WebRtcDataChannelState { Connecting, Open, Closing, Closed }

// --- Factory ---------------------------------------------------------------

/// <summary>
/// Creates transport peer connections. The concrete factory (SIPSorcery vs
/// libdatachannel) is selected by ServerService from WEBRTC_TRANSPORT.
/// </summary>
public interface IWebRtcTransportFactory
{
    /// <param name="config">null ⇒ a bare warm-up PC (PreWarmDtls) with no ICE servers.</param>
    IWebRtcPeerConnection CreatePeerConnection(WebRtcConfiguration? config);
}

// --- Peer connection -------------------------------------------------------

public interface IWebRtcPeerConnection : IDisposable
{
    // Tracks. AddVideoTrack returns the track index used by SendVideoRtp
    // (SIPSorcery: VideoStreamList[index]). NOTE(4b/scope): the libdatachannel
    // impl targets video-over-DataChannel per the plan's KISS constraint and
    // may NOT implement the RTP video path — see analysis UQ1. AddAudioTrack is
    // the SRTP audio fallback (primary audio rides the `audio` DataChannel).
    int AddVideoTrack(WebRtcVideoTrackSpec spec);
    void AddAudioTrack(WebRtcAudioTrackSpec spec);

    // DataChannels (server-initiated). SIPSorcery 8.x is async; libdatachannel
    // is sync — wrap both behind the Task.
    Task<IWebRtcDataChannel> CreateDataChannelAsync(string label, WebRtcDataChannelInit init);

    // SDP. CreateAnswer returns the raw SDP string for streamer-side munging.
    // SetLocalDescriptionAsync is video-PC only (main PC skips it, 8.x bug).
    void SetRemoteDescription(WebRtcSdpType type, string sdp);
    string CreateAnswer();
    string CreateOffer();
    Task SetLocalDescriptionAsync(WebRtcSdpType type, string sdp);

    // ICE. OnIceCandidate arg null/"" ⇒ end-of-candidates.
    void AddIceCandidate(string candidate, string? sdpMid = null, int sdpMLineIndex = 0);
    event Action<string?>? OnIceCandidate;

    // Media send. SendVideoRtp preserves the manual sequence-number multi-track
    // path; SendAudioRtp maps to pc.SendAudio.
    void SendVideoRtp(int trackIndex, byte[] payload, uint rtpTimestamp, int markerBit, int payloadType, ushort sequenceNumber);
    void SendAudioRtp(uint durationRtpUnits, byte[] payload);

    // State.
    WebRtcConnectionState ConnectionState { get; }
    string? ConnectedRemoteIp { get; } // selected remote address, for ICE-type detection

    // Events.
    event Action<IWebRtcDataChannel>? OnDataChannel;              // client-initiated (main PC ondatachannel)
    event Action<WebRtcConnectionState>? OnConnectionStateChange;
    event Action<WebRtcIceState>? OnIceConnectionStateChange;

    void Close();
}

// --- Data channel ----------------------------------------------------------

public interface IWebRtcDataChannel
{
    string Label { get; }
    WebRtcDataChannelState ReadyState { get; }

    /// <summary>Synchronous SCTP send-buffer depth in bytes. Polled by the ABR
    /// congestion logic (FrameSending) — see analysis §3. CRITICAL for parity.</summary>
    ulong BufferedAmount { get; }

    void Send(byte[] data);

    event Action? OnOpen;
    event Action? OnClose;
    event Action<byte[]>? OnMessage; // simplified from SIPSorcery (dc, proto, byte[])

    void Close();
}
