// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Tests.Shared;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass or a player over a network source that stalls can be stopped (#709). Cancelling a pass's
/// build or its run returns, and so does disposing a pass or a player whose run waits on the
/// source. The source is a server on 127.0.0.1 that stops answering (<see cref="StalledHttpSource"/>).
/// </summary>
/// <remarks>
/// Each test acts only once the server has read the request the demuxer is waiting on, so the
/// demuxer is inside FFmpeg and only an interrupt ends its call. A regression leaves it waiting: the
/// bound turns that into a named failure, and disposing the server then ends the call before
/// anything is freed under it.
/// </remarks>
public sealed class StalledSourceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [RequiresFfmpegAndCorpusFact]
    public async Task APassBuildWaitingOnASourceThatNeverAnswers_GivesUpWhenItsTokenIsCancelled()
    {
        await using var server = StalledHttpSource.NeverAnswering();
        using var cancel = new CancellationTokenSource();

        // The open runs on the calling thread, so the build gets a thread of its own here.
        var build = Task.Run(() => PassOver(server).BuildAsync(cancel.Token), CancellationToken.None);
        await server.FirstRequest.WaitAsync(Bound);
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => build.WaitAsync(Bound));

        Assert.False(thrown is TimeoutException, "The build was still waiting on the source after its token was cancelled.");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APassRunWaitingOnAStalledSource_EndsWhenItsTokenIsCancelled()
    {
        await using var server = StalledHttpSource.StallingAfterTheHeader();
        var pass = await PassOver(server).BuildAsync().WaitAsync(Bound);
        Task? run = null;
        try
        {
            using var cancel = new CancellationTokenSource();
            run = pass.RunToCompletionAsync(cancel.Token);
            await server.Reconnected.WaitAsync(Bound); // the pump's read waits for the rest
            await cancel.CancelAsync();

            var thrown = await Record.ExceptionAsync(() => run.WaitAsync(Bound));

            Assert.False(thrown is TimeoutException, "The run was still waiting on the source after its token was cancelled.");
            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        }
        finally
        {
            await EndAsync(server, run, pass.DisposeAsync);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APassDisposedWhileItsRunWaitsOnAStalledSource_EndsTheRunAndReturns()
    {
        await using var server = StalledHttpSource.StallingAfterTheHeader();
        var pass = await PassOver(server).BuildAsync().WaitAsync(Bound);
        Task? run = null;
        Task? disposing = null;
        try
        {
            run = pass.RunToCompletionAsync();
            await server.Reconnected.WaitAsync(Bound);

            disposing = pass.DisposeAsync().AsTask();
            var thrown = await Record.ExceptionAsync(() => disposing.WaitAsync(Bound));

            Assert.False(thrown is TimeoutException, "The dispose was still waiting on the run's read of the source.");
            Assert.Null(thrown);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        }
        finally
        {
            await EndAsync(server, run, () => disposing is null ? pass.DisposeAsync() : new ValueTask(disposing));
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerDisposedWhileItsRunWaitsOnAStalledSource_Returns()
    {
        await using var server = StalledHttpSource.StallingAfterTheHeader();
        var player = await PlayerOver(server).BuildPlayerAsync().WaitAsync(Bound);
        Task? disposing = null;
        try
        {
            Assert.True((await player.PlayAsync().WaitAsync(Bound)).IsSuccess);
            await server.Reconnected.WaitAsync(Bound); // the run's read waits for the rest

            disposing = player.DisposeAsync().AsTask();
            var thrown = await Record.ExceptionAsync(() => disposing.WaitAsync(Bound));

            Assert.False(thrown is TimeoutException, "The player's dispose was still waiting on its run's read of the source.");
            Assert.Null(thrown);
        }
        finally
        {
            await EndAsync(server, run: null, () => disposing is null ? player.DisposeAsync() : new ValueTask(disposing));
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerSeekWhileItsRunWaitsOnAStalledSource_StopsTheRun_AndPlaysOnFromTheTarget()
    {
        // The seek stops the run, whose read the run's cancellation interrupts, then seeks the
        // demuxer, which reopens the source at the target. That reopening observes the seek's own
        // token, not the stopped run's, and the server serves the rest, so the item plays to its
        // end. A seek is accepted before it runs, so the end is what says it ran.
        await using var server = StalledHttpSource.StallingAfterTheHeader();
        var player = await PlayerOver(server).BuildPlayerAsync().WaitAsync(Bound);
        try
        {
            var ended = player.WaitForEndAsync();
            Assert.True((await player.PlayAsync().WaitAsync(Bound)).IsSuccess);
            await server.Reconnected.WaitAsync(Bound);

            var seek = await player.SeekAsync(TimeSpan.FromSeconds(1)).WaitAsync(Bound);
            Assert.True(seek.IsSuccess, seek.Error?.Message);
            var thrown = await Record.ExceptionAsync(() => ended.WaitAsync(Bound));

            Assert.False(thrown is TimeoutException, "The item did not end: the seek was still waiting on the run's read of the source.");
            Assert.Null(thrown);
            Assert.True(ended.Result.IsSuccess, ended.Result.Error?.Message);
        }
        finally
        {
            await EndAsync(server, run: null, player.DisposeAsync);
        }
    }

    private static IPassBuilder PassOver(StalledHttpSource server) =>
        FrameFlowPass
            .Create(server.Source)
            .WithAudioSink(new DiscardingAudioSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled);

    private static IPlayerBuilder PlayerOver(StalledHttpSource server) =>
        FrameFlowPlayer
            .Create()
            .WithMedia(server.Source)
            .WithAudioSink(new DiscardingAudioSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled);

    /// <summary>
    /// Closes the server first, which ends a read nothing interrupted, then lets the run end and
    /// the owner free what it used.
    /// </summary>
    private static async Task EndAsync(StalledHttpSource server, Task? run, Func<ValueTask> dispose)
    {
        await server.DisposeAsync();
        if (run is not null)
            await Record.ExceptionAsync(() => run.WaitAsync(Bound));
        await dispose().AsTask().WaitAsync(Bound);
    }

    /// <summary>An audio sink that takes every buffer at once and plays nothing.</summary>
    private sealed class DiscardingAudioSink : IAudioSink
    {
        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
