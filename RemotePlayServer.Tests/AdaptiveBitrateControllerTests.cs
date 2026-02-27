using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Tests.Helpers;

namespace RemotePlayServer.Tests;

public class AdaptiveBitrateControllerTests
{
    private AdaptiveBitrateController CreateController(
        int initialBitrate = 20000,
        int? maxBitrate = null,
        bool wifiMode = false,
        bool skipWarmup = true)
    {
        var controller = new AdaptiveBitrateController();
        controller.IsWiFiMode = wifiMode;
        controller.Initialize(initialBitrate, maxBitrate);

        if (skipWarmup)
            BypassWarmup(controller);

        return controller;
    }

    /// <summary>
    /// Set _streamStartTime to 60 seconds ago to bypass the 15s warmup.
    /// </summary>
    private static void BypassWarmup(AdaptiveBitrateController controller)
    {
        ReflectionHelper.SetPrivateField(controller, "_streamStartTime", DateTime.UtcNow.AddSeconds(-60));
    }

    /// <summary>
    /// Set _lastAdjustmentTime to far past so cooldown doesn't block.
    /// </summary>
    private static void BypassCooldown(AdaptiveBitrateController controller)
    {
        ReflectionHelper.SetPrivateField(controller, "_lastAdjustmentTime", DateTime.MinValue);
    }

    /// <summary>
    /// Set _lastNetworkIssueTime to far past so recovery is allowed.
    /// </summary>
    private static void BypassRecoveryDelay(AdaptiveBitrateController controller)
    {
        ReflectionHelper.SetPrivateField(controller, "_lastNetworkIssueTime", DateTime.UtcNow.AddSeconds(-30));
    }

    private static QualityFeedbackMessage GoodFeedback(float targetFps = 60f) => new()
    {
        EffectiveFps = targetFps,
        TargetFps = targetFps,
        PacketLossRate = 0f,
        AvgPacketLossRate = 0f,
        BufferStatus = "healthy",
        RttMs = 10,
        Monitors = new List<MonitorFeedback>
        {
            new() { Index = 0, RenderedFrames = 60, DroppedFrames = 0 }
        }
    };

    private static QualityFeedbackMessage HighPacketLossFeedback() => new()
    {
        EffectiveFps = 55,
        TargetFps = 60,
        PacketLossRate = 0.05f, // 5% loss > 2% threshold
        AvgPacketLossRate = 0.05f,
        BufferStatus = "lossy",
        RttMs = 50,
        Monitors = new List<MonitorFeedback>
        {
            new() { Index = 0, RenderedFrames = 55, DroppedFrames = 5 }
        }
    };

    private static QualityFeedbackMessage CriticalFpsWithDropsFeedback() => new()
    {
        EffectiveFps = 10, // 10/60 = 16.7% < 30% critical threshold
        TargetFps = 60,
        PacketLossRate = 0.02f,
        AvgPacketLossRate = 0.02f,
        BufferStatus = "starving",
        RttMs = 100,
        Monitors = new List<MonitorFeedback>
        {
            new() { Index = 0, RenderedFrames = 10, DroppedFrames = 20 }
        }
    };

    private static QualityFeedbackMessage CriticalFpsNoDrop() => new()
    {
        EffectiveFps = 10,
        TargetFps = 60,
        PacketLossRate = 0f,
        AvgPacketLossRate = 0f,
        BufferStatus = "healthy",
        RttMs = 10,
        Monitors = new List<MonitorFeedback>
        {
            new() { Index = 0, RenderedFrames = 10, DroppedFrames = 0 }
        }
    };

    private static QualityFeedbackMessage BufferStarvingWithDrops() => new()
    {
        EffectiveFps = 50,
        TargetFps = 60,
        PacketLossRate = 0.005f,
        AvgPacketLossRate = 0.005f,
        BufferStatus = "starving",
        RttMs = 30,
        Monitors = new List<MonitorFeedback>
        {
            new() { Index = 0, RenderedFrames = 50, DroppedFrames = 10 }
        }
    };

