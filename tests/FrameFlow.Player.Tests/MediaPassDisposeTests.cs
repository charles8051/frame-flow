// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using FrameFlow.Decoding;
using FrameFlow.Media;
using Step = FrameFlow.Player.MediaPass.Step;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins what a pass's dispose does to its run (#696): it cancels a run in flight and waits for it
/// to end before it frees the pump pipeline, the decoders and the demux session, and releases the
/// claims on the sinks after that. A run cannot start once the dispose has begun, and the caller
/// awaiting a run the dispose cancelled sees the cancellation.
/// </summary>
/// <remarks>
/// The order is read from the steps the pass reports through <see cref="MediaPass.OnStep"/>, and a
/// race is driven by acting at one of them. Every wait is on a signal the pass or a sink raises;
/// the bound only limits a failure.
/// </remarks>
public sealed class MediaPassDisposeTests
{
    private const string VideoFile = "test-video-h264-yuv420p.mp4";
    private const string AudioFile = "test-audio-aac.m4a";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly Step[] VideoTeardown =
    [
        Step.DisposingPipeline,
        Step.DisposingVideoDecoder,
        Step.DisposingDemux,
        Step.ReleasingClaims,
    ];

    private static readonly Step[] VideoRunThenTeardown = [Step.RunStarted, Step.RunEnded, .. VideoTeardown];

    private static readonly Step[] AudioRunThenTeardown =
    [
        Step.RunStarted,
        Step.RunEnded,
        Step.DisposingPipeline,
        Step.DisposingAudioDecoder,
        Step.DisposingDemux,
        Step.ReleasingClaims,
    ];

