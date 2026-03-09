#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Manages a Cloudflare Quick Tunnel (trycloudflare.com) for zero-config internet access.
/// Downloads cloudflared binary if needed, starts tunnel, captures public URL.
/// </summary>
public class CloudflareTunnel : IDisposable
{
    private Process? _process;
    private string? _tunnelUrl;
    private bool _disposed;

    private static readonly Regex TunnelUrlRegex = new(
        @"https://[\w-]+\.trycloudflare\.com",
        RegexOptions.Compiled);

    private const string BinaryName = "cloudflared.exe";
    private const string DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    public string? TunnelUrl => _tunnelUrl;
    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Ensure cloudflared binary exists. Downloads from GitHub if missing.
    /// </summary>
    public static async Task<string> EnsureBinaryAsync()
    {
        var binDir = Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(binDir);
        var binaryPath = Path.Combine(binDir, BinaryName);

        if (File.Exists(binaryPath))
            return binaryPath;

        Logger.Info("[Tunnel] Downloading cloudflared...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlayServer/1.0");

        var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var tempPath = binaryPath + ".tmp";
        try
        {
            // Clean up any leftover temp file from previous failed download
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
                await Task.Delay(500); // Let antivirus release
            }

            // Download to temp file
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file);
            }
            // Stream is closed here — wait briefly for antivirus to release lock
            await Task.Delay(500);

            // Move with retry (antivirus may briefly hold the file)
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(binaryPath)) File.Delete(binaryPath);
                    File.Move(tempPath, binaryPath);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Logger.Info($"[Tunnel] File locked, retrying in 1s (attempt {attempt + 1}/3)...");
                    await Task.Delay(1000);
                }
            }
            Logger.Info("[Tunnel] cloudflared downloaded successfully");
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        return binaryPath;
    }

    /// <summary>
    /// Start a quick tunnel pointing to the local server.
    /// Returns the public tunnel URL (https://xxx.trycloudflare.com).
    /// </summary>
    public async Task<string?> StartAsync(int localPort, CancellationToken ct = default)
    {
        var binaryPath = await EnsureBinaryAsync();

        var psi = new ProcessStartInfo(binaryPath, $"tunnel --url http://localhost:{localPort}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            Logger.Error("[Tunnel] Failed to start cloudflared process");
            return null;
        }

        // cloudflared prints the tunnel URL to stderr
        var urlTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var match = TunnelUrlRegex.Match(e.Data);
            if (match.Success && _tunnelUrl == null)
            {
                _tunnelUrl = match.Value;
                urlTcs.TrySetResult(_tunnelUrl);
            }

            // Log cloudflared output at debug level
            if (e.Data.Contains("ERR") || e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
                Logger.Error($"[Tunnel] {e.Data}");
        };

        // Also check stdout (some versions print there)
        _process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var match = TunnelUrlRegex.Match(e.Data);
            if (match.Success && _tunnelUrl == null)
            {
                _tunnelUrl = match.Value;
                urlTcs.TrySetResult(_tunnelUrl);
            }
        };

        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        // Wait for URL with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var completedTask = await Task.WhenAny(
                urlTcs.Task,
                Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (urlTcs.Task.IsCompleted)
            {
                Logger.Info($"[Tunnel] Active: {_tunnelUrl}");
                return _tunnelUrl;
            }
        }
        catch (OperationCanceledException) { }

        // Timeout or cancelled
        Logger.Error("[Tunnel] Timed out waiting for tunnel URL");
        Stop();
        return null;
    }

    /// <summary>
    /// Stop the tunnel process gracefully.
    /// </summary>
    public void Stop()
    {
        if (_process == null) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"[Tunnel] Stop error: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _tunnelUrl = null;
        }

        Logger.Info("[Tunnel] Stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
