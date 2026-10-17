// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins ADR-0086 decision 3 on <see cref="PresenterStallWatchdog"/>: its asynchronous dispose waits
/// for a check already running, so no check runs and no event is raised after it; a second dispose
/// waits for the first; and a stop held past the warning interval names, once, what holds it.
/// </summary>
/// <remarks>
/// No test waits for a duration. The watchdog samples on a <see cref="FakeTimeProvider"/>'s timer,
/// so a check runs only inside <see cref="FakeTimeProvider.Advance"/>, on the thread that advances
/// it (#765). The sampler, or a handler, is the seam: it raises a signal as the check enters it and
/// holds that check until the test releases it, so the check is in flight, for certain, while the
/// dispose runs. A held check holds the advance too, so the test advances on a thread of its own.
/// The warning's interval runs on a second fake clock, so advancing one never runs the other's timer.
/// </remarks>
public sealed class PresenterStallWatchdogTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task DisposeAsync_WaitsForACheckInFlight()
    {
        using var sampler = new HeldSampler();
        var watchdog = sampler.Watch();
        var ticking = OnItsOwnThread(() => sampler.Clock.Advance(Interval));
        await sampler.Entered.WaitAsync(Bound);

        var disposing = watchdog.DisposeAsync().AsTask();

        // The check is held in the sampler, so a dispose that waits for it cannot have ended.
        Assert.False(disposing.IsCompleted);

        sampler.Release();
        await disposing.WaitAsync(Bound);
        await ticking.WaitAsync(Bound);
        Assert.Equal(0, sampler.InFlight);

        // Stopped, the watchdog samples no more.
        sampler.Clock.Advance(Interval);
        Assert.Equal(1, sampler.Calls);
    }

    [Fact]
    public async Task ASecondDisposeAsync_WaitsForTheFirst()
    {
        using var sampler = new HeldSampler();
        var watchdog = sampler.Watch();
        var ticking = OnItsOwnThread(() => sampler.Clock.Advance(Interval));
        await sampler.Entered.WaitAsync(Bound);

        var first = watchdog.DisposeAsync().AsTask();
        var second = watchdog.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);

        sampler.Release();
        await first.WaitAsync(Bound);
        await second.WaitAsync(Bound);
        await ticking.WaitAsync(Bound);
        Assert.Equal(0, sampler.InFlight);
    }

    [Fact]
    public async Task DisposeAsync_AfterDispose_WaitsForTheCheckDisposeLeftRunning()
    {
        using var sampler = new HeldSampler();
        var watchdog = sampler.Watch();
        var ticking = OnItsOwnThread(() => sampler.Clock.Advance(Interval));
        await sampler.Entered.WaitAsync(Bound);

        watchdog.Dispose(); // returns with the check still held
        var disposing = watchdog.DisposeAsync().AsTask();

        Assert.False(disposing.IsCompleted);

        sampler.Release();
        await disposing.WaitAsync(Bound);
        await ticking.WaitAsync(Bound);
        Assert.Equal(0, sampler.InFlight);
    }

    [Fact]
    public async Task AStopHeldByAStalledHandler_NamesTheHandlerOnce()
    {
        var ticks = new FakeTimeProvider();
        var warnings = new FakeTimeProvider();
        var log = new WarningLog();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long samples = 0;

        // Each sample is ten seconds on, with one more frame accepted and none enqueued: the third
        // is an accepted frame unpresented past the timeout, a stall by the present-loop rule.
        var watchdog = new PresenterStallWatchdog(
            () =>
            {
                var n = Interlocked.Increment(ref samples);
                return new PresenterSample(n * Stopwatch.Frequency * 10, 1, 1, n, 0);
            },
            log,
            ticks,
            interval: Interval
        )
        {
            WarningTime = warnings,
        };
        void HoldTheStall(PresenterStallInfo _)
        {
            entered.TrySetResult();

            // The bound only turns a release that never comes into a failure.
            released.Task.Wait(Bound);
        }
        watchdog.Stalled += HoldTheStall;

        try
        {
            // Three checks: the third raises the stall, and its handler holds the advance.
            var ticking = OnItsOwnThread(() => ticks.Advance(Interval * 3));
            await entered.Task.WaitAsync(Bound);

            var disposing = watchdog.DisposeAsync().AsTask();
            warnings.Advance(SlowWorkWarning.DefaultInterval);

            Assert.Single(
                log.Warnings,
                m => m.Contains("Stalled handler", StringComparison.Ordinal)
                    && m.Contains(nameof(HoldTheStall), StringComparison.Ordinal));
            Assert.False(disposing.IsCompleted);

            released.TrySetResult();
            await disposing.WaitAsync(Bound);
            await ticking.WaitAsync(Bound);
            warnings.Advance(SlowWorkWarning.DefaultInterval);
            Assert.Single(log.Warnings);
            Assert.Equal(3, Interlocked.Read(ref samples));
        }
        finally
        {
            released.TrySetResult();
            watchdog.Dispose();
        }
    }

    // Runs work on a thread of its own, not the pool's, so a check it holds holds no pool thread.
    private static Task OnItsOwnThread(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Keeps the messages logged at Warning.</summary>
    private sealed class WarningLog : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                    return [.. _warnings];
            }
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
            if (logLevel != LogLevel.Warning)
                return;
            lock (_warnings)
                _warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// The watchdog's sampler, on the fake clock <see cref="Clock"/>. Holds the first check until
    /// <see cref="Release"/>; a later one returns at once.
    /// </summary>
    private sealed class HeldSampler : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PresenterStallWatchdog? _watchdog;
        private int _calls;
        private int _inFlight;

        /// <summary>Runs a check each time it is advanced past an interval.</summary>
        public FakeTimeProvider Clock { get; } = new();

        /// <summary>Completes once the first check is in the sampler.</summary>
        public Task Entered => _entered.Task;

        /// <summary>How many checks are in the sampler now.</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>How many checks have entered the sampler.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public PresenterStallWatchdog Watch() =>
            _watchdog = new PresenterStallWatchdog(Sample, NullLogger.Instance, Clock, interval: Interval);

        public void Release() => _released.TrySetResult();

        // Releases a check still held and stops the timer, so a failing test leaves nothing running.
        public void Dispose()
        {
            Release();
            _watchdog?.Dispose();
        }

        private PresenterSample Sample()
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    _entered.TrySetResult();

                    // The bound only turns a release that never comes into a failure.
                    _released.Task.Wait(Bound);
                }
                return default;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
