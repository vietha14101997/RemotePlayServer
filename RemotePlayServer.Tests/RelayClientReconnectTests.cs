using System.Reflection;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Tests;

/// <summary>
/// Phase 2 (Signaling Resilience) — RelayClient's reconnect state machine.
/// The backoff loop and state setter are private (RelayClient.Reconnect.cs), so these tests
/// invoke them via reflection rather than standing up a real relay/WebSocket server — that
/// keeps the tests fast/deterministic while still exercising the exact single-flight guard
/// and cancellation wiring that ship in RelayClient.
/// NOTE: RelayClient/RelayClient.Tests target net9.0-windows (WPF host + native deps) — cannot
/// build/run on macOS. VM-verify-pending; not executable on this dev machine.
/// </summary>
public class RelayClientReconnectTests
{
    private static Task InvokeTryStartReconnectLoop(RelayClient client)
    {
        var method = typeof(RelayClient).GetMethod("TryStartReconnectLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("TryStartReconnectLoopAsync not found — did RelayClient.Reconnect.cs get renamed?");
        return (Task)method.Invoke(client, null)!;
    }

    private static void InvokeSetState(RelayClient client, RelayClient.RelayConnectionState state)
    {
        var method = typeof(RelayClient).GetMethod("SetState", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SetState not found — did RelayClient.Reconnect.cs get renamed?");
        method.Invoke(client, new object[] { state });
    }

    [Fact]
    public void TryStartReconnectLoopAsync_SecondConcurrentCall_ReturnsImmediately_SingleFlight()
    {
        using var client = new RelayClient();

        // First call starts the backoff loop and suspends on Task.Delay (it never actually
        // connects — no login/device-id was set, so ConnectPresenceAsync no-ops), so its Task
        // stays pending until Dispose() cancels it below.
        var loopTask = InvokeTryStartReconnectLoop(client);

        // A second, concurrent call must observe the single-flight guard and return synchronously.
        var secondCallTask = InvokeTryStartReconnectLoop(client);

        Assert.True(secondCallTask.IsCompletedSuccessfully,
            "a second concurrent reconnect attempt must be a no-op (single-flight guard)");

        client.Dispose();
        Assert.True(loopTask.Wait(TimeSpan.FromSeconds(5)), "reconnect loop did not stop after Dispose()");
    }

    [Fact]
    public void Dispose_BeforeAnyConnection_DoesNotThrow_AndLeavesStateDisconnected()
    {
        var client = new RelayClient();

        Assert.Equal(RelayClient.RelayConnectionState.Disconnected, client.State);

        client.Dispose();
        client.Dispose(); // idempotent — a second Dispose() must be a no-op, not throw

        Assert.Equal(RelayClient.RelayConnectionState.Disconnected, client.State);
    }

    [Fact]
    public void ReconnectLoop_UserDisconnectMidBackoff_StopsLoop_EndsDisconnected()
    {
        using var client = new RelayClient();
        var stateHistory = new List<RelayClient.RelayConnectionState>();
        client.OnRelayStateChanged += stateHistory.Add;

        // Mirror what ReadLoopAsync does on a transport drop: mark Reconnecting, then start
        // the single-flight backoff loop.
        InvokeSetState(client, RelayClient.RelayConnectionState.Reconnecting);
        var loopTask = InvokeTryStartReconnectLoop(client);

        // Simulate the user explicitly disconnecting while a backoff wait is in flight.
        client.Dispose();

        Assert.True(loopTask.Wait(TimeSpan.FromSeconds(5)), "reconnect loop did not stop after Dispose()");
        Assert.Equal(RelayClient.RelayConnectionState.Disconnected, client.State);
        Assert.Equal(
            new[] { RelayClient.RelayConnectionState.Reconnecting, RelayClient.RelayConnectionState.Disconnected },
            stateHistory);
    }
}
