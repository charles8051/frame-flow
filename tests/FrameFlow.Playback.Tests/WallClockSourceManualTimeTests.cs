using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Drives <see cref="WallClockSource"/> from a manual <see cref="TimeProvider"/>, with no
/// sleeping and no wall clock involved.
/// </summary>
/// <remarks>
/// <para>
/// This is what the injected provider is for. Before it, the pacing loop slept on
/// <c>Task.Delay</c> while <c>Elapsed</c> came from a private <see cref="System.Diagnostics.Stopwatch"/>,
/// so the delay and the clock had different sources: a provider that advanced its own time
/// without advancing the wall would fire the timer, find <c>remaining</c> unchanged, and loop
/// forever. Both now read from the provider, which is what makes these tests possible at all.
/// </para>
/// <para>
/// Nothing here polls or sleeps. Each wait is registered before the advance that satisfies
/// it, and the advance fires the timer synchronously on the test thread.
/// </para>
/// <para>
/// What the advance does <i>not</i> run synchronously is the pacing loop's reaction. When its
/// sleep fires, <c>WaitUntilCoreAsync</c> resumes on a pool thread, re-reads the clock, and arms
/// the next slice. A test that advances again before that re-arm can land between the loop
/// reading the clock and arming the timer, and the timer is then armed against a clock that has
/// already moved past it — due at a time nobody advances to. That hung
/// <c>WaitUntilAsync_CompletesWhenTheProviderReachesTheTarget</c> on a CI runner. So a test that
/// advances more than once across one wait uses <see cref="ArmSignallingTimeProvider"/> and waits
/// for the loop to re-arm before the next advance (ADR-0072 rule 3, applied to a timer rather
/// than a queue).
/// </para>
/// <para>
/// The provider is <c>FakeTimeProvider</c> from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c>. It replaced a hand-rolled one that lived
/// in this file. The hand-rolled version was correct for the tests below and wrong in the ways
/// such a double is usually wrong. Its <c>Advance</c> snapshotted the timer list and fired in
/// registration order rather than due order, so two timers coming due in one advance ran
/// backwards if they were registered that way. Its <c>Change</c> only recorded a deadline, so
/// a timer armed with a due time of zero did not run until the next <c>Advance</c> rather than
/// immediately. And a timer created from inside a callback was not in that advance's snapshot,
/// so it could not fire in the same advance that armed it.
/// </para>
/// <para>
/// None of that bit, because every test here registers one wait at a time. The next test would
/// not have been so lucky, and this file's own remarks were inviting it to copy the pattern.
/// </para>
/// </remarks>
public sealed class WallClockSourceManualTimeTests
{
    [Fact]
    public async Task WaitUntilAsync_CompletesWhenTheProviderReachesTheTarget()
    {
        var time = new ArmSignallingTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();

        var wait = clock.WaitUntilAsync(TimeSpan.FromSeconds(2)).AsTask();
        Assert.False(wait.IsCompleted);

        // WaitUntilAsync armed its first slice before returning.
        var armed = time.TimersCreated;
        time.Advance(TimeSpan.FromSeconds(1));

        // The slice fired; wait for the loop to see a second still to go and arm the next one.
        // Advancing before this is the race described in the class remarks.
        await time.WaitForTimersCreatedAsync(armed + 1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Latest_TracksTheProvider()
    {
        var time = new FakeTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();

        Assert.Equal(TimeSpan.Zero, clock.Latest);
        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), clock.Latest);
    }

    [Fact]
    public async Task PauseFreezesTheClock_AndResumeContinuesFromThere()
    {
        var time = new FakeTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();

        time.Advance(TimeSpan.FromSeconds(1));
        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(5)); // passes, but the clock is stopped

        Assert.Equal(TimeSpan.FromSeconds(1), clock.Latest);
        Assert.False(clock.IsRunning);

        clock.Resume();
        time.Advance(TimeSpan.FromSeconds(2));

        // Resumes from where it stopped: the paused 5 s is not credited.
        Assert.Equal(TimeSpan.FromSeconds(3), clock.Latest);
        Assert.True(clock.IsRunning);
    }

    [Fact]
    public async Task Seek_ReseatsTheOriginAndKeepsRunning()
    {
        var time = new FakeTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();
        time.Advance(TimeSpan.FromSeconds(1));

        clock.Seek(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Latest);
        Assert.True(clock.IsRunning);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(32), clock.Latest);
    }

    [Fact]
    public async Task SeekWhilePaused_StaysPaused()
    {
        var time = new FakeTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();
        clock.Pause();

        clock.Seek(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(10), clock.Latest);
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public async Task AWaitBehindASeek_ResolvesOnTheNextSlice()
    {
        var time = new FakeTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();

        var wait = clock.WaitUntilAsync(TimeSpan.FromSeconds(5)).AsTask();
        Assert.False(wait.IsCompleted);

        // Jumping past the target must release the waiter, not strand it.
        clock.Seek(TimeSpan.FromSeconds(9));
        time.Advance(TimeSpan.FromMilliseconds(50));

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that reports each timer it creates, so a test can wait
    /// for code under test to arm its next timer instead of guessing when it has.
    /// </summary>
    /// <remarks>
    /// Signals after the base provider has registered the timer, so an advance made once the
    /// wait completes is guaranteed to see it. Everything about time itself is still
    /// <see cref="FakeTimeProvider"/>'s; this only makes arming observable.
    /// </remarks>
    private sealed class ArmSignallingTimeProvider : FakeTimeProvider
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _created;

        public int TimersCreated
        {
            get
            {
                lock (_gate)
                    return _created;
            }
        }

        public Task WaitForTimersCreatedAsync(int count)
        {
            lock (_gate)
            {
                if (_created >= count)
                    return Task.CompletedTask;
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, signal));
                return signal.Task;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            List<TaskCompletionSource>? ready = null;
            lock (_gate)
            {
                _created++;
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_created >= _waiters[i].Count)
                    {
                        (ready ??= []).Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }
            if (ready is not null)
                foreach (var signal in ready)
                    signal.TrySetResult();
            return timer;
        }
    }
}