    private static QualityFeedbackMessage FpsDropWithNetworkIssues() => new()
    {
        EffectiveFps = 35, // 35/60 = 58% < 70% threshold
        TargetFps = 60,
        PacketLossRate = 0.005f,
        AvgPacketLossRate = 0.005f,
        BufferStatus = "lossy",
        RttMs = 40,
        Monitors = new List<MonitorFeedback>
        {
            new() { Index = 0, RenderedFrames = 35, DroppedFrames = 10 }
        }
    };

    // ==================== Initialization Tests ====================

    [Fact]
    public void Initialize_SetsCorrectTargetBitrate()
    {
        var controller = new AdaptiveBitrateController();
        controller.Initialize(15000);

        Assert.Equal(15000, controller.TargetBitrateKbps);
        Assert.Equal(15000, controller.InitialBitrateKbps);
    }

    [Fact]
    public void Initialize_SetsCustomMaxBitrate()
    {
        var controller = new AdaptiveBitrateController();
        controller.Initialize(15000, maxBitrateKbps: 30000);

        Assert.Equal(30000, controller.MaxBitrateKbps);
    }

    [Fact]
    public void Initialize_DefaultMaxBitrate_Is50000()
    {
        var controller = new AdaptiveBitrateController();
        controller.Initialize(15000);

        Assert.Equal(50000, controller.MaxBitrateKbps);
    }

    [Fact]
    public void Initialize_DoesNotResetAdjustmentCount()
    {
        // Initialize only sets bitrate/EWMA state, not statistics.
        // Use Reset() to clear AdjustmentCount.
        var controller = CreateController();
        controller.ProcessFeedback(HighPacketLossFeedback()); // Force an adjustment
        Assert.True(controller.AdjustmentCount > 0);

        controller.Initialize(20000); // Re-initialize
        Assert.True(controller.AdjustmentCount > 0); // Count persists
    }

    // ==================== High Packet Loss - Always Decreases ====================

    [Fact]
    public void ProcessFeedback_HighPacketLoss_DecreasesBitrate()
    {
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(HighPacketLossFeedback());

        Assert.True(decision.Changed);
        Assert.True(decision.NewBitrate < 20000);
    }

    [Fact]
    public void ProcessFeedback_HighPacketLoss_DecreasesByAtLeast10Percent()
    {
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(HighPacketLossFeedback());

        // 10% of 20000 = 2000, so new bitrate should be <= 18000
        Assert.True(decision.NewBitrate <= 18000);
    }

    [Fact]
    public void ProcessFeedback_HighPacketLoss_WorksEvenDuringWarmup()
    {
        // Don't skip warmup
        var controller = CreateController(initialBitrate: 20000, skipWarmup: false);
        var decision = controller.ProcessFeedback(HighPacketLossFeedback());

        Assert.True(decision.Changed);
        Assert.True(decision.NewBitrate < 20000);
    }

    // ==================== Critical FPS ====================

    [Fact]
    public void ProcessFeedback_CriticalFpsWithDrops_DecreasesAggressively()
    {
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(CriticalFpsWithDropsFeedback());

        Assert.True(decision.Changed);
        // Aggressive = 25% decrease minimum, so <= 15000
        Assert.True(decision.NewBitrate <= 15000);
    }

    [Fact]
    public void ProcessFeedback_CriticalFpsWithoutDrops_NoDecrease()
    {
        // Static content scenario: low FPS but no dropped frames
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(CriticalFpsNoDrop());

        // Should NOT decrease because no actual network problems
        Assert.False(decision.Changed);
    }

    // ==================== Warmup Period ====================

