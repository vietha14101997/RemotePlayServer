#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network
{
    /// <summary>
    /// Manages ADB reverse port forwarding for direct USB communication.
    ///
    /// Instead of USB Tethering (RNDIS), which creates a virtual network adapter
    /// and routes data through the full TCP/IP stack, ADB reverse creates a direct
    /// TCP pipe over USB. The Android client connects to localhost:PORT, and ADB
    /// routes it through USB to the PC's localhost:PORT.
    ///
    /// Benefits over USB Tethering:
    /// - Lower latency (bypasses RNDIS virtual adapter + TCP/IP stack overhead)
    /// - No need to enable USB Tethering on Android (only USB debugging required)
    /// - Works alongside USB Tethering (can use both simultaneously)
    /// </summary>
    public static class AdbPortForwardHelper
    {
        private static string? _adbPath;
        private static bool _reverseActive;
        private static int _activePort;

        /// <summary>
        /// Detect if ADB is available on the system.
        /// Searches common locations and PATH.
        /// </summary>
        public static bool IsAdbAvailable()
        {
            _adbPath = FindAdbPath();
            if (_adbPath != null)
            {
                Logger.Info($"[ADB] Found adb at: {_adbPath}");
                return true;
            }
            Logger.Debug("[ADB] adb.exe not found on this system");
            return false;
        }

        /// <summary>
        /// Check if an Android device is connected via USB with USB debugging enabled.
        /// </summary>
        public static bool IsDeviceConnected()
        {
            if (_adbPath == null && !IsAdbAvailable()) return false;

            try
            {
                var output = RunAdb("devices");
                // Output format: "List of devices attached\nSERIAL\tdevice\n"
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Contains("\tdevice"))
                    {
                        Logger.Info($"[ADB] Device connected: {line.Trim()}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[ADB] Device check failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Set up ADB reverse port forwarding.
        /// After this, Android client can connect to localhost:port and it will
        /// be routed through USB to PC's localhost:port.
        /// </summary>
        /// <param name="port">The port to forward (same port on both sides)</param>
        /// <returns>True if reverse forwarding was set up successfully</returns>
        public static bool SetupReverse(int port)
        {
            if (_adbPath == null && !IsAdbAvailable()) return false;

            try
            {
                // Remove any existing reverse on this port first
                RunAdb($"reverse --remove tcp:{port}");
            }
            catch { /* Ignore if no existing reverse */ }

            try
            {
                var output = RunAdb($"reverse tcp:{port} tcp:{port}");
                _reverseActive = true;
                _activePort = port;
                Logger.Info($"[ADB] Reverse port forwarding active: Android localhost:{port} → PC localhost:{port}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[ADB] Failed to set up reverse forwarding on port {port}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Remove ADB reverse port forwarding.
        /// </summary>
        public static void RemoveReverse()
        {
            if (!_reverseActive || _adbPath == null) return;

            try
            {
                RunAdb($"reverse --remove tcp:{_activePort}");
                Logger.Info($"[ADB] Reverse port forwarding removed for port {_activePort}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ADB] Failed to remove reverse forwarding: {ex.Message}");
            }
            finally
            {
                _reverseActive = false;
                _activePort = 0;
            }
        }

        /// <summary>
        /// Remove all ADB reverse port forwardings.
        /// </summary>
        public static void RemoveAllReverse()
        {
            if (_adbPath == null) return;

            try
            {
                RunAdb("reverse --remove-all");
                Logger.Info("[ADB] All reverse port forwardings removed");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ADB] Failed to remove all reverse forwardings: {ex.Message}");
            }
            _reverseActive = false;
            _activePort = 0;
        }

        /// <summary>
        /// Check if reverse port forwarding is currently active.
        /// </summary>
        public static bool IsReverseActive => _reverseActive;
        public static int ActivePort => _activePort;

        // ──────────── Internal ────────────

        private static string? FindAdbPath()
        {
            // Check common locations
            var candidates = new[]
            {
                // Android SDK via ANDROID_HOME / ANDROID_SDK_ROOT
                Path.Combine(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "", "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "", "platform-tools", "adb.exe"),
                // Common install locations
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "platform-tools", "adb.exe"),
                // Scrcpy bundled adb
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "scrcpy", "adb.exe"),
            };

            foreach (var path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }

            // Check PATH
            try
            {
                var output = RunProcess("where", "adb.exe");
                var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(firstLine) && File.Exists(firstLine))
                    return firstLine;
            }
            catch { }

            return null;
        }

        private static string RunAdb(string arguments)
        {
            if (_adbPath == null) throw new InvalidOperationException("ADB not found");
            return RunProcess(_adbPath, arguments);
        }

        private static string RunProcess(string fileName, string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException($"Process '{fileName}' timed out after 5 seconds");
            }

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            {
                throw new Exception($"Process exited with code {process.ExitCode}: {error.Trim()}");
            }

            return output;
        }
    }
}
