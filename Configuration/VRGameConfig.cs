#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Configuration
{
    /// <summary>
    /// Configuration manager for the list of games that trigger VR Mode.
    /// Loaded on startup and updated via Settings.
    /// </summary>
    public static class VRGameConfig
    {
        private static readonly object _lock = new();
        private static readonly HashSet<string> _vrGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Subnautica",
            "SubnauticaZero"
        };

        /// <summary>
        /// Gets a copy of the current VR game process names.
        /// </summary>
        public static IReadOnlySet<string> VRGames
        {
            get
            {
                lock (_lock)
                {
                    return new HashSet<string>(_vrGames, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Gets a comma-separated string representation of the VR games.
        /// </summary>
        public static string VRGamesText
        {
            get
            {
                lock (_lock)
                {
                    return string.Join(", ", _vrGames);
                }
            }
        }

        /// <summary>
        /// Loads the list of VR games from configuration.
        /// If the file does not exist, writes the defaults.
        /// </summary>
        public static async Task LoadAsync()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "vr_games.json");
                if (!File.Exists(path))
                {
                    await SaveAsync(new List<string> { "Subnautica", "SubnauticaZero" });
                    return;
                }

                var json = await File.ReadAllTextAsync(path);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    lock (_lock)
                    {
                        _vrGames.Clear();
                        foreach (var game in list)
                        {
                            if (!string.IsNullOrWhiteSpace(game))
                            {
                                _vrGames.Add(game.Trim());
                            }
                        }
                    }
                    Logger.Info($"[VRGameConfig] Loaded {list.Count} VR games: {string.Join(", ", list)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[VRGameConfig] Load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the given list of VR games to configuration.
        /// </summary>
        public static async Task SaveAsync(IEnumerable<string> games)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "vr_games.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var list = new List<string>();
                lock (_lock)
                {
                    _vrGames.Clear();
                    foreach (var game in games)
                    {
                        if (!string.IsNullOrWhiteSpace(game))
                        {
                            var trimmed = game.Trim();
                            _vrGames.Add(trimmed);
                            list.Add(trimmed);
                        }
                    }
                }

                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
                Logger.Info($"[VRGameConfig] Saved {list.Count} VR games");
            }
            catch (Exception ex)
            {
                Logger.Error($"[VRGameConfig] Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a process name matches any of the configured VR games.
        /// </summary>
        public static bool IsVRGame(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            lock (_lock)
            {
                return _vrGames.Contains(processName.Trim());
            }
        }
    }
}
