#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Infrastructure.Network.Telemetry;

/// <summary>
/// Fire-and-forget POST of ConnectionTelemetrySnapshot to the relay's
/// POST /telemetry/connection (contract-v1). Mirrors the plain-HttpClient pattern already
/// used elsewhere in Infrastructure/Network (e.g. RelayClient) but keeps its OWN short-lived
/// client + short timeout, since telemetry must never share fate with — or block on — the
/// signaling/relay connection.
///
/// Hard invariant: a failed/slow/unreachable telemetry POST NEVER surfaces an exception to
/// the caller and NEVER affects connection or streaming state. Every code path here is
/// wrapped so the worst case is a swallowed exception + a debug log line.
/// </summary>
public static class HostTelemetryReporter
{
    private const int PostTimeoutMs = 2000;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(PostTimeoutMs)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Monotonic sequence counters keyed by "sessionId|pcRole|monitorIndex" — matches the
    /// contract's "monotonic per (session_id, pc_role, monitor_index)" requirement. Lives for
    /// the process lifetime; a fresh Host process naturally starts every key back at 1, which
    /// is fine because session_id itself is fresh per connection.
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> SequenceCounters = new();

    /// <summary>
    /// Upper bound on tracked (sessionId, pcRole, monitorIndex) keys. Each connection uses a
    /// fresh session_id, so on a long-lived Host these keys would otherwise grow without bound.
    /// A running Host has at most a handful of concurrent PCs, so this cap is far above real
    /// usage; when exceeded (only reachable after thousands of past sessions) the whole table is
    /// cleared. A cleared active session simply restarts its sequence at 1 — benign, since
    /// sequence is only a telemetry ordering hint and the relay clamps it anyway.
    /// </summary>
    private const int MaxTrackedSequenceKeys = 1024;

    /// <summary>Next monotonic sequence number for one (sessionId, pcRole, monitorIndex) tuple.</summary>
    public static long NextSequence(string sessionId, string pcRole, int monitorIndex)
    {
        if (SequenceCounters.Count >= MaxTrackedSequenceKeys)
            SequenceCounters.Clear();

        var key = $"{sessionId}|{pcRole}|{monitorIndex}";
        return SequenceCounters.AddOrUpdate(key, 1, (_, prev) => prev + 1);
    }

    /// <summary>
    /// Queue one snapshot for delivery. Returns immediately — the actual POST (including DNS/
    /// TCP/TLS) happens on a background task. Swallows every exception; never throws.
    /// </summary>
    public static void ReportFireAndForget(ConnectionTelemetrySnapshot snapshot)
    {
        var relayUrl = RelayClientManager.Instance?.Client.RelayUrl;
        if (string.IsNullOrWhiteSpace(relayUrl))
            return; // No relay configured this session (e.g. pure-LAN mode) — nothing to report to.

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var resp = await HttpClient.PostAsync($"{relayUrl.TrimEnd('/')}/telemetry/connection", content)
                    .ConfigureAwait(false);
                // Endpoint always returns 204 regardless of payload validity (contract-v1) —
                // nothing else to check; a non-2xx here is still telemetry-only, so it is
                // logged at Debug level and otherwise ignored.
                if (!resp.IsSuccessStatusCode)
                    Logger.Debug($"[HostTelemetryReporter] Non-success status {resp.StatusCode} (ignored)");
            }
            catch (Exception ex)
            {
                // Network errors, timeouts, DNS failures, relay down, etc. — telemetry must
                // never affect the connection, so this is the end of the line for the error.
                Logger.Debug($"[HostTelemetryReporter] Report failed (ignored, telemetry only): {ex.Message}");
            }
        });
    }
}
