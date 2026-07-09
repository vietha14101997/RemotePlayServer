#nullable enable
using System;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Computes exponential backoff delays (with jitter) for relay presence-WS reconnect
/// attempts. Pure/stateless calculation — the caller owns the attempt counter so it can
/// reset it on a successful reconnect (Phase 2 — Signaling Resilience).
/// Formula: delay = min(baseDelay * 2^attempt, maxDelay) + jitter(0..maxJitter).
/// </summary>
public sealed class RelayReconnectPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _maxJitter;
    private readonly Random _random;

    /// <summary>
    /// Cap the exponent before computing 2^n so extremely high attempt counts can't
    /// overflow double math before the min() clamp is applied.
    /// </summary>
    private const int MaxExponent = 20;

    public RelayReconnectPolicy(TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, TimeSpan? maxJitter = null, Random? random = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        _maxJitter = maxJitter ?? TimeSpan.FromSeconds(1);
        _random = random ?? Random.Shared;

        if (_baseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay must be positive.");
        if (_maxDelay < _baseDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= base delay.");
        if (_maxJitter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxJitter), "Max jitter cannot be negative.");
    }

    /// <summary>
    /// Delay for a zero-based attempt number (0 = first retry after the initial drop).
    /// Negative attempts are treated as 0.
    /// </summary>
    public TimeSpan NextDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt, 0, MaxExponent);
        var scaledMs = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(scaledMs, _maxDelay.TotalMilliseconds);

        var jitterMs = _maxJitter > TimeSpan.Zero ? _random.NextDouble() * _maxJitter.TotalMilliseconds : 0;
        return TimeSpan.FromMilliseconds(cappedMs + jitterMs);
    }
}
