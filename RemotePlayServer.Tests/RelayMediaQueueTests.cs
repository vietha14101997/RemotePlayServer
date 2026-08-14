using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Application.Streaming;

namespace RemotePlayServer.Tests;

public class RelayMediaQueueTests
{
    [Fact]
    public void P2PState_RepeatedRecoveryAndTerminalFailureTracksCurrentState()
    {
        var state = new RelayP2PState();

        Assert.False(state.IsConnected);
        state.MarkConnected();
        Assert.True(state.IsConnected);
        state.MarkTerminalFailure();
        Assert.False(state.IsConnected);

        state.MarkConnected();
        Assert.True(state.IsConnected);
        state.MarkTerminalFailure();
        Assert.False(state.IsConnected);
    }

    [Fact]
    public void P2PState_StaysConnectedUntilTerminalFailureIsReported()
    {
        var state = new RelayP2PState();
        state.MarkConnected();

        // Transient disconnected callbacks do not call MarkTerminalFailure.
        Assert.True(state.IsConnected);
    }

    [Fact]
    public void P2PState_RejectsStaleAsyncTransitionAfterNewerFailure()
    {
        var state = new RelayP2PState();
        int recoveredVersion = state.MarkConnected();
        int failedVersion = state.MarkTerminalFailure();

        Assert.False(state.IsCurrent(recoveredVersion, connected: true));
        Assert.True(state.IsCurrent(failedVersion, connected: false));
    }

    [Fact]
    public async Task ClientGate_StartTextPrecedesActiveCaptureBinaryOnEveryEntry()
    {
        var events = new List<string>();

        for (int entry = 1; entry <= 2; entry++)
        {
            var startMayComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> transition = RelayClientGate.OpenAsync(
                async () =>
                {
                    events.Add($"start-{entry}-sending");
                    await startMayComplete.Task;
                    events.Add($"start-{entry}-sent");
                    return true;
                },
                () =>
                {
                    events.Add($"binary-{entry}-enabled");
                    return true;
                },
                () => Task.CompletedTask);

            await Task.Yield();
            Assert.DoesNotContain($"binary-{entry}-enabled", events);

            startMayComplete.SetResult();
            Assert.True(await transition);
        }

        Assert.Equal(
        [
            "start-1-sending", "start-1-sent", "binary-1-enabled",
            "start-2-sending", "start-2-sent", "binary-2-enabled"
        ], events);
    }

    [Fact]
    public async Task ClientGate_SendFailureOrStaleP2PGenerationNeverEnablesBinary()
    {
        bool enabled = false;
        bool rolledBack = false;

        Assert.False(await RelayClientGate.OpenAsync(
            () => Task.FromResult(false),
            () => enabled = true,
            () =>
            {
                rolledBack = true;
                return Task.CompletedTask;
            }));
        Assert.False(enabled);
        Assert.False(rolledBack);

        var state = new RelayP2PState();
        int failedVersion = state.MarkTerminalFailure();
        state.MarkConnected();

        Assert.False(await RelayClientGate.OpenAsync(
            () => Task.FromResult(true),
            () => state.TryRunIfCurrent(failedVersion, connected: false, () => enabled = true),
            () =>
            {
                rolledBack = true;
                return Task.CompletedTask;
            }));
        Assert.False(enabled);
        Assert.True(rolledBack);
    }

    [Fact]
    public void BuildRelayFrameChunks_AtProtocolLimit_EmitsCompleteFrame()
    {
        var frame = new byte[SIPSorceryStreamer.RelayMaxChunkSize * SIPSorceryStreamer.RelayMaxChunks];

        var chunks = SIPSorceryStreamer.BuildRelayFrameChunks(1, frame, isKeyframe: false, paramSets: null);

        Assert.NotNull(chunks);
        Assert.Equal(SIPSorceryStreamer.RelayMaxChunks, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal(SIPSorceryStreamer.RelayMaxChunks, chunk[3]));
    }

    [Fact]
    public void BuildRelayFrameChunks_OverProtocolLimit_DropsWholeFrame()
    {
        var frame = new byte[(SIPSorceryStreamer.RelayMaxChunkSize * SIPSorceryStreamer.RelayMaxChunks) + 1];

        var chunks = SIPSorceryStreamer.BuildRelayFrameChunks(0, frame, isKeyframe: true, paramSets: [1, 2, 3]);

        Assert.Null(chunks);
    }

    [Fact]
    public void RecoveryState_SuppressesQueuedPFramesUntilCurrentIdrCompletes()
    {
        var state = new RelayKeyframeRecoveryState(1);
        var queuedPFrame = new RelayVideoFrame(0, [], isKeyframe: false, state.CurrentGeneration(0));

        state.MarkTainted(0);

        Assert.True(state.ShouldSuppress(queuedPFrame));
        var recoveryIdr = new RelayVideoFrame(0, [], isKeyframe: true, state.CurrentGeneration(0));
        Assert.True(state.TryCompleteKeyframe(recoveryIdr));
        Assert.False(state.IsWaitingForKeyframe(0));
    }

