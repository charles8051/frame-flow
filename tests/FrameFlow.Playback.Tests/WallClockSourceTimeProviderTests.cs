using System.Diagnostics;
using FrameFlow.Media;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Pins that <see cref="WallClockSource"/> paces through its injected
/// <see cref="TimeProvider"/> rather than the ambient one.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists because <see cref="TimeProvider.System"/> routes to the platform timer
/// queue, which on Windows is quantized to the ~15.625 ms system tick. A 60 fps frame period
/// is 16.67 ms — just over one quantum — so a sleep for it usually costs two. Measured on
/// this machine, one frame period through <c>TimeProvider.System</c> takes 29.5 ms against
/// 16.3 ms through a high-resolution waitable timer: ~34 fps versus ~61 fps, decided entirely
/// by which provider supplies the sleep.
/// </para>
/// <para>
/// These tests do not assert timing, which would be flaky. They assert the wiring: the
/// injected provider is what gets asked for the delay, so substituting one is sufficient to
/// change the pacing behaviour — and the default constructor selects the high-resolution
/// provider, which is the choice that decides the frame rate.
/// </para>
/// <para>
/// Whether the selected provider is actually fast is a different question, and it belongs to
/// the provider rather than to this type.
/// <c>HighResolutionTimeProviderTests.AFramePeriodCostsAFramePeriod</c> is where it is asked.
/// </para>
/// </remarks>
public sealed class WallClockSourceTimeProviderTests
{
    [Fact]
    public async Task WaitUntilAsync_AsksTheInjectedProviderForTheDelay()
    {
        var provider = new RecordingTimeProvider();
        await using var clock = new WallClockSource(provider);
        clock.Start();

        // Far enough out that the fast path cannot satisfy it synchronously.
        var wait = clock.WaitUntilAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(await provider.TimerRequested.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        clock.Seek(TimeSpan.FromSeconds(31)); // make the target due so the loop exits
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitUntilAsync_DoesNotTouchTheProviderWhenTheTargetIsAlreadyDue()
    {
        var provider = new RecordingTimeProvider();
        await using var clock = new WallClockSource(provider);
        clock.Start();
        clock.Seek(TimeSpan.FromSeconds(10));

        // The synchronous fast path is the per-frame hot path; it must stay allocation-free
        // and must not reach for a timer.
        await clock.WaitUntilAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(provider.TimerRequested.Task.IsCompleted);
    }

    [Fact]
    public async Task DefaultConstruction_SelectsTheHighResolutionProvider()
    {
        // The parameterless constructor is what every existing caller uses, including
        // SubstrateSession, so it is the one that decides whether the fix reaches playback.
        await using var clock = new WallClockSource();

        // Assert the selection, not its consequence. This used to time fifteen real 16.67 ms
        // waits and assert the median came in under 25 ms, which is the same property
        // measured through a shared CI runner's scheduler: #148 is that test failing on
        // windows-latest with min 16.67 (provider wired correctly) and a median of 32.32
        // because more than half the waits were descheduled. The choice the constructor makes
        // is the thing worth pinning, it is the same choice on every platform, and it is
        // observable without a clock.
        Assert.Same(HighResolutionTimeProvider.Preferred, clock.Provider);
    }

    [Fact]
    public async Task ExplicitProvider_OptsOutOfTheDefault()
    {
        // The other half of the contract: passing a provider has to win over the default,
        // which is what lets a caller opt back in to TimeProvider.System and what lets every
        // test in this assembly substitute a fake.
        await using var clock = new WallClockSource(TimeProvider.System);

        Assert.Same(TimeProvider.System, clock.Provider);
    }

    [Fact]
    public async Task WaitUntilAsync_SleepsTheRemainingTime_NotTheCap()
    {
        // Selecting the fast provider is not sufficient on its own: the loop also has to ask
        // it for the right interval. Slicing every wait at MaxSleep instead of the remaining
        // sub-frame time would leave both selection assertions passing and put a 50 ms floor
        // under every frame, which is the same ~20 fps symptom by a different route.
        //
        // Frozen provider, so remaining is exactly the target and nothing here reads a wall
        // clock. The timer never fires; what is asserted is the interval that was asked for.
        var provider = new FrozenRecordingTimeProvider();
        await using var clock = new WallClockSource(provider);
        clock.Start();

        using var cts = new CancellationTokenSource();
        var wait = clock.WaitUntilAsync(TimeSpan.FromMilliseconds(30), cts.Token);

        Assert.Equal(TimeSpan.FromMilliseconds(30), await provider.FirstDueTime.Task);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public async Task WaitUntilAsync_CapsALongSleepAtTheSliceLimit()
    {
        // The other side of the same arithmetic. A far-future target must not be slept in one
        // go — the cap is what lets a paused or seeked clock be re-read promptly (ADR-0057).
        var provider = new FrozenRecordingTimeProvider();
        await using var clock = new WallClockSource(provider);
        clock.Start();

        using var cts = new CancellationTokenSource();
        var wait = clock.WaitUntilAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.Equal(TimeSpan.FromMilliseconds(50), await provider.FirstDueTime.Task);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock never moves and whose timers never fire,
    /// recording the interval the first one was asked for.
    /// </summary>
    /// <remarks>
    /// Frozen rather than advancing, so <c>remaining</c> inside the pacing loop is exactly the
    /// target and the assertion is on an exact value. It never fires a callback, so none of
    /// the ordering hazards that make a hand-rolled advancing provider a bad idea apply here —
    /// this records a request, it does not simulate time.
    /// </remarks>
    private sealed class FrozenRecordingTimeProvider : TimeProvider
    {
        public TaskCompletionSource<TimeSpan> FirstDueTime { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp() => 0;

        public override long TimestampFrequency => Stopwatch.Frequency;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            FirstDueTime.TrySetResult(dueTime);
            return new NeverFiringTimer();
        }

        private sealed class NeverFiringTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Delegates to the system provider but records that a timer was asked for.</summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        public TaskCompletionSource<bool> TimerRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            TimerRequested.TrySetResult(true);
            return System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
