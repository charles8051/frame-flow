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
/// change the pacing behaviour.
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
    public async Task DefaultConstruction_PacesThroughTheHighResolutionProvider()
    {
        // The parameterless constructor is what every existing caller uses, including
        // SubstrateSession, so it is the one that decides whether the fix reaches playback.
        await using var clock = new WallClockSource();
        clock.Start();

        await clock.WaitUntilAsync(TimeSpan.Zero, CancellationToken.None);

        // Off Windows, and on Windows before 10 1803, Preferred is the system provider and
        // there is nothing to assert beyond the clock still working.
        if (!HighResolutionTimeProvider.IsSupported)
            return;

        // Sleeps the system provider would round up to two ticks. Timing is asserted here
        // rather than only wiring because the default is the whole change: a regression to
        // TimeProvider.System would leave every test passing and playback back at ~34 fps.
        //
        // Over a median of 15, not one sample. A single frame period is short enough that one
        // descheduled wake-up decides the result, and this suite runs right after a build.
        var samples = new List<double>();
        for (int i = 0; i < 15; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await clock.WaitUntilAsync(clock.Latest + FramePeriod, CancellationToken.None);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        samples.Sort();
        var median = samples[samples.Count / 2];

        Assert.True(
            median < 25.0,
            $"one 60 fps frame period took {median:F2} ms at the median of {samples.Count} "
                + $"(min {samples[0]:F2}, max {samples[^1]:F2}), which is the quantized cost "
                + "the default provider exists to avoid"
        );
    }

    /// <summary>A 60 fps frame period: just over one system tick, which is the defect.</summary>
    private static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(16.67);

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
