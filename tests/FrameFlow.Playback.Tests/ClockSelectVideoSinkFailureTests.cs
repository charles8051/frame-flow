using FrameFlow.Playback.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// A sink that throws costs the frame it was given and is reported, and the pacer presents the
/// next frame; one that keeps throwing is reported as faulted, and delivery stops (#703). Each
/// test waits on what the pacer raises or what the sink is called with, and stops the pacer
/// before asserting what did not happen: <see cref="ClockSelectVideoSink.DisposeAsync"/> awaits
/// the delivery loop, so everything the loop was going to do has happened by then.
/// </summary>
public sealed partial class ClockSelectVideoSinkTests
{
    private const int MaxSinkFailures = PacerSinkFailures.MaxConsecutiveFailures;

    [Fact]
    public async Task ASinkThatThrowsOnce_IsReported_AndTheNextFrameIsPresented()
    {
        var sink = new ScriptedSink { FailPresent = call => call == 1 };
        var failures = new SinkFailureRecorder();
        var pacer = NewPacer(sink, failures);
        var first = new CountedFrame(TimeSpan.Zero);
        var second = new CountedFrame(TimeSpan.Zero);

        await pacer.PresentAsync(first, default);
        var error = await failures.WaitForReportAsync(1);
        await pacer.PresentAsync(second, default);
        await sink.WaitForTakenAsync(1);
        await pacer.DisposeAsync();

        Assert.Same(sink.Thrown[0], error.Inner);
        Assert.Equal(ErrorCategory.System, error.Category);
        Assert.Contains(nameof(ScriptedSink), error.Message, StringComparison.Ordinal);
        Assert.Equal(1, failures.Reports);
        Assert.Null(failures.Fault);
        Assert.Equal(1, pacer.Presented);
        Assert.Equal(0, first.References);
        Assert.Equal(0, second.References);
    }

