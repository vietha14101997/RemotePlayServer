using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Tests.Helpers;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for the pure host-side adaptive-FPS controller. A fresh controller has
/// _lastChangeTime = DateTime.MinValue so the first Decide() always clears cooldown.
/// Cooldown is bypassed between calls via reflection (same pattern as AdaptiveBitrateControllerTests).
/// </summary>
public class AdaptiveFpsControllerTests
{
    private const int WindowMs = 1000;

    /// <summary>Set _lastChangeTime to far past so cooldown never blocks.</summary>
    private static void BypassCooldown(AdaptiveFpsController c) =>
        ReflectionHelper.SetPrivateField(c, "_lastChangeTime", DateTime.MinValue);

    // ==================== Ramp down on static content ====================

    [Fact]
    public void Decide_StaticContent_RampsDown()
    {
        var c = new AdaptiveFpsController();
        // 60fps cap, only 10 active fps → 10 < 0.5*60=30 → ramp down to next lower bucket (45)
        var d = c.Decide(activeFramesInWindow: 10, windowMs: WindowMs, currentTargetFps: 60, floorFps: 20, ceilFps: 60);

        Assert.True(d.Changed);
        Assert.Equal(45, d.NewFps);
    }

    [Fact]
    public void Decide_StaticContent_RampsDownInCoarseBuckets_ToFloor()
    {
        var c = new AdaptiveFpsController();
        int fps = 60;
        // Repeatedly ramp down with static content; should settle at floor (20), not below.
        for (int i = 0; i < 10; i++)
        {
            var d = c.Decide(activeFramesInWindow: 2, windowMs: WindowMs, currentTargetFps: fps, floorFps: 20, ceilFps: 60);
            if (d.Changed) fps = d.NewFps;
            BypassCooldown(c);
        }
        Assert.Equal(20, fps);
    }

    // ==================== Ramp up on saturation ====================

    [Fact]
    public void Decide_SaturatedContent_RampsUp()
    {
        var c = new AdaptiveFpsController();
        // At 20fps cap, delivering 20 active fps → saturating (20 >= 0.85*20=17) → ramp up to 30
        var d = c.Decide(activeFramesInWindow: 20, windowMs: WindowMs, currentTargetFps: 20, floorFps: 20, ceilFps: 60);

        Assert.True(d.Changed);
        Assert.Equal(30, d.NewFps);
    }

    [Fact]
    public void Decide_SaturatedContent_RecoversToCeiling()
    {
        var c = new AdaptiveFpsController();
        int fps = 20;
        // Busy content saturating the cap each window should climb back to ceiling (60).
        for (int i = 0; i < 10; i++)
        {
            var d = c.Decide(activeFramesInWindow: fps, windowMs: WindowMs, currentTargetFps: fps, floorFps: 20, ceilFps: 60);
            if (d.Changed) fps = d.NewFps;
            BypassCooldown(c);
        }
        Assert.Equal(60, fps);
    }

    // ==================== Bounds ====================

    [Fact]
    public void Decide_AtCeiling_Saturated_NoChange()
    {
        var c = new AdaptiveFpsController();
        var d = c.Decide(activeFramesInWindow: 60, windowMs: WindowMs, currentTargetFps: 60, floorFps: 20, ceilFps: 60);

        Assert.False(d.Changed);
        Assert.Equal(60, d.NewFps);
    }

    [Fact]
    public void Decide_AtFloor_Static_NoChange()
    {
        var c = new AdaptiveFpsController();
        var d = c.Decide(activeFramesInWindow: 1, windowMs: WindowMs, currentTargetFps: 20, floorFps: 20, ceilFps: 60);

        Assert.False(d.Changed);
        Assert.Equal(20, d.NewFps);
    }

