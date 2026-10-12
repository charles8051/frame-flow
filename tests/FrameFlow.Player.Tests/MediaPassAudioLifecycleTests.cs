// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass ends what it started on its audio sink (#609): it drains the sink at a natural end,
/// before <see cref="MediaPass.RunToCompletionAsync"/> returns, and deactivates it on every way
/// out.
/// </summary>
/// <remarks>
/// Every wait here is on a signal the sink raises when the pass calls it. The timeouts only
/// bound a failure.
/// </remarks>
public sealed class MediaPassAudioLifecycleTests
{
    private const string AudioFile = "test-audio-aac.m4a";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The run returns once the sink has drained, not once it was handed the last buffer, and
    /// deactivates the sink after the drain rather than across it.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunToCompletion_AwaitsTheDrain_ThenDeactivates()
    {
        var sink = new LifecycleAudioSink { HoldDrain = true };
        await using var pass = await BuildAsync(sink);

        var run = pass.RunToCompletionAsync();
        var first = await Task.WhenAny(sink.DrainStarted, run).WaitAsync(Bound);
        Assert.True(first == sink.DrainStarted, "The run returned without draining the audio sink.");

        // The run is waiting on the drain the sink has not finished.
        Assert.False(run.IsCompleted, "The run returned while the sink was still draining.");

        sink.ReleaseDrain();
        await run.WaitAsync(Bound);

        Assert.Equal(1, sink.ActivateCount);
        Assert.Equal(1, sink.DrainCount);
        Assert.Equal(1, sink.DeactivateCount);
        Assert.False(sink.DeactivatedWhileDraining, "The pass deactivated the sink mid-drain.");
        Assert.True(sink.PresentCount > 0);
    }

    /// <summary>
    /// A cancelled run stops where it is: no drain, and the sink is still deactivated.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunCancelled_DeactivatesTheSink_WithoutDraining()
    {
        var sink = new LifecycleAudioSink { HoldPresent = true };
        await using var pass = await BuildAsync(sink);

        using var cts = new CancellationTokenSource();
        var run = pass.RunToCompletionAsync(cts.Token);
        await sink.FirstPresent.WaitAsync(Bound);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(0, sink.DrainCount);
        Assert.Equal(1, sink.DeactivateCount);
    }

    /// <summary>
    /// A run whose sink faults deactivates the sink and reports the sink's fault, not anything
    /// the clean-up ran into.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunFaulted_DeactivatesTheSink_AndReportsTheFault()
    {
        var sink = new LifecycleAudioSink { FailPresent = true, FailDeactivate = true };
        await using var pass = await BuildAsync(sink);

        var fault = await Assert.ThrowsAsync<GraphFaultException>(
            () => pass.RunToCompletionAsync().WaitAsync(Bound)
        );

        Assert.IsType<InvalidOperationException>(fault.InnerException);
        Assert.Equal(LifecycleAudioSink.PresentFailure, fault.InnerException!.Message);
        Assert.Equal(0, sink.DrainCount);
        Assert.Equal(1, sink.DeactivateCount);
    }

    /// <summary>
    /// The drain observes the run's token, so a caller can stop waiting for the tail; the sink
    /// is deactivated either way.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunCancelledWhileDraining_DeactivatesTheSink()
    {
        var sink = new LifecycleAudioSink { HoldDrain = true };
        await using var pass = await BuildAsync(sink);

        using var cts = new CancellationTokenSource();
        var run = pass.RunToCompletionAsync(cts.Token);
        var first = await Task.WhenAny(sink.DrainStarted, run).WaitAsync(Bound);
        Assert.True(first == sink.DrainStarted, "The run returned without draining the audio sink.");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(1, sink.DeactivateCount);
    }

    private static async Task<MediaPass> BuildAsync(IAudioSink sink)
    {
        var path = TestEnvironment.GetCorpusFile(AudioFile);
        Assert.NotNull(path);
        return await FrameFlowPass.Create(path!).WithAudioSink(sink).BuildAsync();
    }

    /// <summary>Records the lifecycle calls a pass makes, and holds or fails them on request.</summary>
    private sealed class LifecycleAudioSink : IAudioSink
    {
        public const string PresentFailure = "The sink failed to present.";

        private readonly TaskCompletionSource _drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drainReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstPresent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new();
        private int _activateCount;
        private int _presentCount;
        private int _drainCount;
        private int _deactivateCount;
        private int _draining;
        private int _deactivatedWhileDraining;

        public bool HoldDrain { get; init; }
        public bool HoldPresent { get; init; }
        public bool FailPresent { get; init; }
        public bool FailDeactivate { get; init; }

        public Task DrainStarted => _drainStarted.Task;
        public Task FirstPresent => _firstPresent.Task;
        public int ActivateCount => Volatile.Read(ref _activateCount);
        public int PresentCount => Volatile.Read(ref _presentCount);
        public int DrainCount => Volatile.Read(ref _drainCount);
        public int DeactivateCount => Volatile.Read(ref _deactivateCount);
        public bool DeactivatedWhileDraining => Volatile.Read(ref _deactivatedWhileDraining) != 0;

        public void ReleaseDrain() => _drainReleased.TrySetResult();

        public async ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            Interlocked.Increment(ref _presentCount);
            _firstPresent.TrySetResult();
            if (FailPresent)
                throw new InvalidOperationException(PresentFailure);
            if (HoldPresent)
                await _never.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _drainCount);
            Volatile.Write(ref _draining, 1);
            try
            {
                _drainStarted.TrySetResult();
                if (HoldDrain)
                    await _drainReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _draining, 0);
            }
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activateCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deactivateCount);
            if (Volatile.Read(ref _draining) != 0)
                Volatile.Write(ref _deactivatedWhileDraining, 1);
            if (FailDeactivate)
                throw new InvalidOperationException("Deactivation failed too.");
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
