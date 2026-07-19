#nullable enable

namespace RemotePlayServer.Infrastructure.Capture;

/// <summary>
/// Result of a frame-change evaluation: whether the desktop content changed
/// (i.e. should be encoded/sent) and whether an input-force frame was consumed
/// by this decision (so the caller can decrement its counter).
/// </summary>
public readonly struct FrameChangeResult
{
    /// <summary>True when the frame should be treated as changed (encode + send).</summary>
    public readonly bool DesktopChanged;

    /// <summary>True when this decision consumed one input-force frame; caller must decrement.</summary>
    public readonly bool ConsumeInputForceFrame;

    public FrameChangeResult(bool desktopChanged, bool consumeInputForceFrame)
    {
        DesktopChanged = desktopChanged;
        ConsumeInputForceFrame = consumeInputForceFrame;
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
/// This is a static, allocation-free helper (no D3D/DXGI types) so it is fully unit-testable
/// and imposes no cost on the capture hot loop. Side effects (idle debounce, idle-changed
/// events, counter mutation) stay in the capture loop — this unit only computes the decision.
/// </summary>
public static class FrameChangeDecision
{
    /// <summary>
    /// Evaluate whether the current DXGI frame represents a desktop change.
    /// Precedence mirrors the original inline logic exactly:
    /// raw signal → force-true if not yet sent → force-true + consume if input forcing.
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

        return new FrameChangeResult(desktopChanged, consumeInputForceFrame);
    }
}