    [Fact]
    public async Task ASinkThatThrowsOnEveryCall_IsReportedAsFaulted_AfterTheLimit_AndIsCalledNoMore()
    {
        var sink = new ScriptedSink { FailPresent = _ => true };
        var failures = new SinkFailureRecorder();
        var pacer = NewPacer(sink, failures);
        var frames = new List<CountedFrame>();

        for (int call = 1; call <= MaxSinkFailures + 1; call++)
        {
            var frame = new CountedFrame(TimeSpan.Zero);
            frames.Add(frame);
            await pacer.PresentAsync(frame, default);
            await sink.WaitForCallsAsync(call);
        }
        var fault = await failures.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));

        // Delivery has stopped: a frame given now stays in the ring, and the teardown releases it.
        var afterTheFault = new CountedFrame(TimeSpan.Zero);
        await pacer.PresentAsync(afterTheFault, default);
        await pacer.DisposeAsync();

        Assert.Equal(MaxSinkFailures + 1, sink.Calls);
        Assert.Equal(MaxSinkFailures, failures.Reports);
        Assert.Equal(1, failures.Faults);
        var wrapped = Assert.IsType<InvalidOperationException>(fault);
        Assert.Same(sink.Thrown[^1], wrapped.InnerException);
        Assert.Contains(nameof(ScriptedSink), wrapped.Message, StringComparison.Ordinal);
        Assert.All(frames, f => Assert.Equal(0, f.References));
        Assert.Equal(0, afterTheFault.References);
    }

    [Fact]
    public async Task FailuresSeparatedByAFramePresented_DoNotEscalate()
    {
        // Two runs of as many failures as are tolerated, each ended by a frame the sink takes.
        int calls = 2 * (MaxSinkFailures + 1);
        var sink = new ScriptedSink { FailPresent = call => call % (MaxSinkFailures + 1) != 0 };
        var failures = new SinkFailureRecorder();
        var pacer = NewPacer(sink, failures);

        for (int call = 1; call <= calls; call++)
        {
            await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
            await sink.WaitForCallsAsync(call);
        }
        await sink.WaitForTakenAsync(2);
        await pacer.DisposeAsync();

        Assert.Null(failures.Fault);
        Assert.Equal(2 * MaxSinkFailures, failures.Reports);
    }

    [Fact]
    public async Task AFormatTheSinkRefusesOnce_IsAnnouncedAgain_BeforeAnyFrameOfItIsPresented()
    {
        var sink = new ScriptedSink { FailAnnouncement = call => call == 1 };
        var failures = new SinkFailureRecorder();
        var announcer = new VideoFormatAnnouncer();
        var pacer = NewPacer(sink, failures, announcer);
        var refused = new CountedFrame(TimeSpan.Zero);
        var next = new CountedFrame(TimeSpan.Zero);

        await pacer.PresentAsync(refused, default);
        var error = await failures.WaitForReportAsync(1);
        // The refusal left the sink's last format in place, so this frame asks again first.
        Assert.Null(announcer.Announced);
        await pacer.PresentAsync(next, default);
        await sink.WaitForTakenAsync(1);
        await pacer.DisposeAsync();

        Assert.Same(sink.Thrown[0], error.Inner);
        Assert.Equal(2, sink.Announcements);
        Assert.Equal(1, sink.Calls);
        Assert.Equal(new VideoFormatInfo(320, 240, PixelFormat.Bgra32), announcer.Announced);
        Assert.Null(failures.Fault);
        Assert.Equal(0, refused.References);
        Assert.Equal(0, next.References);
    }

    [Fact]
    public async Task ACancellationTheSinkRaisesWhileThePacerRuns_IsAFailureLikeAnyOther()
    {
        var sink = new ScriptedSink
        {
            FailPresent = call => call == 1,
            Failure = () => new OperationCanceledException("the sink's own"),
        };
        var failures = new SinkFailureRecorder();
        var pacer = NewPacer(sink, failures);

        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        var error = await failures.WaitForReportAsync(1);
        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await sink.WaitForTakenAsync(1);
        await pacer.DisposeAsync();

        Assert.IsType<OperationCanceledException>(error.Inner);
        Assert.Null(failures.Fault);
    }

    [Fact]
    public async Task AfterTheLastFrameFails_TheEndOfContentHoldStillEndsTheRun()
    {
        var clock = new FakeClock();
        var sink = new ScriptedSink { FailPresent = call => call == 2 };
        var failures = new SinkFailureRecorder();
        await using var pacer = NewPacer(sink, failures, clock: clock, time: new FakeTimeProvider());

        pacer.BeginRun();
        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await sink.WaitForTakenAsync(1);
        await pacer.PresentAsync(new CountedFrame(TimeSpan.FromMilliseconds(33)), default);
        clock.Advance(TimeSpan.FromMilliseconds(33));
        await failures.WaitForReportAsync(1);

        var parked = pacer.ParkGeneration;
        pacer.SignalInputComplete();
        var drain = pacer.WaitForDrainAsync(default);
        await pacer.WaitForParkAfterAsync(parked);
        // The frame that failed still has its display interval, and the hold waits it out.
        Assert.False(drain.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(66));
        await AwaitDrainAsync(drain);
        Assert.Null(failures.Fault);
    }

    [Fact]
    public async Task AfterAFailure_AnEmptyRingIsStillReportedAsAnUnderrun()
    {
        var sink = new ScriptedSink { FailPresent = call => call == 1 };
        var failures = new SinkFailureRecorder();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink,
            new FakeClock(),
            capacity: 4,
            timeProvider: time,
            onStarved: edges.Starved,
            onFed: edges.Fed,
            onSinkFailed: failures.OnFailed,
            onSinkFaulted: failures.OnFaulted
        );

        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await failures.WaitForReportAsync(1);
        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await sink.WaitForTakenAsync(1);
        // Parked on the empty ring after the frame the sink took, with the starvation timer armed.
        await pacer.WaitForParkAfterAsync(parked);

        time.Advance(PacerStarvation.MinimumWait);
        await edges.WaitForStarvedAsync(1);

        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await edges.WaitForFedAsync(1);
        Assert.Equal((1, 1), edges.Counts);
    }

    [Fact]
    public async Task ASinkThatThrowsOnEveryCall_LogsOneWarningForItsFailures_AndOneForTheFault()
    {
        // The fake clock never moves, so every failure falls inside the first one's interval.
        var sink = new ScriptedSink { FailPresent = _ => true };
        var failures = new SinkFailureRecorder();
        var logger = new LevelCountingLogger();
        var pacer = new ClockSelectVideoSink(
            sink,
            new FakeClock(),
            logger,
            capacity: 4,
            timeProvider: new FakeTimeProvider(),
            onSinkFailed: failures.OnFailed,
            onSinkFaulted: failures.OnFaulted
        );

        for (int call = 1; call <= MaxSinkFailures + 1; call++)
        {
            await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
            await sink.WaitForCallsAsync(call);
        }
        await failures.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));
        await pacer.DisposeAsync();

        Assert.Equal(2, logger.Count(LogLevel.Warning));
        Assert.Equal(MaxSinkFailures - 1, logger.Count(LogLevel.Debug, "threw"));
    }

    private static ClockSelectVideoSink NewPacer(
        IVideoSink sink,
        SinkFailureRecorder failures,
        VideoFormatAnnouncer? announcer = null,
        FakeClock? clock = null,
        TimeProvider? time = null
    ) =>
        new(
            sink,
            clock ?? new FakeClock(),
            capacity: 4,
            timeProvider: time,
            formatAnnouncer: announcer,
            onSinkFailed: failures.OnFailed,
            onSinkFaulted: failures.OnFaulted
        );

    /// <summary>
    /// A sink whose calls fail as the test says, counted from 1. It owns every frame it is given,
    /// so it releases each one whether the call fails or not.
    /// </summary>
    private sealed class ScriptedSink : IVideoSink
    {
        private readonly object _lock = new();
        private readonly List<Exception> _thrown = [];
        private readonly List<(bool Taken, int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _calls;
        private int _taken;
        private int _announcements;

        /// <summary>Whether the present with this number throws.</summary>
        public Func<int, bool> FailPresent { get; init; } = _ => false;

        /// <summary>Whether the announcement with this number throws.</summary>
        public Func<int, bool> FailAnnouncement { get; init; } = _ => false;

        /// <summary>What a failing call throws.</summary>
        public Func<Exception> Failure { get; init; } = () => new InvalidOperationException("the sink failed");

        /// <summary>Presents the sink was called with, whether they threw or not.</summary>
        public int Calls
        {
            get
            {
                lock (_lock)
                    return _calls;
            }
        }

        public int Announcements
        {
            get
            {
                lock (_lock)
                    return _announcements;
            }
        }

        /// <summary>What the failing calls threw, in order.</summary>
        public IReadOnlyList<Exception> Thrown
        {
            get
            {
                lock (_lock)
                    return [.. _thrown];
            }
        }

        public Task WaitForCallsAsync(int count) => WaitFor(taken: false, count);

        public Task WaitForTakenAsync(int count) => WaitFor(taken: true, count);

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            Exception? failure = null;
            List<TaskCompletionSource> ready;
            lock (_lock)
            {
                _calls++;
                if (FailPresent(_calls))
                {
                    failure = Failure();
                    _thrown.Add(failure);
                }
                else
                {
                    _taken++;
                }
                ready = Ready();
            }
            foreach (var signal in ready)
                signal.TrySetResult();
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
        {
            lock (_lock)
            {
                _announcements++;
                if (!FailAnnouncement(_announcements))
                    return ValueTask.CompletedTask;
                var failure = Failure();
                _thrown.Add(failure);
                return ValueTask.FromException(failure);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // Caller holds _lock.
        private List<TaskCompletionSource> Ready()
        {
            List<TaskCompletionSource> ready = [];
            _waiters.RemoveAll(w =>
            {
                if ((w.Taken ? _taken : _calls) < w.Count)
                    return false;
                ready.Add(w.Signal);
                return true;
            });
            return ready;
        }

        // The bound only turns a call that never comes into a failure rather than a hang.
        private Task WaitFor(bool taken, int count)
        {
            lock (_lock)
            {
                if ((taken ? _taken : _calls) >= count)
                    return Task.CompletedTask;
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((taken, count, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>Records what the pacer reports about its sink.</summary>
    private sealed class SinkFailureRecorder
    {
        private readonly object _lock = new();
        private readonly List<PlaybackError> _reports = [];
        private readonly List<(int Count, TaskCompletionSource<PlaybackError> Signal)> _waiters = [];
        private readonly TaskCompletionSource<Exception> _faulted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int Reports
        {
            get
            {
                lock (_lock)
                    return _reports.Count;
            }
        }

        /// <summary>Completes with the fault, when the pacer reports one.</summary>
        public Task<Exception> FaultReported => _faulted.Task;

        public Exception? Fault => _faulted.Task.IsCompleted ? _faulted.Task.Result : null;

        /// <summary>How many times the pacer reported its sink as faulted.</summary>
        public int Faults => Volatile.Read(ref _faults);

        private int _faults;

        public void OnFailed(PlaybackError error)
        {
            List<(PlaybackError, TaskCompletionSource<PlaybackError>)> ready = [];
            lock (_lock)
            {
                _reports.Add(error);
                _waiters.RemoveAll(w =>
                {
                    if (_reports.Count < w.Count)
                        return false;
                    ready.Add((_reports[w.Count - 1], w.Signal));
                    return true;
                });
            }
            foreach (var (report, signal) in ready)
                signal.TrySetResult(report);
        }

        public void OnFaulted(Exception fault)
        {
            Interlocked.Increment(ref _faults);
            _faulted.TrySetResult(fault);
        }

        /// <summary>Completes with the <paramref name="count"/>th report.</summary>
        public Task<PlaybackError> WaitForReportAsync(int count)
        {
            lock (_lock)
            {
                if (_reports.Count >= count)
                    return Task.FromResult(_reports[count - 1]);
                var signal = new TaskCompletionSource<PlaybackError>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _waiters.Add((count, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>Counts what is logged, by level.</summary>
    private sealed class LevelCountingLogger : ILogger
    {
        private readonly object _lock = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public int Count(LogLevel level, string? containing = null)
        {
            lock (_lock)
                return _entries.Count(e =>
                    e.Level == level
                    && (containing is null || e.Message.Contains(containing, StringComparison.Ordinal))
                );
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_lock)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
