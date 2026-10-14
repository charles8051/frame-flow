using FrameFlow.Playback.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// A sink that throws costs the frame it was given, and the pacer presents the next frame; at most
/// one failure in each interval is reported; one that keeps throwing, counted across the pacers
/// over the sink, is reported as faulted, and delivery stops; and so does anything else that
/// would end the delivery loop (#703). Each test waits on what the pacer raises, what the sink is
/// called with, or the pacer's park, and stops the pacer before asserting what did not happen:
/// <see cref="ClockSelectVideoSink.DisposeAsync"/> awaits the delivery loop, so everything the
/// loop was going to do has happened by then.
/// </summary>
/// <remarks>
/// The report interval runs on a <see cref="FakeTimeProvider"/> nobody advances unless the test
/// says so, so every failure in a test falls inside the first one's interval.
/// </remarks>
public sealed partial class ClockSelectVideoSinkTests
{
    private const int MaxSinkFailures = SinkFailureRun.MaxConsecutiveFailures;

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
    public async Task ASinkThatThrowsOnEveryCall_IsReportedAsFaulted_AfterTheLimit_AndDeliveryStopsThere()
    {
        // The call that escalates is held inside the sink while the next frame waits in the ring,
        // so a pacer that went on delivering would call the sink again before it next parked.
        var sink = new ScriptedSink { FailPresent = _ => true, HoldPresent = MaxSinkFailures + 1 };
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
        await sink.Held;
        var waiting = new CountedFrame(TimeSpan.Zero);
        await pacer.PresentAsync(waiting, default);
        // Inside the sink's call, so not parked: the next park comes after that call returns.
        long parked = pacer.ParkGeneration;

        sink.ReleaseHeld();
        var fault = await failures.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));
        await pacer.WaitForParkAfterAsync(parked);

        Assert.Equal(MaxSinkFailures + 1, sink.Calls);
        await pacer.DisposeAsync();
        Assert.Equal(MaxSinkFailures + 1, sink.Calls);
        Assert.Equal(1, failures.Reports);
        Assert.Equal(1, failures.Faults);
        var wrapped = Assert.IsType<InvalidOperationException>(fault);
        Assert.Same(sink.Thrown[^1], wrapped.InnerException);
        Assert.Contains(nameof(ScriptedSink), wrapped.Message, StringComparison.Ordinal);
        Assert.All(frames, f => Assert.Equal(0, f.References));
        // Left in the ring when delivery stopped, and released by the teardown.
        Assert.Equal(0, waiting.References);
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
        Assert.Equal(1, failures.Reports);
    }

    [Fact]
    public async Task ASinkThatKeepsFailing_IsReportedOncePerInterval_CountingTheFailuresBetween()
    {
        // Every other frame fails, so the sink never escalates: each failure reported would be an
        // error for half its frames.
        var time = new FakeTimeProvider();
        var sink = new ScriptedSink { FailPresent = call => call % 2 == 1 };
        var failures = new SinkFailureRecorder();
        var pacer = NewPacer(sink, failures, time: time);

        for (int call = 1; call <= 6; call++)
        {
            await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
            await sink.WaitForCallsAsync(call);
        }
        time.Advance(SinkFailureRun.ReportInterval);
        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        var second = await failures.WaitForReportAsync(2);
        await pacer.DisposeAsync();

        Assert.Equal(2, failures.Reports);
        Assert.Same(sink.Thrown[^1], second.Inner);
        Assert.Contains("and on 2 more since the last report", second.Message, StringComparison.Ordinal);
        Assert.Null(failures.Fault);
    }

    [Fact]
    public async Task TheRunOfFailures_GoesOnFromOnePacerToTheNextOverTheSameSink()
    {
        // A player gives each item a pacer of its own, over one sink. An item shorter than the
        // limit still counts toward it, and once the sink has faulted, the next item faults at its
        // first failure, until the sink takes a frame.
        const int first = 5;
        var sink = new ScriptedSink { FailPresent = call => call != MaxSinkFailures + 3 };
        var run = new VideoSinkFailures();
        var time = new FakeTimeProvider();

        var a = new SinkFailureRecorder();
        await using (var pacer = NewPacer(sink, a, time: time, sinkFailures: run))
            await PresentEachAsync(pacer, sink, from: 1, to: first);

        var b = new SinkFailureRecorder();
        await using (var pacer = NewPacer(sink, b, time: time, sinkFailures: run))
        {
            await PresentEachAsync(pacer, sink, from: first + 1, to: MaxSinkFailures + 1);
            await b.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var c = new SinkFailureRecorder();
        await using (var pacer = NewPacer(sink, c, time: time, sinkFailures: run))
        {
            await PresentEachAsync(pacer, sink, from: MaxSinkFailures + 2, to: MaxSinkFailures + 2);
            await c.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var d = new SinkFailureRecorder();
        await using (var pacer = NewPacer(sink, d, time: time, sinkFailures: run))
            await PresentEachAsync(pacer, sink, from: MaxSinkFailures + 3, to: MaxSinkFailures + 4);

        Assert.Null(a.Fault);
        Assert.Equal(1, a.Reports);
        // The sink's report interval runs across the pacers too.
        Assert.Equal(0, b.Reports);
        Assert.Equal(0, d.Reports);
        Assert.Null(d.Fault);
        Assert.Equal(1, sink.Taken);
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
    public async Task AClockThatThrows_IsReportedAsAFault_AndTheRingIsReleasedAtTeardown()
    {
        // Not a call to the sink: the loop reads the clock to choose the frame due. It used to end
        // there with nothing said, and the picture stopped.
        var clockFailure = new InvalidOperationException("the clock failed");
        var clock = new FakeClock { OnLatestRead = () => throw clockFailure };
        var sink = new ScriptedSink();
        var failures = new SinkFailureRecorder();
        var pacer = NewPacer(sink, failures, clock: clock);
        var due = new CountedFrame(TimeSpan.Zero);

        await pacer.PresentAsync(due, default);
        var fault = await failures.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));
        var waiting = new CountedFrame(TimeSpan.Zero);
        await pacer.PresentAsync(waiting, default);
        await pacer.DisposeAsync();

        Assert.Same(clockFailure, fault);
        Assert.Equal(1, failures.Faults);
        Assert.Equal(0, failures.Reports);
        Assert.Equal(0, sink.Calls);
        Assert.Equal(0, due.References);
        Assert.Equal(0, waiting.References);
    }

    [Fact]
    public async Task AfterTheLastFrameFails_TheEndOfContentHoldStillEndsTheRun()
    {
        var clock = new FakeClock();
        var sink = new ScriptedSink { FailPresent = call => call == 2 };
        var failures = new SinkFailureRecorder();
        await using var pacer = NewPacer(sink, failures, clock: clock);

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
            onFaulted: failures.OnFaulted
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
            onFaulted: failures.OnFaulted
        );

        await PresentEachAsync(pacer, sink, from: 1, to: MaxSinkFailures + 1);
        await failures.FaultReported.WaitAsync(TimeSpan.FromSeconds(30));
        await pacer.DisposeAsync();

        Assert.Equal(2, logger.Count(LogLevel.Warning));
        Assert.Equal(MaxSinkFailures - 1, logger.Count(LogLevel.Debug, "threw"));
    }

    [Fact]
    public async Task AFaultCallbackThatThrows_IsLoggedAsAnError_NotAsACallbackDeliveryCarriedOnFrom()
    {
        // The session's callback only posts the fault and does not throw. One that did would leave
        // the fault unreported and delivery stopped, which is an error, not a warning that the
        // loop carried on.
        var sink = new ScriptedSink { FailPresent = _ => true };
        var logger = new LevelCountingLogger();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pacer = new ClockSelectVideoSink(
            sink,
            new FakeClock(),
            logger,
            capacity: 4,
            timeProvider: new FakeTimeProvider(),
            onFaulted: _ =>
            {
                called.TrySetResult();
                throw new InvalidOperationException("the fault callback failed");
            }
        );

        await PresentEachAsync(pacer, sink, from: 1, to: MaxSinkFailures + 1);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await pacer.DisposeAsync();

        Assert.Equal(1, logger.Count(LogLevel.Error, "fault callback threw"));
        Assert.Equal(0, logger.Count(LogLevel.Warning, "callback threw; delivery continues"));
    }

    private static ClockSelectVideoSink NewPacer(
        IVideoSink sink,
        SinkFailureRecorder failures,
        VideoFormatAnnouncer? announcer = null,
        FakeClock? clock = null,
        TimeProvider? time = null,
        VideoSinkFailures? sinkFailures = null
    ) =>
        new(
            sink,
            clock ?? new FakeClock(),
            capacity: 4,
            timeProvider: time ?? new FakeTimeProvider(),
            formatAnnouncer: announcer,
            sinkFailures: sinkFailures,
            onSinkFailed: failures.OnFailed,
            onFaulted: failures.OnFaulted
        );

    /// <summary>
    /// Presents one frame for each of the sink's calls <paramref name="from"/> to
    /// <paramref name="to"/>, waiting for each call before the next frame, and returns once the
    /// pacer has parked after the last one, so it has dealt with that call's outcome.
    /// </summary>
    /// <remarks>
    /// The park is what makes a dispose straight after safe: a failure the loop has not yet
    /// counted when the pacer stops is a failure on the way out, and is not counted at all.
    /// </remarks>
    private static async Task PresentEachAsync(ClockSelectVideoSink pacer, ScriptedSink sink, int from, int to)
    {
        for (int call = from; call <= to; call++)
        {
            long parked = pacer.ParkGeneration;
            await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
            await sink.WaitForCallsAsync(call);
            if (call == to)
                await pacer.WaitForParkAfterAsync(parked).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// A sink whose calls fail as the test says, counted from 1. It owns every frame it is given,
    /// so it releases each one whether the call fails or not.
    /// </summary>
    private sealed class ScriptedSink : IVideoSink
    {
        private readonly object _lock = new();
        private readonly List<Exception> _thrown = [];
        private readonly List<(bool Taken, int Count, TaskCompletionSource Signal)> _waiters = [];
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _taken;
        private int _announcements;

        /// <summary>Whether the present with this number throws.</summary>
        public Func<int, bool> FailPresent { get; init; } = _ => false;

        /// <summary>Whether the announcement with this number throws.</summary>
        public Func<int, bool> FailAnnouncement { get; init; } = _ => false;

        /// <summary>What a failing call throws.</summary>
        public Func<Exception> Failure { get; init; } = () => new InvalidOperationException("the sink failed");

        /// <summary>
        /// The present, by number, that does not return until <see cref="ReleaseHeld"/>, or 0 for
        /// none.
        /// </summary>
        public int HoldPresent { get; init; }

        /// <summary>Completes once the held present has been called.</summary>
        public Task Held => _held.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void ReleaseHeld() => _release.TrySetResult();

        /// <summary>Presents the sink was called with, whether they threw or not.</summary>
        public int Calls
        {
            get
            {
                lock (_lock)
                    return _calls;
            }
        }

        /// <summary>Presents the sink took without throwing.</summary>
        public int Taken
        {
            get
            {
                lock (_lock)
                    return _taken;
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
            bool hold;
            List<TaskCompletionSource> ready;
            lock (_lock)
            {
                _calls++;
                hold = _calls == HoldPresent;
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

            if (hold)
            {
                _held.TrySetResult();
                return new ValueTask(ReturnOnReleaseAsync(failure));
            }
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

        private async Task ReturnOnReleaseAsync(Exception? failure)
        {
            await _release.Task.ConfigureAwait(false);
            if (failure is not null)
                throw failure;
        }

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

    /// <summary>Records what the pacer reports about its sink, and its fault.</summary>
    private sealed class SinkFailureRecorder
    {
        private readonly object _lock = new();
        private readonly List<PlaybackError> _reports = [];
        private readonly List<(int Count, TaskCompletionSource<PlaybackError> Signal)> _waiters = [];
        private readonly TaskCompletionSource<Exception> _faulted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _faults;

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

        /// <summary>How many times the pacer reported a fault.</summary>
        public int Faults => Volatile.Read(ref _faults);

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
