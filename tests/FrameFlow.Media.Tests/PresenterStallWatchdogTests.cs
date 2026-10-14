// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins ADR-0086 decision 3 on <see cref="PresenterStallWatchdog"/>: its asynchronous dispose waits
/// for a check already running, so no check runs and no event is raised after it, and a second
/// dispose waits for the first.
/// </summary>
/// <remarks>
/// The watchdog's timer is the machine's, because what is under test is that the dispose waits for
/// that timer's callback, which a fake clock's timer does not model. No test waits for a duration.
/// The sampler the watchdog calls is the seam: it raises a signal as the first check enters it, and
/// holds that check until the test releases it, so the check is in flight, for certain, while the
/// dispose runs. The short interval only makes the first check come soon.
/// </remarks>
public sealed class PresenterStallWatchdogTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task DisposeAsync_WaitsForACheckInFlight()
    {
        using var sampler = new HeldSampler();
        var watchdog = sampler.Watch();
        await sampler.Entered.WaitAsync(Bound);

        var disposing = watchdog.DisposeAsync().AsTask();

        // The check is held in the sampler, so a dispose that waits for it cannot have ended.
        Assert.False(disposing.IsCompleted);

        sampler.Release();
        await disposing.WaitAsync(Bound);
        Assert.Equal(0, sampler.InFlight);
    }

    [Fact]
    public async Task ASecondDisposeAsync_WaitsForTheFirst()
    {
        using var sampler = new HeldSampler();
        var watchdog = sampler.Watch();
        await sampler.Entered.WaitAsync(Bound);

        var first = watchdog.DisposeAsync().AsTask();
        var second = watchdog.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);

        sampler.Release();
        await first.WaitAsync(Bound);
        await second.WaitAsync(Bound);
        Assert.Equal(0, sampler.InFlight);
    }

    [Fact]
    public async Task DisposeAsync_AfterDispose_WaitsForTheCheckDisposeLeftRunning()
    {
        using var sampler = new HeldSampler();
        var watchdog = sampler.Watch();
        await sampler.Entered.WaitAsync(Bound);

        watchdog.Dispose(); // returns with the check still held
        var disposing = watchdog.DisposeAsync().AsTask();

        Assert.False(disposing.IsCompleted);

        sampler.Release();
        await disposing.WaitAsync(Bound);
        Assert.Equal(0, sampler.InFlight);
    }

    /// <summary>
    /// The watchdog's sampler. Holds the first check until <see cref="Release"/>; a later one, which
    /// the timer may start beside it, returns at once.
    /// </summary>
    private sealed class HeldSampler : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PresenterStallWatchdog? _watchdog;
        private int _calls;
        private int _inFlight;

        /// <summary>Completes once the first check is in the sampler.</summary>
        public Task Entered => _entered.Task;

        /// <summary>How many checks are in the sampler now.</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        public PresenterStallWatchdog Watch() =>
            _watchdog = new PresenterStallWatchdog(Sample, NullLogger.Instance, interval: TimeSpan.FromMilliseconds(1));

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
