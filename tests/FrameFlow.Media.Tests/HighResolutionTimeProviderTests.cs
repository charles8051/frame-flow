using System.Diagnostics;
using FrameFlow.Media;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins <see cref="HighResolutionTimeProvider"/>, the library-level answer to Windows timer
/// quantization (issues #128 and #152).
/// </summary>
/// <remarks>
/// <para>
/// The property that matters is the timing one, and it is the only one that cannot be checked
/// by wiring alone: a delay must cost roughly what it asked for rather than being rounded up
/// to the ~15.625 ms system tick. Everything else here exists because this type owns an OS
/// handle per timer on a path that creates one per frame, so its teardown has to be right.
/// </para>
/// <para>
/// The timing assertions are one-sided. They bound the high-resolution provider and never
/// assert that <see cref="TimeProvider.System"/> is slow, because a host that has called
/// <c>timeBeginPeriod(1)</c> makes it fast too — that is the whole point of the issue — and a
/// test that fails when the machine is configured well would be worse than no test.
/// </para>
/// </remarks>
public sealed class HighResolutionTimeProviderTests
{
    /// <summary>A 60 fps frame period: the interval the defect is about.</summary>
    private static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(16.67);

    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public void PreferredIsUsableEverywhere()
    {
        // Callers are meant to use this without a platform check.
        Assert.NotNull(HighResolutionTimeProvider.Preferred);

        if (!OnWindows)
            Assert.Same(TimeProvider.System, HighResolutionTimeProvider.Preferred);
    }

    [Fact]
    public void SupportTracksThePlatform()
    {
        // The flag is a Windows facility, so nothing else can report support.
        if (!OnWindows)
            Assert.False(HighResolutionTimeProvider.IsSupported);
    }

    [Fact]
    public void ReadingTheClockIsUnchanged()
    {
        // Only timers are substituted. If the timestamp source moved as well, WallClockSource
        // would be measuring elapsed time on one clock and sleeping on another.
        var provider = HighResolutionTimeProvider.Preferred;

        Assert.Equal(Stopwatch.Frequency, provider.TimestampFrequency);

        var before = provider.GetTimestamp();
        Thread.SpinWait(10_000);
        Assert.True(provider.GetTimestamp() >= before, "the timestamp must be monotonic");
    }

    [RequiresHighResolutionTimer]
    public async Task AFramePeriodCostsAFramePeriod()
    {
        var provider = HighResolutionTimeProvider.Preferred;

        // Warm the pool and the wait thread so the first sample is not the one that decides it.
        await Task.Delay(FramePeriod, provider, CancellationToken.None);

        var samples = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await Task.Delay(FramePeriod, provider, CancellationToken.None);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];

