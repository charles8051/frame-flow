// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using FrameFlow.Decoding;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Step = FrameFlow.Player.MediaPass.Step;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins what a pass's dispose does to its run (#696): it cancels a run in flight and waits for it
/// to end before it frees the pump pipeline, the decoders and the demux session, and releases the
/// claims on the sinks after that. A run cannot start once the dispose has begun, and the caller
/// awaiting a run the dispose cancelled sees the cancellation. A second dispose waits for the first
/// and ends as it did; a dispose held by work that ignores its token says once what it waits for;
/// and what freeing throws is thrown once the claims are released (ADR-0086, decision 3).
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
    private const string AvFile = "test-av-h264-aac.mp4";
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
        var logs = new RecordingLoggerFactory();
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink, logs);
        var steps = StepLog.On(pass);
        string? holderAtRunEnd = null;
        string? holderAtDemux = null;
        steps.At(Step.RunEnded, () => holderAtRunEnd = sink.Claimant);
        steps.At(Step.DisposingDemux, () => holderAtDemux = sink.Claimant);

        var run = pass.RunToCompletionAsync();
        await sink.Entered.WaitAsync(Bound); // a frame is in the sink, so the graph is running

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Equal(Holder, holderAtRunEnd); // still claimed when the run ended
        Assert.Equal(Holder, holderAtDemux); // and while the pass frees what the run used
        Assert.Null(sink.Claimant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Empty(logs.WarningsFrom<MediaPass>()); // a cancellation is not a fault
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARunThatFaultsAsTheDisposeCancelsIt_IsLoggedOnce_AndTheDisposeDoesNotThrow()
    {
        // The sink throws when its token is cancelled, so the run the dispose cancels ends in a
        // fault. The run's caller sees the fault; the dispose logs it and returns.
        var logs = new RecordingLoggerFactory();
        await using var sink = new HoldingVideoSink { FailOnCancel = true };
        var pass = await BuildVideoPassAsync(sink, logs);

        var run = pass.RunToCompletionAsync();
        await sink.Entered.WaitAsync(Bound);

        var thrown = await Record.ExceptionAsync(() => pass.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.Null(thrown);
        var fault = await Assert.ThrowsAsync<GraphFaultException>(() => run.WaitAsync(Bound));
        Assert.IsType<InvalidOperationException>(fault.InnerException);
        var warning = Assert.Single(logs.WarningsFrom<MediaPass>());
        Assert.Same(fault, warning.Exception);
        Assert.Null(sink.Claimant);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeWhileThePumpIsParked_CancelsTheRun_AndFreesThePipelineAfterIt()
    {
        // The sink holds the first frame, so the decoder stops draining its one-packet queue and
        // the pump parks on a write it cannot finish. Cancelling that write leaves the packet with
        // the pipeline, which frees it, so the pipeline must not be freed while the pump runs.
        await using var sink = new HoldingVideoSink();
        var pass = await ((PassBuilder)FrameFlowPass.Create(Corpus(VideoFile)))
            .WithDecoderOptions(video: new VideoDecoderOptions { PacketQueueCapacity = 1 })
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();
        var steps = StepLog.On(pass);

        var run = pass.RunToCompletionAsync();
        var parked = pass.WaitUntilPumpParkedAsync();
        Assert.Same(parked, await Task.WhenAny(parked, run).WaitAsync(Bound));

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Null(sink.Claimant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeOfAPassWithAudioAndVideo_EndsTheRun_ThenFreesBothDecoders()
    {
        await using var video = new HoldingVideoSink();
        var audio = new DrainHoldingAudioSink();
        var pass = await FrameFlowPass
            .Create(Corpus(AvFile))
            .WithVideoSink(video)
            .WithAudioSink(audio)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();
        var steps = StepLog.On(pass);

        var run = pass.RunToCompletionAsync();
        await video.Entered.WaitAsync(Bound);

        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Step[] expected =
        [
            Step.RunStarted,
            Step.RunEnded,
            Step.DisposingPipeline,
            Step.DisposingVideoDecoder,
            Step.DisposingAudioDecoder,
            Step.DisposingDemux,
            Step.ReleasingClaims,
        ];
        Assert.Equal(expected, steps.Taken);
        Assert.Equal(1, audio.DeactivateCount);
        Assert.Null(video.Claimant);
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

    [RequiresFfmpegAndCorpusFact]
    public async Task ARunWhoseStartThrows_StillEnds_SoADisposeStillReturns()
    {
        // Whatever throws once a run has been let start, the run ends, and a dispose waiting on it
        // is released. The seam's own callback is the one thing that can throw there.
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        steps.At(Step.RunStarted, () => throw new InvalidOperationException("The step failed."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.RunToCompletionAsync().WaitAsync(Bound));
        await pass.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Null(sink.Claimant);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeHeldByASinkThatIgnoresItsToken_SaysOnceWhatTheRunIsStillIn_AndFreesNothing()
    {
        // The sink holds its frame whatever its token says, so the run cannot end before the test
        // releases it, and neither can the dispose. The warning's interval runs on a fake clock,
        // whose timers fire inside Advance.
        var logs = new RecordingLoggerFactory();
        var time = new FakeTimeProvider();
        await using var sink = new HoldingVideoSink { IgnoreToken = true };
        var pass = await BuildVideoPassAsync(sink, logs);
        pass.TimeProvider = time;
        var steps = StepLog.On(pass);
        var interval = MediaPass.DisposeWarningInterval;

        var run = pass.RunToCompletionAsync();
        await sink.Entered.WaitAsync(Bound);
        var disposing = pass.DisposeAsync().AsTask();

        time.Advance(interval - TimeSpan.FromTicks(1));
        Assert.Empty(logs.WarningsFrom<MediaPass>());
        time.Advance(TimeSpan.FromTicks(1));
        var warning = Assert.Single(logs.WarningsFrom<MediaPass>());
        Assert.Contains("still running:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("graph", warning.Message, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(logs.WarningsFrom<MediaPass>());

        // However long it has waited, nothing the run uses has been freed.
        Assert.False(disposing.IsCompleted);
        Assert.Equal([Step.RunStarted], steps.Taken);
        Assert.Equal(Holder, sink.Claimant);

        sink.Release();
        await disposing.WaitAsync(Bound);
        Assert.Equal(VideoRunThenTeardown, steps.Taken);
        Assert.Null(sink.Claimant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task WhatFreeingThrows_IsThrownOnceTheClaimsAreReleased_AndEveryResourceIsStillFreed()
    {
        // A step's callback that throws stands in for a free that throws: what either throws is
        // collected, and the frees after it still run.
        await using var sink = new HoldingVideoSink();
        var pass = await BuildVideoPassAsync(sink);
        var steps = StepLog.On(pass);
        var pipeline = new InvalidOperationException("freeing the pipeline failed");
        var demux = new InvalidOperationException("freeing the demux session failed");
        steps.At(Step.DisposingPipeline, () => throw pipeline);
        steps.At(Step.DisposingDemux, () => throw demux);

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => pass.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.Equal([pipeline, demux], thrown.InnerExceptions);
        Assert.Equal(VideoTeardown, steps.Taken);
        Assert.Null(sink.Claimant);

        // A later dispose tears nothing down again, and ends as the first did.
        var again = await Assert.ThrowsAsync<AggregateException>(() => pass.DisposeAsync().AsTask().WaitAsync(Bound));
        Assert.Same(thrown, again);
        Assert.Equal(VideoTeardown, steps.Taken);
    }

    private static string Corpus(string name) =>
        TestEnvironment.GetCorpusFile(name) ?? throw new InvalidOperationException($"{name} is missing.");

    private static Task<MediaPass> BuildVideoPassAsync(IVideoSink sink, ILoggerFactory? logs = null) =>
        FrameFlowPass
            .Create(Corpus(VideoFile))
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithLogger(logs)
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
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Hold { get; init; } = true;

        /// <summary>When set, a held call ignores its token and holds until <see cref="Release"/>.</summary>
        public bool IgnoreToken { get; init; }

        public void Release() => _released.TrySetResult();

        /// <summary>When set, a held call throws <see cref="InvalidOperationException"/> as its token cancels.</summary>
        public bool FailOnCancel { get; init; }

        public Task Entered => _entered.Task;

        public IDisposable? TryClaim(string claimant) => _claims.TryClaim(claimant);

        public string? Claimant => _claims.Claimant;

        public long ClaimOrder => _claims.ClaimOrder;

        public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            _entered.TrySetResult();
            if (!Hold)
                return;
            if (IgnoreToken)
            {
                await _released.Task.ConfigureAwait(false);
                return;
            }
            try
            {
                await _never.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (FailOnCancel)
            {
                throw new InvalidOperationException("The sink failed as it was cancelled.");
            }
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

    /// <summary>Records what is logged at Information and above: its category, level, exception and message.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> WarningsFrom<T>() =>
            [.. _entries.Where(e => e.Category == typeof(T).FullName && e.Level >= LogLevel.Warning)];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        public sealed record Entry(string Category, LogLevel Level, Exception? Exception, string Message);

        private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (IsEnabled(logLevel))
                    entries.Enqueue(new Entry(category, logLevel, exception, formatter(state, exception)));
            }
        }
    }
}