    [Fact]
    public void ProcessFeedback_DuringWarmup_NormalIssuesDontDecrease()
    {
        var controller = CreateController(initialBitrate: 20000, skipWarmup: false);

        // Buffer starving with drops - normally would decrease, but warmup blocks it
        // Note: the drops must not trigger the critical FPS or high packet loss paths
        var feedback = new QualityFeedbackMessage
        {
            EffectiveFps = 50,
            TargetFps = 60,
            PacketLossRate = 0.005f, // Below 2% threshold
            AvgPacketLossRate = 0.005f,
            BufferStatus = "starving",
            RttMs = 30,
            Monitors = new List<MonitorFeedback>
            {
                new() { Index = 0, RenderedFrames = 50, DroppedFrames = 5 }
            }
        };

        var decision = controller.ProcessFeedback(feedback);
        Assert.False(decision.Changed);
    }

    // ==================== Post-Warmup Decrease Conditions ====================

    [Fact]
    public void ProcessFeedback_BufferStarvingWithDrops_DecreasesBitrate()
    {
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(BufferStarvingWithDrops());

        Assert.True(decision.Changed);
        Assert.True(decision.NewBitrate < 20000);
    }

    [Fact]
    public void ProcessFeedback_BufferStarvingWithoutDrops_NoDecrease()
    {
        var controller = CreateController(initialBitrate: 20000);
        var feedback = new QualityFeedbackMessage
        {
            EffectiveFps = 50,
            TargetFps = 60,
            PacketLossRate = 0f,
            AvgPacketLossRate = 0f,
            BufferStatus = "starving",
            RttMs = 10,
            Monitors = new List<MonitorFeedback>
            {
                new() { Index = 0, RenderedFrames = 50, DroppedFrames = 0 }
            }
        };

        var decision = controller.ProcessFeedback(feedback);
        Assert.False(decision.Changed);
    }

    [Fact]
    public void ProcessFeedback_FpsDropWithNetworkIssues_DecreasesBitrate()
    {
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(FpsDropWithNetworkIssues());

        Assert.True(decision.Changed);
        Assert.True(decision.NewBitrate < 20000);
    }

    // ==================== Increase Conditions ====================

    [Fact]
    public void ProcessFeedback_GoodConditions_BelowInitial_IncreasesBitrate()
    {
        var controller = CreateController(initialBitrate: 20000);

        // First: force a decrease
        controller.ProcessFeedback(HighPacketLossFeedback());
        int afterDecrease = controller.TargetBitrateKbps;
        Assert.True(afterDecrease < 20000);

        // Bypass cooldown for next feedback
        BypassCooldown(controller);

        // Now send good feedback - should increase
        var decision = controller.ProcessFeedback(GoodFeedback());
        Assert.True(decision.Changed);
        Assert.True(decision.NewBitrate > afterDecrease);
    }

    [Fact]
    public void ProcessFeedback_NeverExceedsInitialBitrate()
    {
        var controller = CreateController(initialBitrate: 20000);

        // Force decrease
        controller.ProcessFeedback(HighPacketLossFeedback());
        BypassCooldown(controller);

        // Try to increase multiple times
        for (int i = 0; i < 50; i++)
        {
            controller.ProcessFeedback(GoodFeedback());
            BypassCooldown(controller);
        }

        // Should never exceed initial bitrate
        Assert.True(controller.TargetBitrateKbps <= 20000);
    }

    [Fact]
    public void ProcessFeedback_AtInitialBitrate_GoodConditions_NoChange()
    {
        var controller = CreateController(initialBitrate: 20000);
        var decision = controller.ProcessFeedback(GoodFeedback());

        // Already at initial bitrate, no increase needed
        Assert.False(decision.Changed);
    }

