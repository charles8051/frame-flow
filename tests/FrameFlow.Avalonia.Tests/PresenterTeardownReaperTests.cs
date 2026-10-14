using FrameFlow.Avalonia.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0086 decision 3 on <see cref="PresenterTeardownReaper"/>, the one route by which the
/// interop view frees a producer it cannot free at once: nothing is disposed before the ring it
/// filled is released, a wait that goes on is named once, the disposal runs on the thread pool
/// rather than on whatever thread released the ring, and every producer is disposed even when one
/// throws, with the fault logged.
/// </summary>
/// <remarks>
/// The ring's release is a completion source the test completes. The wait's warning interval runs on
/// a <see cref="FakeTimeProvider"/>, whose timer fires inside <see cref="FakeTimeProvider.Advance"/>:
/// the warning is raised only while the reaper is waiting on the release, so it is also the signal
/// that the reaper got that far.
/// </remarks>
public sealed class PresenterTeardownReaperTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // SlowWorkWarning.DefaultInterval, which the reaper waits before it says what it waits for.
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(5);

    private const string Waits = "waits for the compositor to release the ring";

    [Fact]
    public async Task ADroppedProducer_IsHeldUntilItsRingIsReleased_AndThenDisposed()
    {
        var log = new RecordingLoggerFactory();
        var time = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new RecordingProducer(release.Task);

        var retired = PresenterTeardownReaper.Retire(release.Task, log.CreateLogger("reaper"), time, producer);
        time.Advance(WarningInterval);

        Assert.Single(Warnings(log), m => m.Contains(Waits, StringComparison.Ordinal));
        Assert.False(producer.Disposed);
        Assert.False(retired.IsCompleted);

        release.SetResult();
        await retired.WaitAsync(Bound);

        Assert.True(producer.Disposed);
        Assert.True(producer.DisposedAfterRelease);
        time.Advance(WarningInterval);
        Assert.Single(Warnings(log)); // said once, and nothing went wrong
    }

    [Fact]
    public async Task ARetiredProducer_IsDisposedOnThePool_NotOnTheThreadThatReleasedItsRing()
    {
        // Completed without RunContinuationsAsynchronously, as a compositor's task may be: whatever
        // waits on it can run inside SetResult, on the releasing thread. That thread is a dedicated
        // one, held until the disposal has happened, so the pool cannot hand the disposal to it.
        var release = new TaskCompletionSource();
        var producer = new RecordingProducer(release.Task);
        var retired = PresenterTeardownReaper.Retire(
            release.Task, new RecordingLoggerFactory().CreateLogger("reaper"), new FakeTimeProvider(), producer);

        var releaser = new Thread(() =>
        {
            release.SetResult();
            producer.WhenDisposed.Wait(Bound);
        });
        releaser.Start();

        await retired.WaitAsync(Bound);
        Assert.True(releaser.Join(Bound));
        Assert.NotEqual(releaser.ManagedThreadId, producer.DisposedOnThread);
    }

    [Fact]
    public async Task EveryRetiredProducerIsDisposed_EvenWhenOneThrows_AndTheFaultIsLogged()
    {
        var log = new RecordingLoggerFactory();
        var throwing = new RecordingProducer(throwOnDispose: true);
        var after = new RecordingProducer();

        // Never faults: there is no caller on the present path to hand a fault to.
        await PresenterTeardownReaper
            .Retire(Task.CompletedTask, log.CreateLogger("reaper"), new FakeTimeProvider(), throwing, after)
            .WaitAsync(Bound);

        Assert.True(throwing.Disposed);
        Assert.True(after.Disposed);
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning && e.Error?.Message == RecordingProducer.DisposeFault);
    }

    [Fact]
    public async Task EveryDeferredProducerIsDisposed_EvenWhenOneThrows_AndTheFaultIsLogged()
    {
        var log = new RecordingLoggerFactory();
        var throwing = new RecordingProducer(throwOnDispose: true);
        var after = new RecordingProducer();

        await PresenterTeardownReaper
            .Enqueue(Task.CompletedTask, log.CreateLogger("reaper"), throwing, after)
            .WaitAsync(Bound);

        Assert.True(after.Disposed);
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning && e.Error?.Message == RecordingProducer.DisposeFault);
    }

    private static IEnumerable<string> Warnings(RecordingLoggerFactory log) =>
        log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
}