    [Fact]
    public void Decide_RespectsClientCeiling_BelowBucket()
    {
        var c = new AdaptiveFpsController();
        // Client ceiling = 30. Saturated at 30 must NOT jump to 45.
        var d = c.Decide(activeFramesInWindow: 30, windowMs: WindowMs, currentTargetFps: 30, floorFps: 20, ceilFps: 30);

        Assert.False(d.Changed);
        Assert.Equal(30, d.NewFps);
    }

    // ==================== Hysteresis dead-band ====================

    [Fact]
    public void Decide_MidRange_Hysteresis_Holds()
    {
        var c = new AdaptiveFpsController();
        // current=30: dead-band is [15, 25.5). activeRate=20 is between → hold (no flap).
        var d = c.Decide(activeFramesInWindow: 20, windowMs: WindowMs, currentTargetFps: 30, floorFps: 20, ceilFps: 60);

        Assert.False(d.Changed);
        Assert.Equal("stable", d.Reason);
    }

    // ==================== Cooldown ====================

    [Fact]
    public void Decide_Cooldown_BlocksRapidFlip()
    {
        var c = new AdaptiveFpsController();

        var d1 = c.Decide(activeFramesInWindow: 5, windowMs: WindowMs, currentTargetFps: 60, floorFps: 20, ceilFps: 60);
        Assert.True(d1.Changed); // first ramp down

        // Immediate second call — cooldown must block.
        var d2 = c.Decide(activeFramesInWindow: 5, windowMs: WindowMs, currentTargetFps: d1.NewFps, floorFps: 20, ceilFps: 60);
        Assert.False(d2.Changed);
        Assert.Equal("cooldown_down", d2.Reason);
    }

    [Fact]
    public void Decide_DownFasterThanUp_AsymmetricCooldown()
    {
        var c = new AdaptiveFpsController();
        // 1.5s since last change: down cooldown (1s) cleared, up cooldown (2s) not.
        ReflectionHelper.SetPrivateField(c, "_lastChangeTime", DateTime.UtcNow.AddMilliseconds(-1500));

        // Down is allowed at 1.5s
        var down = c.Decide(activeFramesInWindow: 5, windowMs: WindowMs, currentTargetFps: 60, floorFps: 20, ceilFps: 60);
        Assert.True(down.Changed);

        // Reset clock to 1.5s ago again, this time exercise the UP path — must still be in cooldown.
        ReflectionHelper.SetPrivateField(c, "_lastChangeTime", DateTime.UtcNow.AddMilliseconds(-1500));
        var up = c.Decide(activeFramesInWindow: 20, windowMs: WindowMs, currentTargetFps: 20, floorFps: 20, ceilFps: 60);
        Assert.False(up.Changed);
        Assert.Equal("cooldown_up", up.Reason);
    }

    // ==================== Edge cases ====================

    [Fact]
    public void Decide_DegenerateRange_FloorEqualsCeil_NoChange()
    {
        var c = new AdaptiveFpsController();
        var d = c.Decide(activeFramesInWindow: 60, windowMs: WindowMs, currentTargetFps: 30, floorFps: 30, ceilFps: 30);

        Assert.False(d.Changed);
        Assert.Equal(30, d.NewFps);
    }

    [Fact]
    public void Decide_ZeroWindow_NoCrash_NoChange()
    {
        var c = new AdaptiveFpsController();
        var d = c.Decide(activeFramesInWindow: 0, windowMs: 0, currentTargetFps: 60, floorFps: 20, ceilFps: 60);

        Assert.False(d.Changed);
    }

    [Fact]
    public void Decide_FullMotion_HoldsCeiling()
    {
        var c = new AdaptiveFpsController();
        // Gaming/video: 60 active fps at 60 cap → saturated but already at ceiling → hold.
        var d = c.Decide(activeFramesInWindow: 60, windowMs: WindowMs, currentTargetFps: 60, floorFps: 20, ceilFps: 60);

        Assert.False(d.Changed);
        Assert.Equal(60, d.NewFps);
    }
}