    private static readonly string Holder = $"a pass over '{VideoFile}'";

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeWhileTheGraphRuns_CancelsTheRun_AndFreesNothingUntilItHasEnded()
    {
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        string? holderAtRunEnd = null;
        steps.At(Step.RunEnded, () => holderAtRunEnd = sink.Claimant);

        var run = pass.RunToCompletionAsync();
        await sink.Entered.WaitAsync(Bound); // a frame is in the sink, so the graph is running

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Equal(Holder, holderAtRunEnd); // still claimed when the run ended
        Assert.Null(sink.Claimant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARunDisposedWhileItDrains_ReportsCancellation_NotSuccess()
    {
        // The graph has finished and the run waits for the device to play out. The dispose
        // cancels that wait, so the device finishing afterwards does not make the run a success.
        var sink = new DrainHoldingAudioSink();
        var pass = await FrameFlowPass.Create(Corpus(AudioFile)).WithAudioSink(sink).BuildAsync();
        var steps = StepLog.On(pass);

        var run = pass.RunToCompletionAsync();
        var first = await Task.WhenAny(sink.DrainStarted, run).WaitAsync(Bound);
        Assert.True(first == sink.DrainStarted, "The run ended without draining the audio sink.");

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);
        sink.ReleaseDrain();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Equal(AudioRunThenTeardown, steps.Taken);
        Assert.Equal(1, sink.DeactivateCount);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeBeforeAnyRun_TearsDownAtOnce_AndARunAfterItIsRefused()
    {
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoTeardown, steps.Taken);
        Assert.Null(sink.Claimant);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pass.RunToCompletionAsync());
        Assert.Equal(VideoTeardown, steps.Taken); // the refused run took no step
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeAfterTheRunEnded_TearsDown_AndReleasesTheClaim()
    {
        await using var sink = new HoldingVideoSink { Hold = false };
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        await pass.RunToCompletionAsync().WaitAsync(Bound);

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Null(sink.Claimant);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASecondDispose_DoesNothing()
    {
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);
        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoTeardown, steps.Taken);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeWhileAnotherIsUnderWay_WaitsForItToFinish()
    {
        // The second dispose is made as the run ends, while the first waits for that run.
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        Task? second = null;
        var secondDoneAtRunEnd = true;
        steps.At(
            Step.RunEnded,
            () =>
            {
                second = pass.DisposeAsync().AsTask();
                secondDoneAtRunEnd = second.IsCompleted;
            }
        );

        var run = pass.RunToCompletionAsync();
        await sink.Entered.WaitAsync(Bound);
        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.NotNull(second);
        Assert.False(secondDoneAtRunEnd, "The second dispose returned before the first had torn down.");
        await second!.WaitAsync(Bound);
        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARunStartedWhileTheDisposeIsUnderWay_IsRefused()
    {
        // The dispose wins the race: the run is asked for once the dispose has begun, before it
        // has freed anything.
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        Task? lateRun = null;
        steps.At(Step.DisposingPipeline, () => lateRun = pass.RunToCompletionAsync());

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.NotNull(lateRun);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lateRun!.WaitAsync(Bound));
        Assert.Equal(VideoTeardown, steps.Taken);
        Assert.Null(sink.Claimant);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeAsTheRunStarts_CancelsIt_AndWaitsForIt()
    {
        // The run wins the race: the dispose arrives after the run was let start, before the run
        // has linked the pass's token. The sink does not hold, so a run left alone would succeed.
        await using var sink = new HoldingVideoSink { Hold = false };
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        Task? disposing = null;
        var disposeDoneAtRunStart = true;
        string? holderAtRunEnd = null;
        steps.At(
            Step.RunStarted,
            () =>
            {
                disposing = pass.DisposeAsync().AsTask();
                disposeDoneAtRunStart = disposing.IsCompleted;
            }
        );
        steps.At(Step.RunEnded, () => holderAtRunEnd = sink.Claimant);

        var run = pass.RunToCompletionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.NotNull(disposing);
        await disposing!.WaitAsync(Bound);
        Assert.False(disposeDoneAtRunStart, "The dispose returned before the run it raced had ended.");
        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Equal(Holder, holderAtRunEnd);
        Assert.Null(sink.Claimant);
    }

    private static string Corpus(string name) =>
        TestEnvironment.GetCorpusFile(name) ?? throw new InvalidOperationException($"{name} is missing.");

    private static Task<MediaPass> BuildVideoPassAsync(IVideoSink sink) =>
        FrameFlowPass
            .Create(Corpus(VideoFile))
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

    /// <summary>The steps a pass takes, in order, with an action to run at any of them.</summary>
    private sealed class StepLog
    {
        private readonly ConcurrentQueue<Step> _taken = new();
        private readonly ConcurrentDictionary<Step, Action> _actions = new();

        public static StepLog On(MediaPass pass)
        {
            var log = new StepLog();
            pass.OnStep = log.Take;
            return log;
        }

        public Step[] Taken => [.. _taken];

        /// <summary>Runs <paramref name="action"/> as the pass takes <paramref name="step"/>, on its thread.</summary>
        public void At(Step step, Action action) => _actions[step] = action;

        private void Take(Step step)
        {
            _taken.Enqueue(step);
            if (_actions.TryGetValue(step, out var action))
                action();
        }
    }

    /// <summary>
    /// A claimable video sink that raises <see cref="Entered"/> on its first frame and, while
    /// <see cref="Hold"/> is set, holds that call until its token is cancelled.
    /// </summary>
    private sealed class HoldingVideoSink : IVideoSink, IClaimableSink
    {
        private readonly SinkClaimGate _claims = new(typeof(HoldingVideoSink));
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new();

        public bool Hold { get; init; } = true;

        public Task Entered => _entered.Task;

        public IDisposable? TryClaim(string claimant) => _claims.TryClaim(claimant);

        public string? Claimant => _claims.Claimant;

        public long ClaimOrder => _claims.ClaimOrder;

        public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            _entered.TrySetResult();
            if (Hold)
                await _never.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _claims.Close();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An audio sink whose drain holds until it is released or its token is cancelled.</summary>
    private sealed class DrainHoldingAudioSink : IAudioSink
    {
        private readonly TaskCompletionSource _drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drainReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deactivateCount;

        public Task DrainStarted => _drainStarted.Task;

        public int DeactivateCount => Volatile.Read(ref _deactivateCount);

        public void ReleaseDrain() => _drainReleased.TrySetResult();

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
        {
            _drainStarted.TrySetResult();
            await _drainReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deactivateCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
