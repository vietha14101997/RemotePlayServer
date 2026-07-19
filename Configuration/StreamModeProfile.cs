#nullable enable

namespace RemotePlayServer.Configuration;

/// <summary>
/// User-facing streaming mode. A named bundle over the Phase-2 adaptive-FPS mechanics.
/// </summary>
public enum StreamMode
{
    /// <summary>Current/legacy behaviour: adaptive FPS OFF, full 60fps ceiling. Low-latency gaming.</summary>
    Gaming = 0,

    /// <summary>Desktop/efficiency: adaptive FPS ON, capped at a lower ceiling for reading/typing/scrolling.</summary>
    Efficiency = 1,
}

/// <summary>
/// Pure mode → runtime-settings map (no I/O, fully unit-testable).
///
/// Efficiency is NOT new mechanics — it is the Phase-2 controller enabled with a lower ceiling:
///   Gaming     = adaptive OFF, ceil 60  → byte-for-byte identical to today's path.
///   Efficiency = adaptive ON,  ceil 30, floor 20 → ramps 30↔20 on desktop content.
///
/// No separate bitrate table (DRY): capping FPS is itself the text-quality win — at the same
/// bitrate, half the frame rate means ~2× the bits/frame, so text is crisper. The existing
/// GetBitrateRange + AdaptiveBitrateController (which already won't cut bitrate on low-FPS-no-drops
/// or raise it on static content) handles the byte budget. QP tuning is unavailable at the C#
/// wrapper layer (only SetBitrate exists — see plan Q4), so the profile is FPS-cap only.
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

    // Efficiency defaults. Gaming keeps the full 60 ceiling and adaptive OFF (regression-identical).
    public const int GamingCeilFps = 60;
    public const int EfficiencyCeilFps = 30;
    public const int DefaultFloorFps = 20;

    public static StreamModeProfile For(StreamMode mode) => mode switch
    {
        StreamMode.Efficiency => new StreamModeProfile(true, EfficiencyCeilFps, DefaultFloorFps),
        _ /* Gaming */        => new StreamModeProfile(false, GamingCeilFps, DefaultFloorFps),
    };
}
