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
/// None of that bit, because every test here registers one wait at a time and advances once to
/// satisfy it. The next test would not have been so lucky, and this file's own remarks were
/// inviting it to copy the pattern.
/// </para>
/// </remarks>
public sealed class WallClockSourceManualTimeTests
{
    [Fact]
    public async Task WaitUntilAsync_CompletesWhenTheProviderReachesTheTarget()
    {
        var time = new FakeTimeProvider();
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
}
