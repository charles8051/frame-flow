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
/// </remarks>
public sealed class WallClockSourceManualTimeTests
{
    [Fact]
    public async Task WaitUntilAsync_CompletesWhenTheProviderReachesTheTarget()
    {
        var time = new ManualTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();

        var wait = clock.WaitUntilAsync(TimeSpan.FromSeconds(2)).AsTask();
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Latest_TracksTheProvider()
    {
        var time = new ManualTimeProvider();
        await using var clock = new WallClockSource(time);
        clock.Start();

        Assert.Equal(TimeSpan.Zero, clock.Latest);
        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), clock.Latest);
    }

    [Fact]
    public async Task PauseFreezesTheClock_AndResumeContinuesFromThere()
    {
        var time = new ManualTimeProvider();
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
        var time = new ManualTimeProvider();
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
        var time = new ManualTimeProvider();
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
        var time = new ManualTimeProvider();
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
    /// Minimal manual <see cref="TimeProvider"/>: time moves only on <see cref="Advance"/>,
    /// which fires every timer that has come due, synchronously on the calling thread.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
                return _ticks;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks);
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _ticks += by.Ticks;
                due = [.. _timers];
            }

            // Outside the gate: a callback may create or dispose timers.
            foreach (var t in due)
                t.FireIfDue(GetTimestamp());
        }

        internal void Remove(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        internal sealed class ManualTimer(
            ManualTimeProvider provider,
            TimerCallback callback,
            object? state
        ) : ITimer
        {
            private long _dueAt = long.MaxValue;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _dueAt =
                    dueTime == Timeout.InfiniteTimeSpan
                        ? long.MaxValue
                        : provider.GetTimestamp() + dueTime.Ticks;
                return true;
            }

            public void FireIfDue(long now)
            {
                if (now < _dueAt)
                    return;

                _dueAt =
                    _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero
                        ? long.MaxValue
                        : now + _period.Ticks;

                callback(state);
            }

            public void Dispose() => provider.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
