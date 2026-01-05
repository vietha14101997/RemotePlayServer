#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// Helper for Android Debug Bridge (ADB) commands.
    /// Used for USB connection mode via reverse port forwarding.
    /// </summary>
    public static class AdbHelper
    {
        private static string? _adbPath;
        private static bool _initialized;

        /// <summary>
        /// Initialize ADB helper by finding adb.exe path.
        /// Searches in: bin/tools/, PATH, ANDROID_HOME/platform-tools/
        /// </summary>
        public static bool Initialize()
        {
            if (_initialized) return _adbPath != null;
            _initialized = true;

            // Priority 1: Bundled in bin/tools/
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var bundledPath = Path.Combine(appDir, "tools", "adb.exe");
            if (File.Exists(bundledPath))
            {
                _adbPath = bundledPath;
                Console.WriteLine($"[ADB] Found bundled: {_adbPath}");
                return true;
            }

            // Priority 2: In PATH
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                try
                {
                    var adbInPath = Path.Combine(dir.Trim(), "adb.exe");
                    if (File.Exists(adbInPath))
                    {
                        _adbPath = adbInPath;
                        Console.WriteLine($"[ADB] Found in PATH: {_adbPath}");
                        return true;
                    }
                }
                catch { }
            }

            // Priority 3: ANDROID_HOME
            var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
            if (!string.IsNullOrEmpty(androidHome))
            {
                var androidAdb = Path.Combine(androidHome, "platform-tools", "adb.exe");
                if (File.Exists(androidAdb))
                {
                    _adbPath = androidAdb;
                    Console.WriteLine($"[ADB] Found in ANDROID_HOME: {_adbPath}");
                    return true;
                }
            }

            Console.WriteLine("[ADB] adb.exe not found. USB mode unavailable.");
            return false;
        }

        /// <summary>
        /// Check if ADB is available on this system.
        /// </summary>
        public static bool IsAvailable => Initialize();

        /// <summary>
        /// Get list of connected USB devices.
        /// Returns device serials like ["ABCD1234", "5678EFGH"]
        /// </summary>
        public static string[] GetConnectedDevices()
        {
            if (!IsAvailable) return Array.Empty<string>();

            try
            {
                var output = RunAdbCommand("devices");
                // Parse output like:
                // List of devices attached
                // ABCD1234    device
                // 5678EFGH    device

                return output
                    .Split('\n')
                    .Skip(1) // Skip "List of devices attached"
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrEmpty(line) && line.Contains('\t'))
                    .Select(line => line.Split('\t')[0])
                    .Where(serial => !string.IsNullOrEmpty(serial))
                    .ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADB] GetConnectedDevices error: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Check if any USB device is connected.
        /// </summary>
        public static bool IsDeviceConnected()
        {
            var devices = GetConnectedDevices();
            return devices.Length > 0;
        }

        /// <summary>
        /// Get the first connected device serial (for single-device scenarios).
        /// </summary>
        public static string? GetFirstDeviceSerial()
        {
            var devices = GetConnectedDevices();
            return devices.Length > 0 ? devices[0] : null;
        }

        /// <summary>
        /// Setup reverse port forwarding: device:port → localhost:port
        /// This allows the Android app to connect to localhost:port and reach the PC server.
        /// </summary>
        /// <param name="port">Port to forward (e.g., 8288)</param>
        /// <param name="deviceSerial">Optional device serial (for multi-device)</param>
        /// <returns>True if successful</returns>
        public static bool SetupReversePort(int port, string? deviceSerial = null)
        {
            if (!IsAvailable) return false;

            try
            {
                // First remove any existing reverse for this port
                CleanupReversePort(port, deviceSerial);

                // Setup new reverse: tcp:8288 → tcp:8288
                var args = $"reverse tcp:{port} tcp:{port}";
                if (!string.IsNullOrEmpty(deviceSerial))
                    args = $"-s {deviceSerial} " + args;

                var output = RunAdbCommand(args);

                // Verify it was set
                var reverseList = RunAdbCommand(
                    string.IsNullOrEmpty(deviceSerial)
                        ? "reverse --list"
                        : $"-s {deviceSerial} reverse --list");

                bool success = reverseList.Contains($"tcp:{port}");

                if (success)
                    Console.WriteLine($"[ADB] Reverse port forwarding active: device:{port} → localhost:{port}");
                else
                    Console.WriteLine($"[ADB] Failed to setup reverse port {port}");

                return success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADB] SetupReversePort error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Remove reverse port forwarding for a specific port.
        /// </summary>
        public static void CleanupReversePort(int port, string? deviceSerial = null)
        {
            if (!IsAvailable) return;

            try
            {
                var args = $"reverse --remove tcp:{port}";
                if (!string.IsNullOrEmpty(deviceSerial))
                    args = $"-s {deviceSerial} " + args;

                RunAdbCommand(args);
                Console.WriteLine($"[ADB] Removed reverse for port {port}");
            }
            catch (Exception ex)
            {
                // Ignore errors - port might not have been forwarded
                Console.WriteLine($"[ADB] CleanupReversePort: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove all reverse port forwardings.
        /// </summary>
        public static void CleanupAllReverse(string? deviceSerial = null)
        {
            if (!IsAvailable) return;

            try
            {
                var args = "reverse --remove-all";
                if (!string.IsNullOrEmpty(deviceSerial))
                    args = $"-s {deviceSerial} " + args;

                RunAdbCommand(args);
                Console.WriteLine("[ADB] Removed all reverse port forwardings");
            }
            catch { }
        }

        /// <summary>
        /// Start ADB server if not running.
        /// </summary>
        public static bool StartServer()
        {
            if (!IsAvailable) return false;

            try
            {
                RunAdbCommand("start-server");
                Console.WriteLine("[ADB] Server started");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADB] StartServer error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Wait for a device to be connected (with timeout).
        /// </summary>
        public static async Task<bool> WaitForDeviceAsync(int timeoutMs = 30000, CancellationToken ct = default)
        {
            if (!IsAvailable) return false;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                if (IsDeviceConnected())
                    return true;

                await Task.Delay(500, ct);
            }

            return false;
        }

        /// <summary>
        /// Get device model name (for logging/display).
        /// </summary>
        public static string? GetDeviceModel(string? deviceSerial = null)
        {
            if (!IsAvailable) return null;

            try
            {
                var args = "shell getprop ro.product.model";
                if (!string.IsNullOrEmpty(deviceSerial))
                    args = $"-s {deviceSerial} " + args;

                return RunAdbCommand(args).Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Run an ADB command and return stdout.
        /// </summary>
        private static string RunAdbCommand(string arguments, int timeoutMs = 10000)
        {
            if (_adbPath == null)
                throw new InvalidOperationException("ADB not initialized");

            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("Failed to start ADB process");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException($"ADB command timed out: {arguments}");
            }

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                // Some ADB commands return non-zero but are fine (e.g., reverse --remove for non-existent)
                Console.WriteLine($"[ADB] Warning: {error.Trim()}");
            }

            return output;
        }
    }
}
