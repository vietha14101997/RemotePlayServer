using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Tests;

/// <summary>
/// Phase 2 (Signaling Resilience) — pure backoff math for RelayClient's reconnect loop.
/// No networking/WPF dependency: this class is a deterministic calculator, so these tests
/// exercise the exact formula (delay = min(base*2^n, max) + jitter) without needing a relay.
/// </summary>
public class RelayReconnectPolicyTests
{
    private static RelayReconnectPolicy CreatePolicy(int seed, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, TimeSpan? maxJitter = null) =>
        new(baseDelay, maxDelay, maxJitter, new Random(seed));

    [Fact]
    public void NextDelay_FirstAttempt_ApproxBaseDelayPlusJitter()
    {
        var policy = CreatePolicy(seed: 1, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), maxJitter: TimeSpan.FromSeconds(1));

        var delay = policy.NextDelay(0);

        // attempt 0 => base * 2^0 = base (1s) + jitter in [0,1)s
        Assert.InRange(delay.TotalSeconds, 1.0, 2.0);
    }

    [Fact]
    public void NextDelay_GrowsExponentiallyWithAttempt()
    {
        var policy = CreatePolicy(seed: 2, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), maxJitter: TimeSpan.Zero);

        var d0 = policy.NextDelay(0); // 1s
        var d1 = policy.NextDelay(1); // 2s
        var d2 = policy.NextDelay(2); // 4s
        var d3 = policy.NextDelay(3); // 8s

        Assert.Equal(1.0, d0.TotalSeconds, precision: 3);
        Assert.Equal(2.0, d1.TotalSeconds, precision: 3);
        Assert.Equal(4.0, d2.TotalSeconds, precision: 3);
        Assert.Equal(8.0, d3.TotalSeconds, precision: 3);
    }

    [Fact]
    public void NextDelay_CapsAtMaxDelay_EvenForLargeAttempts()
    {
        var policy = CreatePolicy(seed: 3, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), maxJitter: TimeSpan.FromSeconds(1));

        // 2^10 = 1024s, far past the 30s cap.
        var delay = policy.NextDelay(10);

        Assert.InRange(delay.TotalSeconds, 30.0, 31.0);
    }

    [Fact]
    public void NextDelay_HugeAttemptCount_DoesNotOverflowOrThrow()
    {
        var policy = CreatePolicy(seed: 4, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), maxJitter: TimeSpan.FromSeconds(1));

        var delay = policy.NextDelay(int.MaxValue - 1);

        Assert.InRange(delay.TotalSeconds, 30.0, 31.0);
    }

    [Fact]
    public void NextDelay_NegativeAttempt_TreatedAsZero()
    {
        var policy = CreatePolicy(seed: 5, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), maxJitter: TimeSpan.Zero);

        var delay = policy.NextDelay(-5);

        Assert.Equal(1.0, delay.TotalSeconds, precision: 3);
    }

    [Fact]
    public void NextDelay_NeverExceedsMaxPlusJitter()
    {
        var policy = CreatePolicy(seed: 6, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), maxJitter: TimeSpan.FromSeconds(1));

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var delay = policy.NextDelay(attempt);
            Assert.True(delay.TotalSeconds <= 31.0, $"attempt {attempt} produced {delay.TotalSeconds}s, expected <= 31s");
        }
    }

    [Theory]
    [InlineData(0, 30, 1)]   // base must be positive
    [InlineData(1, 0, 1)]    // max must be >= base
    public void Constructor_InvalidRange_Throws(int baseSeconds, int maxSeconds, int jitterSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayReconnectPolicy(TimeSpan.FromSeconds(baseSeconds), TimeSpan.FromSeconds(maxSeconds), TimeSpan.FromSeconds(jitterSeconds)));
    }

    [Fact]
    public void Constructor_NegativeJitter_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-1)));
    }
}
