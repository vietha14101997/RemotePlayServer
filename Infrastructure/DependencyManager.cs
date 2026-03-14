#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure;

/// <summary>
/// Checks and auto-downloads required runtime dependencies (FFmpeg, ADB platform-tools).
/// Called at startup before any feature that needs these binaries.
/// </summary>
public static class DependencyManager
{
    // FFmpeg shared GPL build from BtbN (reliable, auto-updated)
    private const string FfmpegDownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";

    // Google's official ADB platform-tools
    private const string AdbDownloadUrl =
        "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    /// <summary>
    /// Check all dependencies and download any that are missing.
    /// Returns true if all critical dependencies are available.
    /// </summary>
    public static async Task<bool> EnsureAllAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== Dependency Check ===");

        var ffmpegOk = await EnsureFfmpegAsync();
        var adbOk = await EnsureAdbAsync();

        if (ffmpegOk && adbOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[Dependencies] All dependencies ready.");
            Console.ResetColor();
        }
        else if (ffmpegOk)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[Dependencies] FFmpeg ready. ADB optional (USB streaming via ADB unavailable).");
            Console.ResetColor();
        }

        return ffmpegOk; // FFmpeg is critical, ADB is optional
    }

    /// <summary>
    /// Ensure FFmpeg shared libraries exist in the bin/ folder.
    /// Downloads and extracts from GitHub if missing.
    /// </summary>
    public static async Task<bool> EnsureFfmpegAsync()
    {
        var binDir = Path.Combine(AppContext.BaseDirectory, "bin");

        // Check if FFmpeg DLLs already exist
        if (Directory.Exists(binDir) && File.Exists(Path.Combine(binDir, "avcodec-61.dll")))
        {
            Logger.Info("[Dependencies] FFmpeg binaries found.");
            return true;
        }

        // Also check for older or different version naming
        if (Directory.Exists(binDir))
        {
            var avcodecFiles = Directory.GetFiles(binDir, "avcodec*.dll");
            if (avcodecFiles.Length > 0)
            {
                Logger.Info("[Dependencies] FFmpeg binaries found (alternate version).");
                return true;
            }
        }

        // Check fallback folder
        var fallbackDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg-master-latest-win64-gpl-shared", "bin");
        if (Directory.Exists(fallbackDir) && Directory.GetFiles(fallbackDir, "avcodec*.dll").Length > 0)
        {
            Logger.Info("[Dependencies] FFmpeg binaries found in fallback folder.");
            return true;
        }

        // Download FFmpeg
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[Dependencies] FFmpeg not found. Downloading...");
        Console.ResetColor();

        try
        {
            var zipPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg-download.zip");
            var extractDir = AppContext.BaseDirectory;

            await DownloadFileAsync(FfmpegDownloadUrl, zipPath, "FFmpeg");

            Console.WriteLine("[Dependencies] Extracting FFmpeg...");

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg-extract-temp");
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, true);

            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

            // Find the bin folder inside extracted archive
            // Structure: ffmpeg-master-latest-win64-gpl-shared/bin/*.dll
            string? sourceBinDir = null;
            foreach (var dir in Directory.GetDirectories(tempExtractDir, "bin", SearchOption.AllDirectories))
            {
                if (Directory.GetFiles(dir, "avcodec*.dll").Length > 0)
                {
                    sourceBinDir = dir;
                    break;
                }
            }

            if (sourceBinDir == null)
            {
                Logger.Error("[Dependencies] FFmpeg archive doesn't contain expected DLLs.");
                CleanupTempFiles(zipPath, tempExtractDir);
                return false;
            }

            // Create bin/ directory and copy DLLs
            Directory.CreateDirectory(binDir);
            foreach (var file in Directory.GetFiles(sourceBinDir))
            {
                var destFile = Path.Combine(binDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            // Cleanup
            CleanupTempFiles(zipPath, tempExtractDir);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[Dependencies] FFmpeg downloaded and installed successfully.");
            Console.ResetColor();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Dependencies] FFmpeg download failed: {ex.Message}");
            Console.WriteLine("[Dependencies] Please manually place FFmpeg shared DLLs in the 'bin' folder.");
            Console.ResetColor();
            return false;
        }
    }

    /// <summary>
    /// Ensure ADB (Android Debug Bridge) is available.
    /// Downloads Google's platform-tools if ADB is not found anywhere.
    /// </summary>
    public static async Task<bool> EnsureAdbAsync()
    {
        // Check if ADB is already available via existing search paths
        if (FindExistingAdb() != null)
        {
            Logger.Info("[Dependencies] ADB found on system.");
            return true;
        }

        // Check our own tools/platform-tools directory
        var localAdbDir = Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools");
        var localAdbPath = Path.Combine(localAdbDir, "adb.exe");
        if (File.Exists(localAdbPath))
        {
            // Ensure it's in PATH so AdbPortForwardHelper can find it
            AddToPath(localAdbDir);
            Logger.Info($"[Dependencies] ADB found at: {localAdbPath}");
            return true;
        }

        // Download ADB platform-tools
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[Dependencies] ADB not found. Downloading platform-tools...");
        Console.ResetColor();

        try
        {
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
            Directory.CreateDirectory(toolsDir);

            var zipPath = Path.Combine(toolsDir, "platform-tools.zip");

            await DownloadFileAsync(AdbDownloadUrl, zipPath, "ADB platform-tools");

            Console.WriteLine("[Dependencies] Extracting platform-tools...");

            // Extract - Google's zip contains "platform-tools/" folder at root
            if (Directory.Exists(localAdbDir))
                Directory.Delete(localAdbDir, true);

            ZipFile.ExtractToDirectory(zipPath, toolsDir);

            // Cleanup zip
            try { File.Delete(zipPath); } catch { }

            if (File.Exists(localAdbPath))
            {
                AddToPath(localAdbDir);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[Dependencies] ADB platform-tools downloaded successfully.");
                Console.ResetColor();
                return true;
            }

            Logger.Error("[Dependencies] ADB extraction succeeded but adb.exe not found.");
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Dependencies] ADB download failed: {ex.Message}");
            Console.WriteLine("[Dependencies] USB streaming via ADB will be unavailable. WiFi/USB Tethering still work.");
            Console.ResetColor();
            return false;
        }
    }

    // ──────────── Internal ────────────

    private static async Task DownloadFileAsync(string url, string destPath, string name)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlayServer/1.0");

        // Clean up any leftover temp file
        var tempPath = destPath + ".tmp";
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { }
            await Task.Delay(500);
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long downloaded = 0;
        int lastPercent = -1;

        while (true)
        {
            var bytesRead = await contentStream.ReadAsync(buffer);
            if (bytesRead == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloaded += bytesRead;

            // Show progress
            if (totalBytes > 0)
            {
                int percent = (int)(downloaded * 100 / totalBytes.Value);
                if (percent != lastPercent && percent % 10 == 0)
                {
                    Console.WriteLine($"[Dependencies] {name}: {percent}% ({downloaded / 1024 / 1024}MB / {totalBytes.Value / 1024 / 1024}MB)");
                    lastPercent = percent;
                }
            }
        }

        fileStream.Close();
        await Task.Delay(500); // Let antivirus release

        // Move with retry (antivirus may briefly hold the file)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Logger.Info($"[Dependencies] File locked, retrying in 1s (attempt {attempt + 1}/3)...");
                await Task.Delay(1000);
            }
        }
    }

    private static string? FindExistingAdb()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "scrcpy", "adb.exe"),
        };

        foreach (var path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
        }

        // Check PATH via 'where'
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("where", "adb.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(firstLine) && File.Exists(firstLine))
                    return firstLine;
            }
        }
        catch { }

        return null;
    }

    private static void AddToPath(string directory)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!currentPath.Contains(directory, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", directory + ";" + currentPath);
        }
    }

    private static void CleanupTempFiles(string zipPath, string? tempDir)
    {
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }
}
