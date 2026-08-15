#nullable enable

namespace RemotePlayServer.Configuration;

/// <summary>
/// User-facing streaming mode. A named bundle over the Phase-2 adaptive-FPS mechanics.
/// </summary>
public enum StreamMode
{
    /// <summary>Current/legacy behaviour: adaptive FPS OFF, full 60fps ceiling. Low-latency gaming.</summary>
    Gaming = 0,

    /// <summary>Desktop/efficiency/work: adaptive FPS ON (15-30fps), capped at a lower ceiling for reading/typing/scrolling with ultra-low bandwidth and crisp text.</summary>
    Efficiency = 1,
    Work = 1,
}

/// <summary>
/// Pure mode → runtime-settings map (no I/O, fully unit-testable).
///
/// Work/Efficiency mode:
///   Gaming = adaptive OFF, ceil 60  → full 60fps low latency.
///   Work   = adaptive ON,  ceil 30, floor 15 → ramps 30↔15 on desktop content.
/// </summary>
public readonly struct StreamModeProfile
{
    public readonly bool AdaptiveFpsEnabled;
    public readonly int CeilFps;
    public readonly int FloorFps;

    public StreamModeProfile(bool adaptiveFpsEnabled, int ceilFps, int floorFps)
    {
        AdaptiveFpsEnabled = adaptiveFpsEnabled;
        CeilFps = ceilFps;
        FloorFps = floorFps;
    }

    // Efficiency/Work defaults. Gaming keeps the full 60 ceiling and adaptive OFF.
    public const int GamingCeilFps = 60;
    public const int EfficiencyCeilFps = 30;
    public const int DefaultFloorFps = 15;

    public static StreamModeProfile For(StreamMode mode) => mode switch
    {
        StreamMode.Efficiency => new StreamModeProfile(true, EfficiencyCeilFps, DefaultFloorFps),
        _ /* Gaming */        => new StreamModeProfile(false, GamingCeilFps, DefaultFloorFps),
    };
}