    [Fact]
    public void RecoveryState_StaleIdrCannotClearNewerTaint()
    {
        var state = new RelayKeyframeRecoveryState(1);
        state.MarkTainted(0);
        var staleIdr = new RelayVideoFrame(0, [], isKeyframe: true, state.CurrentGeneration(0));

        state.MarkTainted(0);

        Assert.False(state.TryCompleteKeyframe(staleIdr));
        Assert.True(state.IsWaitingForKeyframe(0));
    }

    [Fact]
    public void WorkerSession_RepeatedRelayEntryRequiresFreshIdrOnEveryTrack()
    {
        using var firstSession = new RelayMediaWorkerSession(6, 2, 2, 2, CancellationToken.None);
        var firstIdr = new RelayVideoFrame(
            0, [], isKeyframe: true, firstSession.Recovery.CurrentGeneration(0), 6);
        Assert.True(firstSession.Recovery.TryCompleteKeyframe(firstIdr));
        Assert.False(firstSession.Recovery.IsWaitingForKeyframe(0));

        using var session = new RelayMediaWorkerSession(7, 2, 2, 2, CancellationToken.None);

        Assert.True(session.Recovery.IsWaitingForKeyframe(0));
        Assert.True(session.Recovery.IsWaitingForKeyframe(1));
        Assert.True(session.Recovery.ShouldSuppress(
            new RelayVideoFrame(0, [], isKeyframe: false, session.Recovery.CurrentGeneration(0), 7)));

        var idr = new RelayVideoFrame(
            0, [], isKeyframe: true, session.Recovery.CurrentGeneration(0), 7);
        Assert.True(session.Recovery.TryCompleteKeyframe(idr));
        Assert.False(session.Recovery.ShouldSuppress(
            new RelayVideoFrame(0, [], isKeyframe: false, session.Recovery.CurrentGeneration(0), 7)));
        Assert.True(session.Recovery.IsWaitingForKeyframe(1));
    }

    [Fact]
    public void DirectKeyframeGate_RelayIdrCannotUnlockNextDirectFrameChain()
    {
        var gate = new DirectMediaKeyframeGate();
        gate.Require();

        Assert.False(gate.TryComplete(relayMediaMode: true, isKeyframe: true));
        Assert.True(gate.ShouldSuppress(relayMediaMode: false, isKeyframe: false));
        Assert.True(gate.TryComplete(relayMediaMode: false, isKeyframe: true));
        Assert.False(gate.ShouldSuppress(relayMediaMode: false, isKeyframe: false));
    }

    [Fact]
    public async Task WorkerSession_StopTerminatesWorkersWithNonCancelableParentToken()
    {
        using var session = new RelayMediaWorkerSession(1, 1, 2, 2, CancellationToken.None);
        var videoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        session.Start(
            async (_, worker) =>
            {
                videoStarted.SetResult();
                await Task.Delay(Timeout.Infinite, worker.Token);
            },
            async (_, worker) =>
            {
                audioStarted.SetResult();
                await Task.Delay(Timeout.Infinite, worker.Token);
            });

        await Task.WhenAll(videoStarted.Task, audioStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(session.VideoTask.IsCompleted);
        Assert.True(session.AudioTask.IsCompleted);
        Assert.True(session.Token.IsCancellationRequested);
        Assert.False(session.VideoQueue.Writer.TryWrite(new RelayVideoFrame(0, [], false, 0, 1)));
        Assert.False(session.AudioQueue.Writer.TryWrite([]));
    }

    [Fact]
    public async Task WorkerSession_NewGenerationDoesNotConsumeOldGenerationQueue()
    {
        using var oldSession = new RelayMediaWorkerSession(1, 1, 2, 2, CancellationToken.None);
        using var newSession = new RelayMediaWorkerSession(2, 1, 2, 2, CancellationToken.None);
        oldSession.VideoQueue.Writer.TryWrite(new RelayVideoFrame(0, [], false, 0, 1));
        oldSession.Start(DrainVideoAsync, DrainAudioAsync);

        Assert.False(newSession.VideoQueue.Reader.TryRead(out _));

        await oldSession.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(oldSession.VideoQueue.Reader.TryRead(out _));
    }

    private static async Task DrainVideoAsync(
        System.Threading.Channels.ChannelReader<RelayVideoFrame> reader,
        RelayMediaWorkerSession session)
    {
        try
        {
            while (await reader.WaitToReadAsync(session.Token))
                while (reader.TryRead(out _)) { }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (reader.TryRead(out _)) { }
        }
    }

    private static async Task DrainAudioAsync(
        System.Threading.Channels.ChannelReader<byte[]> reader,
        RelayMediaWorkerSession session)
    {
        try
        {
            while (await reader.WaitToReadAsync(session.Token))
                while (reader.TryRead(out _)) { }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (reader.TryRead(out _)) { }
        }
    }
}
