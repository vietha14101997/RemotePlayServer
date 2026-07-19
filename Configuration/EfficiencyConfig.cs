#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePlayServer.Core;

namespace RemotePlayServer.Configuration;

/// <summary>
/// Runtime knobs for Host Desktop/Efficiency mode.
///
/// The persisted source of truth is <see cref="Mode"/> (Gaming|Efficiency), stored in
/// <c>Configuration/efficiency-settings.json</c>. The derived runtime settings are exposed as a
/// single immutable <see cref="StreamModeProfile"/> snapshot via <see cref="Active"/>, consumed by
/// <c>AdaptiveFpsCoordinator</c>.
///
/// DEFAULT = <see cref="StreamMode.Gaming"/> for safe landing: adaptive FPS OFF, full 60 ceiling ⇒
/// a plain build stays byte-for-byte identical to today's client-driven FPS behaviour. The snapshot
/// is held behind a <c>volatile</c> reference and published atomically, so the Phase-3 mode toggle
/// can flip it live (the coordinator reads <see cref="Active"/> once per ~1s tick, never mixing an
/// old ceiling with a new flag) without a restart. Any load error keeps the safe Gaming default.
/// </summary>
public static class EfficiencyConfig
{
    private const string ConfigFileName = "efficiency-settings.json";

    /// <summary>Persisted mode (Gaming|Efficiency). Default Gaming.</summary>
    public static StreamMode Mode { get; private set; } = StreamMode.Gaming;

    // Single ATOMIC snapshot of the derived profile. A reference write is atomic, so a reader
    // (the coordinator's timer thread) can never observe a half-applied profile — this prevents
    // the torn read where a mode toggle would briefly mix the old ceiling with the new flag.
    // ALWAYS read once per decision via <see cref="Active"/>, never field-by-field.
    private static volatile ProfileSnapshot _active = new(StreamModeProfile.For(StreamMode.Gaming));

    /// <summary>Current derived profile as a consistent, immutable snapshot. Read once per tick.</summary>
    public static StreamModeProfile Active => _active.Profile;

    private sealed class ProfileSnapshot
    {
        public readonly StreamModeProfile Profile;
        public ProfileSnapshot(StreamModeProfile p) => Profile = p;
    }

    static EfficiencyConfig()
    {
        try
        {
            var path = ConfigPath();
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<EfficiencySettings>(File.ReadAllText(path));
                ApplyProfile(cfg?.Mode ?? StreamMode.Gaming);
            }
            else
            {
                ApplyProfile(StreamMode.Gaming);
                WriteConfig(path);
            }
        }
        catch (Exception ex)
        {
            ApplyProfile(StreamMode.Gaming);
            Logger.Error($"[Efficiency] Failed to load {ConfigFileName}, using safe default (Gaming): {ex.Message}");
        }
    }

    /// <summary>
    /// Switch mode at runtime (from the Host UI). Updates the derived runtime fields — the
    /// running coordinator picks them up on its next tick, no stream teardown — and persists.
    /// </summary>
    public static void SetMode(StreamMode mode, bool persist = true)
    {
        ApplyProfile(mode);
        if (persist)
        {
            try { WriteConfig(ConfigPath()); }
            catch (Exception ex) { Logger.Warn($"[Efficiency] Failed to persist mode: {ex.Message}"); }
        }
        var p = Active;
        Logger.Info($"[Efficiency] Mode set to {mode} (adaptiveFps={p.AdaptiveFpsEnabled}, ceil={p.CeilFps}, floor={p.FloorFps})");
    }

    private static void ApplyProfile(StreamMode mode)
    {
        Mode = mode;
        // Single atomic publish: readers see the whole profile or none of it.
        _active = new ProfileSnapshot(StreamModeProfile.For(mode));
    }

    private sealed class EfficiencySettings
    {
        [JsonPropertyName("streamMode")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StreamMode Mode { get; set; } = StreamMode.Gaming;
    }

    private static void WriteConfig(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new EfficiencySettings { Mode = Mode },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string ConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "Configuration", ConfigFileName);
}
