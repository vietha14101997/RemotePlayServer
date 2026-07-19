using RemotePlayServer.Infrastructure.Capture;

namespace RemotePlayServer.Tests;

/// <summary>
/// Regression tests locking the exact truth table of the desktop idle / frame-change
/// decision extracted from PerMonitorCapture. These assert the CURRENT behavior so the
/// pure extraction cannot silently drift from the original inline logic.
/// </summary>
public class FrameChangeDecisionTests
{
    // ==================== Idle (no change) ====================

    [Fact]
    public void Evaluate_Idle_BothSignalsZero_NotChanged()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 0,
            totalMetadataBufferSize: 0,
            initialFrameSent: true,
            inputForceFrames: 0);

        Assert.False(r.DesktopChanged);
        Assert.False(r.ConsumeInputForceFrame);
    }

    // ==================== Content change via each signal ====================

    [Fact]
    public void Evaluate_LastPresentTimeNonZero_Changed()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 12345,
            totalMetadataBufferSize: 0,
            initialFrameSent: true,
            inputForceFrames: 0);

        Assert.True(r.DesktopChanged);
        Assert.False(r.ConsumeInputForceFrame);
    }

    [Fact]
    public void Evaluate_MetadataBufferNonZero_Changed()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 0,
            totalMetadataBufferSize: 64,
            initialFrameSent: true,
            inputForceFrames: 0);

        Assert.True(r.DesktopChanged);
        Assert.False(r.ConsumeInputForceFrame);
    }

    // ==================== Initial-frame override ====================

    [Fact]
    public void Evaluate_InitialFrameNotSent_ForcesChangeEvenWhenIdle()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 0,
            totalMetadataBufferSize: 0,
            initialFrameSent: false,
            inputForceFrames: 0);

        Assert.True(r.DesktopChanged);
        // Initial-frame override must NOT consume an input-force frame.
        Assert.False(r.ConsumeInputForceFrame);
    }

    // ==================== Input-force override + consume ====================

    [Fact]
    public void Evaluate_InputForceFrames_ForcesChangeAndConsumes()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 0,
            totalMetadataBufferSize: 0,
            initialFrameSent: true,
            inputForceFrames: 3);

        Assert.True(r.DesktopChanged);
        Assert.True(r.ConsumeInputForceFrame);
    }

    [Fact]
    public void Evaluate_InputForceZero_DoesNotConsume()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 0,
            totalMetadataBufferSize: 0,
            initialFrameSent: true,
            inputForceFrames: 0);

        Assert.False(r.ConsumeInputForceFrame);
    }

    // ==================== Full-motion (gaming path safe by construction) ====================

    [Fact]
    public void Evaluate_FullMotion_AlwaysChanged_NoConsumeWhenNoInput()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 999999,
            totalMetadataBufferSize: 4096,
            initialFrameSent: true,
            inputForceFrames: 0);

        Assert.True(r.DesktopChanged);
        Assert.False(r.ConsumeInputForceFrame);
    }

    // ==================== Combined overrides ====================

    [Fact]
    public void Evaluate_ChangedAndInputForce_ConsumesInputForce()
    {
        // Desktop already changed AND input is forcing: still consumes an input-force frame
        // (mirrors original inline logic where InputForceFrames-- runs whenever >0).
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 100,
            totalMetadataBufferSize: 0,
            initialFrameSent: true,
            inputForceFrames: 5);

        Assert.True(r.DesktopChanged);
        Assert.True(r.ConsumeInputForceFrame);
    }

    [Fact]
    public void Evaluate_NotSentAndInputForce_ConsumesInputForce()
    {
        var r = FrameChangeDecision.Evaluate(
            lastPresentTime: 0,
            totalMetadataBufferSize: 0,
            initialFrameSent: false,
            inputForceFrames: 2);

        Assert.True(r.DesktopChanged);
        Assert.True(r.ConsumeInputForceFrame);
    }
}
