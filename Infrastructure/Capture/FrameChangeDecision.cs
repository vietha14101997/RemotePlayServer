#nullable enable

namespace RemotePlayServer.Infrastructure.Capture;

/// <summary>
/// Result of a frame-change evaluation: whether the desktop content changed
/// (i.e. should be encoded/sent), whether an input-force frame was consumed
/// by this decision (so the caller can decrement its counter), and whether
/// the change was large enough to look like a "scene change" (tab switch,
/// window resize, dialog open) — in which case the encoder should be
/// forced to emit a fresh IDR instead of a P-frame.
/// </summary>
public readonly struct FrameChangeResult
{
    /// <summary>True when the frame should be treated as changed (encode + send).</summary>
    public readonly bool DesktopChanged;

    /// <summary>True when this decision consumed one input-force frame; caller must decrement.</summary>
    public readonly bool ConsumeInputForceFrame;

    /// <summary>
    /// True when the change is large enough to look like a scene change
    /// (tab switch / window resize / dialog open). When true, the caller
    /// should request an IDR for the next encode. False on small motion
    /// (cursor move, scrolling) where a P-frame is sufficient.
    /// </summary>
    public readonly bool IsSceneChange;

    public FrameChangeResult(bool desktopChanged, bool consumeInputForceFrame, bool isSceneChange)
    {
        DesktopChanged = desktopChanged;
        ConsumeInputForceFrame = consumeInputForceFrame;
        IsSceneChange = isSceneChange;
    }
}

/// <summary>
/// Pure desktop-idle / frame-change decision extracted from the capture hot loop.
///
/// DXGI reports whether the desktop actually changed via <c>LastPresentTime</c> and
/// <c>TotalMetadataBufferSize</c>. When both are zero the desktop is idle (user reading,
/// no mouse movement) and the encode/send can be skipped — the primary thermal/bandwidth
/// optimization. Two overrides force a frame through regardless:
///   1. Initial frame not yet sent → always send so the client gets immediate content.
///   2. Client input in flight (<c>InputForceFrames</c>) → send so visual feedback is captured.
///
/// Full-motion content always sets <c>LastPresentTime</c>/<c>TotalMetadataBufferSize</c>,
/// so it is never throttled here (gaming path safe by construction).
///
/// Additionally, a "scene change" is detected when the dirty metadata buffer is large
/// (>= 25% of the monitor area). This signals a tab switch / window open / dialog pop,
/// and the caller should force the encoder to produce an IDR — otherwise the client
/// sees P-frames referencing the OLD keyframe and reconstructs "old background + new
/// video area" until the next IDR (up to GOP seconds).
///
/// This is a static, allocation-free helper (no D3D/DXGI types) so it is fully unit-testable
/// and imposes no cost on the capture hot loop. Side effects (idle debounce, idle-changed
/// events, counter mutation) stay in the capture loop — this unit only computes the decision.
/// </summary>
public static class FrameChangeDecision
{
    /// <summary>
    /// Scene-change heuristic: dirty metadata covers >= ~3% of monitor area.
    /// Empirically tuned for tab switches (Messenger -> YouTube) where the desktop
    /// layout changes. Lowered from 500KB (25% area) to 50KB (~3% area) so that
    /// YouTube tab switches — which often have small dirty metadata because only
    /// the video player region changes — still trigger IDR.
    ///
    /// A single DXGI dirty rect header is ~16 bytes plus the rect itself, so 50KB
    /// corresponds to several thousand dirty rectangles — well beyond normal
    /// cursor movement / scrolling but small enough to catch tab content swaps.
    /// </summary>
    public const uint SceneChangeMetadataBytesThreshold = 50_000;

    /// <summary>
    /// Evaluate whether the current DXGI frame represents a desktop change.
    /// Precedence mirrors the original inline logic exactly:
    /// raw signal → force-true if not yet sent → force-true + consume if input forcing.
    /// Scene-change is reported only when the change is large (heuristic on metadata size).
    /// </summary>
    /// <param name="lastPresentTime">DXGI <c>OutduplFrameInfo.LastPresentTime</c> (0 = no present).</param>
    /// <param name="totalMetadataBufferSize">DXGI <c>OutduplFrameInfo.TotalMetadataBufferSize</c> (0 = no dirty/move regions).</param>
    /// <param name="initialFrameSent">Whether the first frame has been confirmed sent for this monitor.</param>
    /// <param name="inputForceFrames">Remaining input-driven forced frames (>0 forces + consumes one).</param>
    public static FrameChangeResult Evaluate(
        long lastPresentTime,
        uint totalMetadataBufferSize,
        bool initialFrameSent,
        int inputForceFrames)
    {
        bool desktopChanged = lastPresentTime != 0 || totalMetadataBufferSize > 0;

        // Force frames through until the streamer confirms the first frame was SENT.
        if (!initialFrameSent)
            desktopChanged = true;

        // Force capture while the client is actively sending input.
        bool consumeInputForceFrame = inputForceFrames > 0;
        if (consumeInputForceFrame)
            desktopChanged = true;

        // Scene-change: dirty metadata covers a large portion of the screen.
        // This usually indicates a tab switch / window resize / dialog popup.
        // We request an IDR in that case so the next encode is not a P-frame
        // referencing stale content (the original tab-switch bug).
        bool isSceneChange = totalMetadataBufferSize >= SceneChangeMetadataBytesThreshold;

        return new FrameChangeResult(desktopChanged, consumeInputForceFrame, isSceneChange);
    }
}