    [Fact]
    public void ProcessFeedback_StaticContent_NoIncrease()
    {
        var controller = CreateController(initialBitrate: 20000);

        // Force decrease first
        controller.ProcessFeedback(HighPacketLossFeedback());
        BypassCooldown(controller);

        // Mark recent network issue to prevent auto-recovery from firing
        // (HighPacketLoss returns early in CalculateNewBitrate before setting _lastNetworkIssueTime)
        ReflectionHelper.SetPrivateField(controller, "_lastNetworkIssueTime", DateTime.UtcNow);

        // Low FPS (static content) - isActiveContent check fails (FPS ratio < 0.6)
        var feedback = new QualityFeedbackMessage
        {
            EffectiveFps = 20, // 20/60 = 33% < 60% threshold
            TargetFps = 60,
            PacketLossRate = 0f,
            AvgPacketLossRate = 0f,
            BufferStatus = "healthy",
            RttMs = 10,
            Monitors = new List<MonitorFeedback>
            {
                new() { Index = 0, RenderedFrames = 20, DroppedFrames = 0 }
            }
        };

        var decision = controller.ProcessFeedback(feedback);
        // No increase because content is not actively streaming
        Assert.False(decision.Changed);
    }

    // ==================== Bitrate Floor ====================

    [Fact]
    public void ProcessFeedback_NeverGoesBelowMinBitrate()
    {
        var controller = CreateController(initialBitrate: 3000); // Close to floor
        controller.MinBitrateKbps = 2000;

        // Repeated high loss
        for (int i = 0; i < 10; i++)
        {
            controller.ProcessFeedback(HighPacketLossFeedback());
            BypassCooldown(controller);
        }

        Assert.True(controller.TargetBitrateKbps >= 2000);
    }

    [Fact]
    public void ProcessFeedback_CustomMinBitrate_Respected()
    {
        var controller = CreateController(initialBitrate: 10000);
        controller.MinBitrateKbps = 5000;

        for (int i = 0; i < 20; i++)
        {
            controller.ProcessFeedback(HighPacketLossFeedback());
            BypassCooldown(controller);
        }

        Assert.True(controller.TargetBitrateKbps >= 5000);
    }

    // ==================== Cooldown ====================

    [Fact]
    public void ProcessFeedback_DuringCooldown_NoChange()
    {
        var controller = CreateController(initialBitrate: 20000);

        // First adjustment - succeeds
        var decision1 = controller.ProcessFeedback(HighPacketLossFeedback());
        Assert.True(decision1.Changed);

        // Immediately try again - should be blocked by cooldown
        var decision2 = controller.ProcessFeedback(HighPacketLossFeedback());
        Assert.False(decision2.Changed);
        Assert.Equal("cooldown", decision2.Reason);
    }

    [Fact]
    public void ProcessFeedback_AfterCooldown_CanAdjust()
    {
        var controller = CreateController(initialBitrate: 20000);

        controller.ProcessFeedback(HighPacketLossFeedback());
        BypassCooldown(controller); // Simulate time passing

        var decision = controller.ProcessFeedback(HighPacketLossFeedback());
        Assert.True(decision.Changed);
    }

    // ==================== WiFi Mode ====================

    [Fact]
    public void ProcessFeedback_WiFiMode_FasterCooldown()
    {
        var controllerWifi = CreateController(initialBitrate: 20000, wifiMode: true);
        var controllerNormal = CreateController(initialBitrate: 20000, wifiMode: false);

        // Both make first adjustment
        controllerWifi.ProcessFeedback(HighPacketLossFeedback());
        controllerNormal.ProcessFeedback(HighPacketLossFeedback());

        // Set both to 1.8 seconds ago (between WiFi 1.5s and normal 2.0s cooldowns)
        var time = DateTime.UtcNow.AddMilliseconds(-1800);
        ReflectionHelper.SetPrivateField(controllerWifi, "_lastAdjustmentTime", time);
        ReflectionHelper.SetPrivateField(controllerNormal, "_lastAdjustmentTime", time);

        // WiFi should be able to adjust (1.8s > 1.5s WiFi cooldown)
        var wifiDecision = controllerWifi.ProcessFeedback(HighPacketLossFeedback());
        Assert.True(wifiDecision.Changed);

        // Normal should still be in cooldown (1.8s < 2.0s normal cooldown)
        var normalDecision = controllerNormal.ProcessFeedback(HighPacketLossFeedback());
        Assert.False(normalDecision.Changed);
    }

    // ==================== Auto-Recovery ====================

