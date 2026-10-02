using FrameFlow.Playback.Core;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The pacer reports an empty ring as an underrun once it has waited
/// <see cref="PacerStarvation.Threshold"/>, and reports ready when frames flow again (#547).
/// Time is a <see cref="FakeTimeProvider"/> the test advances. Whether a wait is timed at all is
/// read from <see cref="ClockSelectVideoSink.StarvationWait"/> once the loop has parked on it:
/// asserting that no underrun was reported after advancing time would race the loop's wake.
/// </summary>
public sealed partial class ClockSelectVideoSinkTests
{
    [Fact]
    public async Task AnEmptyRing_PastTheThreshold_ReportsAnUnderrun_AndAFrameReportsReady()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time, onStarved: edges.Starved, onFed: edges.Fed);

        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);
        // Parked on the empty ring with the starvation timer armed.
        await pacer.WaitForParkAfterAsync(parked);

        time.Advance(PacerStarvation.MinimumWait);
        await edges.WaitForStarvedAsync(1);

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(33)), default);
        await edges.WaitForFedAsync(1);

        Assert.Equal((1, 1), edges.Counts);
    }

    [Fact]
    public async Task AnEmptyRing_ShortOfTheThreshold_ReportsNothing()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time, onStarved: edges.Starved, onFed: edges.Fed);

        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);
        await pacer.WaitForParkAfterAsync(parked);

        time.Advance(PacerStarvation.MinimumWait - TimeSpan.FromMilliseconds(1));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(2);

        Assert.Equal((0, 0), edges.Counts);
    }

    [Fact]
    public async Task BeforeTheRunsFirstFrame_AnEmptyRingIsNotTimed()
    {
        // The startup and a seek wait for their first frame; that wait is not an underrun.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time, onStarved: edges.Starved, onFed: edges.Fed);

        await pacer.WaitForParkAfterAsync(0);

        Assert.Null(pacer.StarvationWait);

        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);
        await pacer.WaitForParkAfterAsync(parked);

        Assert.Equal(PacerStarvation.MinimumWait, pacer.StarvationWait);
    }

    [Fact]
    public async Task APausedPacer_IsNotStarved_AndAfterTheResumeAStillEmptyRingIsReportedAgain()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time, onStarved: edges.Starved, onFed: edges.Fed);

        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);
        await pacer.WaitForParkAfterAsync(parked);
        time.Advance(PacerStarvation.MinimumWait);
        await edges.WaitForStarvedAsync(1);

        // The pause forgets the underrun without reporting it resolved: the controller leaves
        // Rebuffering for Paused by itself. A paused wait is not timed.
        parked = pacer.ParkGeneration;
        pacer.Pause();
        await pacer.WaitForParkAfterAsync(parked);
        Assert.Null(pacer.StarvationWait);

        parked = pacer.ParkGeneration;
        pacer.Resume();
        await pacer.WaitForParkAfterAsync(parked);
        Assert.Equal(PacerStarvation.MinimumWait, pacer.StarvationWait);
        Assert.Equal((1, 0), edges.Counts);

        time.Advance(PacerStarvation.MinimumWait);
        await edges.WaitForStarvedAsync(2);
        Assert.Equal((2, 0), edges.Counts);
    }

    [Fact]
    public async Task InputCompleting_WhileStarved_ReportsReady_SoTheEndIsReportedFromPlaying()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time, onStarved: edges.Starved, onFed: edges.Fed);

        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);
        await pacer.WaitForParkAfterAsync(parked);
        time.Advance(PacerStarvation.MinimumWait);
        await edges.WaitForStarvedAsync(1);

        pacer.SignalInputComplete();
        await edges.WaitForFedAsync(1);

        Assert.Equal((1, 1), edges.Counts);
    }

    [Fact]
    public async Task AStarvedRing_IsReportedOnce_HoweverLongItStaysEmpty()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        var edges = new EdgeRecorder();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time, onStarved: edges.Starved, onFed: edges.Fed);

        long parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);
        await pacer.WaitForParkAfterAsync(parked);
        parked = pacer.ParkGeneration;
        time.Advance(PacerStarvation.MinimumWait);
        await edges.WaitForStarvedAsync(1);
        await pacer.WaitForParkAfterAsync(parked);

        // Starved, the wait is no longer timed, so it cannot report a second underrun.
        Assert.Null(pacer.StarvationWait);
    }

    /// <summary>Counts the pacer's starvation edges and lets a test wait for the nth of each.</summary>
    private sealed class EdgeRecorder
    {
        private readonly object _lock = new();
        private readonly List<(bool Starved, int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _starved;
        private int _fed;

        public (int Starved, int Fed) Counts
        {
            get
            {
                lock (_lock)
                    return (_starved, _fed);
            }
        }

        public void Starved() => Record(starved: true);

        public void Fed() => Record(starved: false);

        public Task WaitForStarvedAsync(int count) => WaitFor(starved: true, count);

        public Task WaitForFedAsync(int count) => WaitFor(starved: false, count);

        private void Record(bool starved)
        {
            List<TaskCompletionSource> ready = [];
            lock (_lock)
            {
                int now = starved ? ++_starved : ++_fed;
                _waiters.RemoveAll(w =>
                {
                    if (w.Starved != starved || w.Count > now)
                        return false;
                    ready.Add(w.Signal);
                    return true;
                });
            }

            foreach (var signal in ready)
                signal.SetResult();
        }

        // The bound only turns a missing edge into a failure rather than a hang.
        private Task WaitFor(bool starved, int count)
        {
            lock (_lock)
            {
                if ((starved ? _starved : _fed) >= count)
                    return Task.CompletedTask;
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((starved, count, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }
}