        // Two system ticks is 31.25 ms, which is what an unquantized-looking request actually
        // costs through the platform timer queue and what caps playback at ~34 fps. One tick
        // is 15.625 ms. Asserting under 25 ms puts the result on the correct side of that
        // boundary with room for a busy machine — a real regression here doubles the number,
        // it does not nudge it.
        Assert.True(
            median < 25.0,
            $"a {FramePeriod.TotalMilliseconds:F2} ms delay took {median:F2} ms at the median "
                + $"(min {samples[0]:F2}, max {samples[^1]:F2}), which is the quantized cost "
                + "this provider exists to avoid"
        );
    }

    [Fact]
    public async Task AOneShotTimerFiresOnceWithItsState()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var marker = new object();
        object? received = null;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        using var timer = provider.CreateTimer(
            state =>
            {
                received = state;
                Interlocked.Increment(ref count);
                fired.TrySetResult();
            },
            marker,
            TimeSpan.FromMilliseconds(10),
            Timeout.InfiniteTimeSpan
        );

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(marker, received);

        // A one-shot must stay one-shot: an auto-reset timer that got re-armed would show up
        // here as a second callback.
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task APeriodicTimerKeepsFiring()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var fires = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timer = provider.CreateTimer(
            _ =>
            {
                if (Interlocked.Increment(ref fires) >= 3)
                    enough.TrySetResult();
            },
            null,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5)
        );

        await enough.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref fires) >= 3);
    }

    [Fact]
    public async Task ChangeReschedulesAPendingTimer()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timer = provider.CreateTimer(
            _ => fired.TrySetResult(),
            null,
            TimeSpan.FromMinutes(10),
            Timeout.InfiniteTimeSpan
        );

        Assert.True(timer.Change(TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan));
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ChangingToInfiniteDisarms()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var fires = 0;

        using var timer = provider.CreateTimer(
            _ => Interlocked.Increment(ref fires),
            null,
            TimeSpan.FromMilliseconds(50),
            Timeout.InfiniteTimeSpan
        );

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));

        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref fires));
    }

    [Fact]
    public async Task DisposeStopsTheCallback()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var fires = 0;

        var timer = provider.CreateTimer(
            _ => Interlocked.Increment(ref fires),
            null,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20)
        );
        timer.Dispose();

        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref fires));

        // Idempotent: Task.Delay disposes on both completion and cancellation.
        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task DisposeAsyncCompletesAndIsIdempotent()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var timer = provider.CreateTimer(
            _ => { },
            null,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20)
        );

        await timer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await timer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [RequiresHighResolutionTimer]
    public async Task TimersDoNotLeakTheirHandles()
    {
        // The pacing loops build one of these per frame, so a handle held until finalization
        // would be a leak at 60/second rather than a tidiness complaint. DisposeAsync is used
        // deliberately: it completes once the wait registration has given its reference back,
        // which is the moment the handle can actually close.
        var provider = HighResolutionTimeProvider.Preferred;
        using var self = Process.GetCurrentProcess();

        const int rounds = 300;
        for (int i = 0; i < 20; i++)
            await CreateAndDisposeAsync(provider);

        self.Refresh();
        var before = self.HandleCount;

        for (int i = 0; i < rounds; i++)
            await CreateAndDisposeAsync(provider);

        self.Refresh();
        var growth = self.HandleCount - before;

        // Generous: the process opens handles for its own reasons while a test runs. A leak
        // would be ~300 here, not tens.
        Assert.True(
            growth < 100,
            $"{rounds} created-and-disposed timers grew the process handle count by {growth}"
        );

        static async Task CreateAndDisposeAsync(TimeProvider provider)
        {
            var timer = provider.CreateTimer(
                _ => { },
                null,
                TimeSpan.FromMinutes(10),
                Timeout.InfiniteTimeSpan
            );
            await timer.DisposeAsync();
        }
    }

    [RequiresHighResolutionTimer]
    public async Task DisposingWhileTimersAreFiringIsSafe()
    {
        // Disposal racing an in-flight fire, across both dispose paths, with fuses short
        // enough that teardown lands in the middle of firing.
        //
        // What this does NOT do is distinguish the two teardown orderings. Teardown
        // unregisters the thread-pool wait before closing the handle it waits on; putting the
        // close back first — the order this file shipped with — leaves this test green,
        // because registering the wait takes a reference on the SafeWaitHandle and the OS
        // handle therefore stays open until the unregister returns it. Verified by trying it.
        // The ordering is what it is so correctness does not rest on that reference, not
        // because the other order was observed to fail.
        var provider = HighResolutionTimeProvider.Preferred;
        var afterDrain = 0;
        var faults = 0;

        var workers = Enumerable
            .Range(0, 8)
            .Select(w =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < 250; i++)
                    {
                        // Only the DisposeAsync half watches for a late callback, because it
                        // is the only half entitled to. DisposeAsync completes once every
                        // callback has returned, so a fire after it is unambiguously a defect.
                        // Sync Dispose promises no such thing — Unregister(null) leaves a
                        // running callback running — so a callback that passed OnSignalled's
                        // check before teardown can observe the flag afterwards. Asserting
                        // over both halves would fail intermittently against a correct
                        // implementation. The sync half still runs; it just contributes the
                        // concurrent dispose-under-fire traffic and the fault count.
                        var drained = false;
                        var watched = (w + i) % 2 != 0;
                        var timer = provider.CreateTimer(
                            _ =>
                            {
                                if (watched && Volatile.Read(ref drained))
                                    Interlocked.Increment(ref afterDrain);
                            },
                            null,
                            TimeSpan.FromMilliseconds(i % 3),
                            TimeSpan.FromMilliseconds(1)
                        );

                        try
                        {
                            if (watched)
                            {
                                await timer.DisposeAsync();
                                Volatile.Write(ref drained, true);
                            }
                            else
                            {
                                timer.Dispose();
                            }
                        }
                        catch
                        {
                            Interlocked.Increment(ref faults);
                        }
                    }
                })
            )
            .ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, Volatile.Read(ref faults));
        Assert.Equal(0, Volatile.Read(ref afterDrain));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(4294967295)]
    public void AnOutOfRangeIntervalIsRejected(double milliseconds)
    {
        var provider = HighResolutionTimeProvider.Preferred;
        var bad = TimeSpan.FromMilliseconds(milliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => provider.CreateTimer(_ => { }, null, bad, Timeout.InfiniteTimeSpan)
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => provider.CreateTimer(_ => { }, null, TimeSpan.Zero, bad)
        );
    }

    [Fact]
    public void ANullCallbackIsRejected()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        Assert.Throws<ArgumentNullException>(
            () => provider.CreateTimer(null!, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
        );
    }

    [Fact]
    public async Task ADueTimeOfZeroFiresImmediately()
    {
        // The kernel treats a due time of zero as "already past". Worth pinning because the
        // timer is armed after its wait is registered specifically so this fire is not lost.
        var provider = HighResolutionTimeProvider.Preferred;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timer = provider.CreateTimer(
            _ => fired.TrySetResult(),
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan
        );

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ADelayThroughTheProviderIsCancellable()
    {
        var provider = HighResolutionTimeProvider.Preferred;
        using var cts = new CancellationTokenSource();

        var delay = Task.Delay(TimeSpan.FromMinutes(10), provider, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => delay.WaitAsync(TimeSpan.FromSeconds(5))
        );
    }
}

/// <summary>
/// Skips unless the platform actually has high-resolution waitable timers, so the two tests
/// that assert on <see cref="HighResolutionTimeProvider"/>'s own behaviour do not silently
/// re-assert <see cref="TimeProvider.System"/>'s instead. Matches the
/// <c>RequiresAudioDeviceFact</c> pattern in FrameFlow.Audio.Tests.
/// </summary>
internal sealed class RequiresHighResolutionTimerAttribute : FactAttribute
{
    public RequiresHighResolutionTimerAttribute()
    {
        if (!HighResolutionTimeProvider.IsSupported)
            Skip = "No high-resolution waitable timer on this platform.";
    }
}