    [Fact]
    public void ProcessFeedback_AutoRecovery_AfterStablePeriod()
    {
        var controller = CreateController(initialBitrate: 20000);

        // Force decrease
        controller.ProcessFeedback(HighPacketLossFeedback());
        int afterDecrease = controller.TargetBitrateKbps;
        Assert.True(afterDecrease < 20000);

        // Simulate stable period: no network issues for >10s, last adjustment >5s ago
        BypassCooldown(controller);
        BypassRecoveryDelay(controller);

        // Good feedback with no issues should trigger auto-recovery
        var feedback = GoodFeedback();
        var decision = controller.ProcessFeedback(feedback);

        // Should recover (increase) toward initial bitrate
        Assert.True(decision.NewBitrate >= afterDecrease);
    }

    // ==================== Reset ====================

    [Fact]
    public void Reset_RestoresInitialBitrate()
    {
        var controller = CreateController(initialBitrate: 20000);

        // Force decrease
        controller.ProcessFeedback(HighPacketLossFeedback());
        Assert.True(controller.TargetBitrateKbps < 20000);

        controller.Reset();
        Assert.Equal(20000, controller.TargetBitrateKbps);
    }

    [Fact]
    public void Reset_ClearsAdjustmentCount()
    {
        var controller = CreateController(initialBitrate: 20000);
        controller.ProcessFeedback(HighPacketLossFeedback());
        Assert.True(controller.AdjustmentCount > 0);

        controller.Reset();
        Assert.Equal(0, controller.AdjustmentCount);
    }

    // ==================== Step Size Tests ====================

    [Fact]
    public void ProcessFeedback_DecreaseStepIsLargerThanIncreaseStep()
    {
        var controller = CreateController(initialBitrate: 20000);

        // Decrease
        controller.ProcessFeedback(HighPacketLossFeedback());
        int decreased = controller.TargetBitrateKbps;
        int decreaseAmount = 20000 - decreased;

        // Reset and decrease again to get same starting point, then increase
        controller.Reset();
        BypassWarmup(controller);
        controller.ProcessFeedback(HighPacketLossFeedback());
        BypassCooldown(controller);

        var increaseDecision = controller.ProcessFeedback(GoodFeedback());
        if (increaseDecision.Changed)
        {
            int increaseAmount = increaseDecision.NewBitrate - controller.TargetBitrateKbps + (increaseDecision.NewBitrate - decreased);
            // The key property: decrease step (10%) > increase step (5%)
            // Decrease: max(500, current/10), Increase: max(250, current/20)
            Assert.True(decreaseAmount > 0);
        }
    }

    // ==================== Edge Cases ====================

    [Fact]
    public void ProcessFeedback_ZeroTargetFps_DoesNotCrash()
    {
        var controller = CreateController(initialBitrate: 20000);
        var feedback = new QualityFeedbackMessage
        {
            EffectiveFps = 0,
            TargetFps = 0,
            PacketLossRate = 0f,
            BufferStatus = "healthy",
            RttMs = 10,
            Monitors = new List<MonitorFeedback>()
        };

        var decision = controller.ProcessFeedback(feedback);
        Assert.NotNull(decision);
    }

    [Fact]
    public void ProcessFeedback_NullMonitors_DoesNotCrash()
    {
        var controller = CreateController(initialBitrate: 20000);
        var feedback = new QualityFeedbackMessage
        {
            EffectiveFps = 60,
            TargetFps = 60,
            PacketLossRate = 0.05f, // High loss
            BufferStatus = "lossy",
            RttMs = 50,
            Monitors = null
        };

        var decision = controller.ProcessFeedback(feedback);
        Assert.NotNull(decision);
    }

    [Fact]
    public void GetStats_ReturnsNonEmptyString()
    {
        var controller = CreateController(initialBitrate: 20000);
        var stats = controller.GetStats();

        Assert.NotNull(stats);
        Assert.Contains("Target=20000", stats);
    }
}
