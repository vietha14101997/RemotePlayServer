#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Manages internet mode configuration.
/// Singleton pattern - initialized once at startup.
/// </summary>
public class InternetManager
{
    private static volatile InternetManager? _instance;
    public static InternetManager? Instance => _instance;

    public InternetConfig Config { get; }

    private const string ConfigFileName = "internet-settings.json";

    private InternetManager(InternetConfig config)
    {
        Config = config;
    }

    /// <summary>
    /// Load config from internet-settings.json and create singleton instance.
    /// Creates default config file if missing.
    /// </summary>
    public static async Task<InternetConfig> LoadConfigAsync()
    {
        var configPath = GetConfigPath();
        InternetConfig config;

        if (File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath);
                config = JsonSerializer.Deserialize<InternetConfig>(json) ?? new InternetConfig();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Internet] Failed to read config: {ex.Message}. Using defaults.");
                config = new InternetConfig();
            }
        }
        else
        {
            config = new InternetConfig();
            try
            {
                var dir = Path.GetDirectoryName(configPath);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(configPath, json);
                Logger.Info($"[Internet] Created default config: {configPath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Internet] Failed to create default config: {ex.Message}");
            }
        }

        _instance = new InternetManager(config);
        return config;
    }

    private static string GetConfigPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Configuration", ConfigFileName);
    }
}
