using Microsoft.Extensions.Time.Testing;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Deterministic unit coverage for the ADR-0057 Stage 2 presenter-side
/// select-by-clock pacer — the pure core (<see cref="ClockSelectBuffer"/>) and
/// the shell decorator (<see cref="ClockSelectVideoSink"/>). No FFmpeg / corpus:
/// a hand-driven fake clock and tracking frames make the timing exact.
/// </summary>
public sealed class ClockSelectVideoSinkTests
{
    // ── Pure core ─────────────────────────────────────────────────

    [Fact]
    public void Select_PresentsFreshestDueFrame_AndDropsEarlierDueOnes()
    {
        var buffer = new ClockSelectBuffer(capacity: 8);
        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0));
        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(33));
        var f2 = new TrackingFrame(TimeSpan.FromMilliseconds(66));
        var f3 = new TrackingFrame(TimeSpan.FromMilliseconds(99));
        buffer.Admit(f0);
        buffer.Admit(f1);
        buffer.Admit(f2);
        buffer.Admit(f3);

        var dropped = new List<IVideoFrame>();
        // now = 70ms: f0,f1,f2 are due (<=70); f2 is the freshest due → present;
        // f0,f1 are late → drop. f3 (99ms) stays buffered.
        var present = buffer.Select(TimeSpan.FromMilliseconds(70), dropped);

        Assert.Same(f2, present);
        Assert.Equal(new IVideoFrame[] { f0, f1 }, dropped);
        Assert.Equal(1, buffer.Count); // only f3 remains
        Assert.Equal(TimeSpan.FromMilliseconds(99), buffer.EarliestPts);
    }

    [Fact]
    public void Select_NothingDue_ReturnsNull_AndKeepsBuffer()
    {
        var buffer = new ClockSelectBuffer(capacity: 4);
        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(50));
        buffer.Admit(f0);

        var dropped = new List<IVideoFrame>();
        var present = buffer.Select(TimeSpan.FromMilliseconds(10), dropped);

        Assert.Null(present);
        Assert.Empty(dropped);
        Assert.Equal(1, buffer.Count);
    }

    // ── The post-seek floor (#157) ────────────────────────────────

    [Fact]
    public void Floor_RefusesFramesBelowTheSeekTarget()
    {
        // Seeking to 7 s restarts the demuxer at the keyframe before it, so frames from
        // the keyframe up to the target arrive carrying a PTS the clock is already past.
        // They are references, not content to show.
        var buffer = new ClockSelectBuffer(capacity: 8);
        buffer.SetFloor(TimeSpan.FromSeconds(7));

        var early = new TrackingFrame(TimeSpan.FromSeconds(6.5));
        var justUnder = new TrackingFrame(TimeSpan.FromMilliseconds(6983));

        Assert.False(buffer.Admit(early));
        Assert.False(buffer.Admit(justUnder));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Floor_AdmitsTheTargetFrameAndIsThenSpent()
    {
        var buffer = new ClockSelectBuffer(capacity: 8);
        buffer.SetFloor(TimeSpan.FromSeconds(7));

        Assert.False(buffer.Admit(new TrackingFrame(TimeSpan.FromSeconds(6.9))));
        Assert.True(buffer.HasFloor);

        var target = new TrackingFrame(TimeSpan.FromSeconds(7));
        Assert.True(buffer.Admit(target));

        // One-shot. Anything that arrives afterwards is ordinary content, judged only by
        // the clock — including a frame below the old target, which on a well-behaved
        // stream cannot happen and on a misbehaving one must not vanish silently.
        Assert.False(buffer.HasFloor);
        Assert.True(buffer.Admit(new TrackingFrame(TimeSpan.FromSeconds(1))));
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Floor_WithoutOneEveryFrameIsAdmitted()
    {
        // The steady-state path: no seek pending, nothing is refused.
        var buffer = new ClockSelectBuffer(capacity: 8);

        Assert.False(buffer.HasFloor);
        Assert.True(buffer.Admit(new TrackingFrame(TimeSpan.Zero)));
        Assert.True(buffer.Admit(new TrackingFrame(TimeSpan.FromMilliseconds(16))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void Floor_AtOrBelowZeroIsNotAFloor(int milliseconds)
    {
        // A RepeatMode.One rewind runs the same discontinuity recipe with target 0, where
        // every frame qualifies anyway and a floor is pure risk. A file with an edit list
        // can also open on slightly negative PTS, which a floor of exactly zero would eat.
        var buffer = new ClockSelectBuffer(capacity: 4);
        buffer.SetFloor(TimeSpan.FromMilliseconds(milliseconds));

        Assert.False(buffer.HasFloor);
        Assert.True(buffer.Admit(new TrackingFrame(TimeSpan.FromMilliseconds(-20))));
        Assert.True(buffer.Admit(new TrackingFrame(TimeSpan.Zero)));
    }

    [Fact]
    public void Floor_DoesNotChangeWhatSelectDoes()
    {
        // The floor decides admission; the clock still decides presentation. A frame at
        // the target is not due until the clock reaches it.
        var buffer = new ClockSelectBuffer(capacity: 4);
        buffer.SetFloor(TimeSpan.FromSeconds(7));
        buffer.Admit(new TrackingFrame(TimeSpan.FromSeconds(7)));

        var dropped = new List<IVideoFrame>();
        Assert.Null(buffer.Select(TimeSpan.FromSeconds(6.9), dropped));
        Assert.NotNull(buffer.Select(TimeSpan.FromSeconds(7), dropped));
    }

    [Fact]
    public void DrainInto_RemovesEverything()
    {
        var buffer = new ClockSelectBuffer(capacity: 4);
        var f0 = new TrackingFrame(TimeSpan.Zero);
        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(33));
        buffer.Admit(f0);
        buffer.Admit(f1);

        var sink = new List<IVideoFrame>();
        buffer.DrainInto(sink);

        Assert.Equal(new IVideoFrame[] { f0, f1 }, sink);
        Assert.True(buffer.IsEmpty);
    }

    // ── Shell: select-by-clock delivery ───────────────────────────

    [Fact]
    public async Task DeliversFramesInOrder_AtTheirClockTime()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0));
        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(33));
        var f2 = new TrackingFrame(TimeSpan.FromMilliseconds(66));

        // clock starts at 0 → f0 is due immediately.
        await pacer.PresentAsync(f0, default);
        await sink.WaitForCountAsync(1);
        Assert.Equal(new[] { TimeSpan.Zero }, sink.PresentedPts);

        var parked = pacer.ParkGeneration;
        await pacer.PresentAsync(f1, default);
        await pacer.PresentAsync(f2, default);
        // Not yet due — clock still at 0. Parking means the loop has seen both frames and
        // decided against them; the sleep this replaces only meant "probably long enough".
        await pacer.WaitForParkAfterAsync(parked);
        Assert.Single(sink.PresentedPts);

        clock.Advance(TimeSpan.FromMilliseconds(40)); // f1 (33) due, f2 (66) not.
        await sink.WaitForCountAsync(2);
        Assert.Equal(TimeSpan.FromMilliseconds(33), sink.PresentedPts[1]);

        clock.Advance(TimeSpan.FromMilliseconds(70)); // f2 due.
        await sink.WaitForCountAsync(3);
        Assert.Equal(TimeSpan.FromMilliseconds(66), sink.PresentedPts[2]);

        Assert.Equal(0, pacer.DroppedLate);
    }

    [Fact]
    public async Task DropsLateFrames_WhenClockJumpsPastSeveral()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 8);

        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0));
        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(33));
        var f2 = new TrackingFrame(TimeSpan.FromMilliseconds(66));
        var f3 = new TrackingFrame(TimeSpan.FromMilliseconds(99));

        // Present f0 first (due at 0) and let it through, so the loop is parked.
        await pacer.PresentAsync(f0, default);
        await sink.WaitForCountAsync(1);

        // Buffer the rest while the clock is still at 0.
        await pacer.PresentAsync(f1, default);
        await pacer.PresentAsync(f2, default);
        await pacer.PresentAsync(f3, default);

        // Jump the clock past f1 and f2: f3 is the freshest due → present;
        // f1 and f2 are late → dropped + disposed.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await sink.WaitForCountAsync(2);

        Assert.Equal(TimeSpan.FromMilliseconds(99), sink.PresentedPts[1]); // f3 presented as freshest due
        Assert.True(f1.IsDisposed, "late frame f1 should be disposed");
        Assert.True(f2.IsDisposed, "late frame f2 should be disposed");
        Assert.Equal(2, pacer.DroppedLate);
    }

    [Fact]
    public async Task PresentAsync_BlocksWhenRingFull_UntilClockDrains()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 2);

        // Fill the ring with two not-yet-due frames (clock at 0).
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(10)), default);
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(20)), default);

        // The loop has seen both frames and parked with neither due, so the ring is full and
        // no slot has been released. A third enqueue blocks on that, and cannot be completed
        // by anything until the clock moves — so this needs no wait, only the park above.
        await pacer.WaitForParkAfterAsync(0);
        var third = pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(30)), default);
        Assert.Empty(sink.PresentedPts);
        Assert.False(third.IsCompleted, "PresentAsync should backpressure when the ring is full");

        // Advance the clock so the first frame is delivered, freeing a slot.
        clock.Advance(TimeSpan.FromMilliseconds(15));
        await third.AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // now unblocks.
    }

    [Fact]
    public async Task Flush_DropsAndDisposesBufferedFrames()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(50));
        var f2 = new TrackingFrame(TimeSpan.FromMilliseconds(80));
        var parked = pacer.ParkGeneration;
        await pacer.PresentAsync(f1, default);
        await pacer.PresentAsync(f2, default);
        // The loop has picked up f1 as "earliest" and parked on the clock. Flushing before
        // that would be flushing a buffer the loop had not looked at.
        await pacer.WaitForParkAfterAsync(parked);

        pacer.Flush();

        Assert.True(f1.IsDisposed, "flushed frame f1 should be disposed");
        Assert.True(f2.IsDisposed, "flushed frame f2 should be disposed");
        Assert.Empty(sink.PresentedPts);

        // After flush the ring slots are free again: a fresh due frame flows.
        var f3 = new TrackingFrame(TimeSpan.FromMilliseconds(0));
        await pacer.PresentAsync(f3, default);
        await sink.WaitForCountAsync(1);
    }

    [Fact]
    public async Task BeginRun_WithASeekFloor_DiscardsPreTargetFramesInsteadOfPresenting()
    {
        // The #157 regression. Before the fix these frames were presented — each arrived
        // alone, each was already due against a clock seated at the target, so each was
        // the freshest due frame at its own moment and the late-drop rule never fired.
        // The GOP played out at decode rate.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7)); // the launch that commits the seek
        clock.Advance(TimeSpan.FromSeconds(7)); // step 6: clock seated on the target

        // Reference frames from the keyframe the demuxer landed on.
        var preTarget = new[]
        {
            new TrackingFrame(TimeSpan.FromSeconds(6.90)),
            new TrackingFrame(TimeSpan.FromSeconds(6.95)),
            new TrackingFrame(TimeSpan.FromMilliseconds(6983)),
        };
        foreach (var f in preTarget)
            await pacer.PresentAsync(f, default);

        // The destination frame.
        var atTarget = new TrackingFrame(TimeSpan.FromSeconds(7));
        await pacer.PresentAsync(atTarget, default);

        await sink.WaitForCountAsync(1);

        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, sink.PresentedPts);
        Assert.All(preTarget, f => Assert.True(f.IsDisposed, "a pre-target frame was not released"));
        Assert.Equal(preTarget.Length, pacer.DroppedBeforeTarget);

        // Ring slots came back, so the decoder was never held up by the discards — the
        // decode-forward to the target is the seek's cost either way.
        Assert.Equal(0, pacer.DroppedLate);
    }

    [Fact]
    public async Task BeginRun_WithoutASeekFloor_KeepsAdmittingEverything()
    {
        // Every launch that is not a committed seek — first play, a loop rewind — passes
        // no floor. Nothing may be refused there.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.Flush();

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.Zero), default);
        await sink.WaitForCountAsync(1);

        Assert.Equal(0, pacer.DroppedBeforeTarget);
    }

    // ── The post-seek clock settle (#161) ─────────────────────────

    [Fact]
    public async Task WaitForSeekTarget_ReportsTheFirstFrameAtTheFloor()
    {
        // The session waits on this to put the clocks on the frame that actually arrived,
        // so the decoder's walk from the keyframe does not count as playback time.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7));
        var waiting = pacer.WaitForSeekTargetAsync(pacer.CurrentRunId, TimeSpan.FromSeconds(5), default);

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(6.9)), default);
        Assert.False(waiting.IsCompleted, "a pre-target frame must not satisfy the wait");

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7.017)), default);

        Assert.Equal(TimeSpan.FromSeconds(7.017), await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheDestinationFrameIsHeldUntilTheClocksAreReseated()
    {
        // Publishing the target and delivering it are two different moments, and the clock
        // between them is the one the reseat exists to correct. Without the hold the loop
        // wakes on arrival, finds this frame due against a clock 0.7 s past it, and presents
        // it — along with whatever follows, at decode rate. That is the run-up (#161).
        var clock = new FakeClock();
        var sink = new RecordingSink();
        // The settle hold has a 250 ms real-time backstop behind it. Held on a fake provider
        // that nobody advances, it cannot fire, so the assertion below is about the hold
        // rather than about which of two wall clocks won (#78).
        var time = new FakeTimeProvider();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time);

        pacer.BeginRun(TimeSpan.FromSeconds(7), holdForSettle: true);
        clock.Advance(TimeSpan.FromSeconds(7.73)); // the clock ran through the decode-forward

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7)), default);

        Assert.Equal(
            TimeSpan.FromSeconds(7),
            await pacer.WaitForSeekTargetAsync(pacer.CurrentRunId, TimeSpan.FromSeconds(5), default)
        );

        // The loop has parked inside the hold. An unheld loop would have found this frame due
        // against a clock 0.73 s past it and presented it before ever parking.
        await pacer.WaitForParkAfterAsync(0);
        Assert.Empty(sink.PresentedPts);

        // The session reseats, then releases.
        clock.Advance(TimeSpan.FromSeconds(7));
        pacer.ReleaseSeekSettle(pacer.CurrentRunId);

        await sink.WaitForCountAsync(1);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, sink.PresentedPts);
    }

    [Fact]
    public async Task AHeldDestinationFrameIsReleasedEvenIfNobodyReseats()
    {
        // The backstop. Every path that arms the hold releases it, but a path added later
        // that forgets should cost a hiccup, not a frozen picture.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7), holdForSettle: true);
        clock.Advance(TimeSpan.FromSeconds(7.73));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7)), default);

        await sink.WaitForCountAsync(1);
    }

    [Fact]
    public async Task AStaleSettleDoesNotReleaseANewerRunsHold()
    {
        // A settle can finish late — past its cap, or after a scheduler delay — by which
        // time the next seek has started and armed a hold of its own. An unscoped release
        // would open that one's gate and deliver its destination frame against the clock
        // its own settle has not corrected yet.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider(); // backstop parked; see the reseat test above.
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, timeProvider: time);

        pacer.BeginRun(TimeSpan.FromSeconds(7), holdForSettle: true);
        var staleRun = pacer.CurrentRunId;
        clock.Advance(TimeSpan.FromSeconds(7.73));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7)), default);

        // The next seek supersedes it before the first settle got anywhere.
        pacer.Flush();
        pacer.BeginRun(TimeSpan.FromSeconds(20), holdForSettle: true);
        clock.Advance(TimeSpan.FromSeconds(20.5));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(20)), default);

        // Park the loop inside the newer run's hold first, so "still held" below is a
        // statement about the release rather than about the loop not having got there yet.
        await pacer.WaitForParkAfterAsync(0);
        Assert.True(pacer.IsSettleHeld);

        pacer.ReleaseSeekSettle(staleRun);

        // The scoping check, and it is synchronous: ReleaseSeekSettle either cleared the flag
        // or it did not. Waiting to see whether a frame came out was asking the same question
        // through a scheduler.
        Assert.True(pacer.IsSettleHeld, "a stale release must not clear the newer run's hold");
        Assert.Empty(sink.PresentedPts);

        clock.Advance(TimeSpan.FromSeconds(20));
        pacer.ReleaseSeekSettle(pacer.CurrentRunId);

        await sink.WaitForCountAsync(1);
        Assert.Equal(new[] { TimeSpan.FromSeconds(20) }, sink.PresentedPts);
    }

    [Fact]
    public async Task ARunNobodyWillReseatDoesNotHoldItsDestinationFrame()
    {
        // First play after a seek, and the resume after a paused seek, both arm a floor and
        // then carry on: they run on the controller's command loop, which cannot wait for
        // the decoder. Holding delivery for a reseat those runs never perform would stall
        // the picture until the backstop expired.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7)); // holdForSettle defaults to false
        clock.Advance(TimeSpan.FromSeconds(7.73));

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7)), default);

        // Delivered on the spot, not after the 250 ms backstop.
        await sink.WaitForCountAsync(1).WaitAsync(TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task WaitForSeekTarget_ReturnsAtOnceForARunWithNoFloor()
    {
        // Ordinary play and loop rewind. The session calls this unconditionally, so it has
        // to be free when there is nothing to wait for.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun();

        Assert.Null(await pacer.WaitForSeekTargetAsync(pacer.CurrentRunId, TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task WaitForSeekTarget_ReleasesWhenTheRunEndsWithoutReachingTheTarget()
    {
        // A target past the end of the stream. Waiting out the cap would stall the seek for
        // no reason; input completing says it is never coming.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7));
        var waiting = pacer.WaitForSeekTargetAsync(pacer.CurrentRunId, TimeSpan.FromSeconds(30), default);
        Assert.False(waiting.IsCompleted);

        pacer.SignalInputComplete();

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitForSeekTarget_GivesUpOnTheCapRatherThanThrowing()
    {
        // Every reason this does not complete is a reason to carry on with the clocks as
        // they are, so the cap returns null instead of faulting the seek.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7));

        Assert.Null(await pacer.WaitForSeekTargetAsync(pacer.CurrentRunId, TimeSpan.FromMilliseconds(50), default));
    }

    [Fact]
    public async Task WaitForDrain_CompletesOnlyAfterLastFrameFinishesDisplaying()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun();
        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0));
        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(50)); // Duration 33ms ⇒ ends at 83ms.
        await pacer.PresentAsync(f0, default);
        await pacer.PresentAsync(f1, default);
        await sink.WaitForCountAsync(1); // f0 delivered (due at 0); f1 buffered.

        // Park the loop on f1's pacing wait before signalling, so the assertion below is
        // about SignalInputComplete and not about the loop still catching up.
        await pacer.WaitForParkAfterAsync(0);

        pacer.SignalInputComplete();
        var drain = pacer.WaitForDrainAsync(default);

        // f1 isn't due yet, so drain must NOT complete. SignalInputComplete drains inline when
        // it drains at all, and the parked loop cannot until the clock moves — so there is
        // nothing to wait for here.
        Assert.False(drain.IsCompleted, "drain must wait for the last buffered frame to play");

        // f1 is now due and delivered, but its 33ms display interval has NOT elapsed
        // (clock 60 < end 83). Draining here would cut the final frame short and fire
        // Ended ~one frame early — the drain must keep waiting.
        var displayParked = pacer.ParkGeneration;
        clock.Advance(TimeSpan.FromMilliseconds(60));
        await sink.WaitForCountAsync(2);
        await pacer.WaitForParkAfterAsync(displayParked);
        Assert.False(drain.IsCompleted, "drain must hold until the last frame finishes displaying (Pts+Duration)");

        // The clock reaches the last frame's end ⇒ the run is truly over ⇒ drain completes.
        clock.Advance(TimeSpan.FromMilliseconds(83));
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sink.PresentedPts.Count);
    }

    [Fact]
    public async Task WaitForDrain_EmptyRun_CompletesImmediately()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun();
        // A zero-frame run: input completes with nothing ever presented. _lastFrameEndPts
        // stays Zero, so the end-of-content gate is satisfied at once.
        pacer.SignalInputComplete();

        await pacer.WaitForDrainAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(sink.PresentedPts);
    }

    [Fact]
    public async Task WaitForDrain_MasterStopsBeforeLastFrameEnd_CapsOutInsteadOfHanging()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        // Small maxWait so the end-of-content hold caps quickly. Models an audio master
        // that stops publishing before the video tail finishes (clock never reaches the
        // last frame's end): the drain must cap out and fire EOS, not hang forever.
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, maxWait: TimeSpan.FromMilliseconds(150));

        pacer.BeginRun();
        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0)); // ends at 33ms.
        await pacer.PresentAsync(f0, default);
        await sink.WaitForCountAsync(1);

        pacer.SignalInputComplete();
        var drain = pacer.WaitForDrainAsync(default);

        // Clock stays at 0 — it never reaches f0's end (33ms). Without the cap the hold
        // would wait forever; with it, the hold caps after maxWait and the run drains.
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(sink.PresentedPts);
    }

    [Fact]
    public async Task WaitForDrain_FlushDuringHold_DoesNotFireEosOnTheDiscontinuity()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun();
        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0)); // ends at 33ms.
        await pacer.PresentAsync(f0, default);
        await sink.WaitForCountAsync(1);

        pacer.SignalInputComplete();
        var drain = pacer.WaitForDrainAsync(default);
        // Wait for the loop to actually enter the end-of-content hold (clock 0 < 33). Flushing
        // before it gets there would test nothing, and a sleep could not tell the difference.
        await pacer.WaitForParkAfterAsync(0);

        var flushParked = pacer.ParkGeneration;
        pacer.Flush(); // a seek/loop discontinuity mid-hold.

        // A Flush is NOT end-of-stream: the hold must break and re-evaluate, never fire
        // EOS on the discontinuity (which would advance/loop a signage playlist spuriously).
        await pacer.WaitForParkAfterAsync(flushParked);
        Assert.False(drain.IsCompleted, "Flush during the end-of-content hold must not fire EOS");
    }

    // ── The wait cap vs. a pause (#127) ────────────────────────

    [Fact]
    public async Task WaitCap_ForcePresentsWhenTheMasterNeverReachesTheFramesPts()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, maxWait: TimeSpan.FromMilliseconds(120));

        pacer.BeginRun();
        // Due at 5s against a clock parked at 0: a genuinely stalled/misaligned master.
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(5)), default);

        // The cap fires and the frame presents anyway — choppy-but-alive, the behaviour
        // the cap exists for. This is the baseline the paused case must NOT match.
        await sink.WaitForCountAsync(1);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, sink.PresentedPts);
    }

    [Fact]
    public async Task WaitCap_IsSuspendedWhilePaused_SoAPauseDoesNotWalkTheRingOut()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, maxWait: TimeSpan.FromMilliseconds(120), timeProvider: time);

        pacer.BeginRun();
        pacer.Pause();

        // A full ring of undue frames against a clock stopped at 0 — exactly the state a
        // pause leaves behind. Uncapped, the cap force-presented one frame every maxWait
        // until the ring was empty, creeping the picture forward while the user was paused
        // and logging each as a suspected stalled master (#127).
        var parked = pacer.ParkGeneration;
        for (int i = 0; i < 4; i++)
            await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(100 + (33 * i))), default);
        await pacer.WaitForParkAfterAsync(parked);

        // Several maxWait periods, on the clock the cap is armed from. A suspended cap has no
        // armed timer to fire, so this cannot wake the loop and needs no second park.
        time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Empty(sink.PresentedPts);
    }

    [Fact]
    public async Task WaitCap_ReArmsOnResume_SoAStalledMasterStillDegradesToChoppy()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, maxWait: TimeSpan.FromMilliseconds(120), timeProvider: time);

        pacer.BeginRun();
        pacer.Pause();
        var parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(5)), default);
        await pacer.WaitForParkAfterAsync(parked);

        time.Advance(TimeSpan.FromMilliseconds(250)); // twice the cap, suspended, so nothing.
        Assert.Empty(sink.PresentedPts);

        // Resuming onto a master that is still not advancing is the case the cap is for.
        var resumed = pacer.ParkGeneration;
        pacer.Resume();
        await pacer.WaitForParkAfterAsync(resumed); // the cap is armed once it re-parks.

        time.Advance(TimeSpan.FromMilliseconds(150)); // past the re-armed cap.
        await sink.WaitForCountAsync(1);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, sink.PresentedPts);
    }

    [Fact]
    public async Task WaitCap_PauseMidWait_BreaksACapAlreadyCountingDown()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, maxWait: TimeSpan.FromMilliseconds(300), timeProvider: time);

        pacer.BeginRun();
        var parked = pacer.ParkGeneration;
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(5)), default);
        await pacer.WaitForParkAfterAsync(parked); // the loop is in the wait, cap armed.

        time.Advance(TimeSpan.FromMilliseconds(150)); // cap half spent, not fired.
        Assert.Empty(sink.PresentedPts);

        var pausedGen = pacer.ParkGeneration;
        pacer.Pause();
        await pacer.WaitForParkAfterAsync(pausedGen); // the recheck re-entered the wait.

        // Without the recheck the already-armed cap would fire 150 ms into the pause and
        // present one frame anyway. The pause must break that wait, not merely affect the
        // next one — so the remaining 150 ms of the original cap, and more, must pass with
        // nothing presented.
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Empty(sink.PresentedPts);
    }

    [Fact]
    public async Task WaitForDrain_PausedOverTheLastFramesDisplay_DoesNotEndTheClip()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var time = new FakeTimeProvider();
        await using var pacer = new ClockSelectVideoSink(
            sink, clock, capacity: 4, maxWait: TimeSpan.FromMilliseconds(120), timeProvider: time);

        pacer.BeginRun();
        var f0 = new TrackingFrame(TimeSpan.FromMilliseconds(0)); // ends at 33ms.
        await pacer.PresentAsync(f0, default);
        await sink.WaitForCountAsync(1);

        // Paused before input completes, so the hold is never armed with a cap in the
        // first place — the loop cannot race us to a cap-out.
        //
        // Pause on its own cannot be awaited: a loop parked for want of frames is waiting on
        // arrival, which the recheck token does not reach. SignalInputComplete sets arrival,
        // so it is the wake this parks on.
        pacer.Pause();
        var parked = pacer.ParkGeneration;
        pacer.SignalInputComplete();
        var drain = pacer.WaitForDrainAsync(default);
        await pacer.WaitForParkAfterAsync(parked);

        // Far past the 120 ms cap, on the clock the cap is armed from. Suspended by the
        // pause, so there is no armed timer to fire and nothing can wake the loop.
        time.Advance(TimeSpan.FromSeconds(30));

        // The end-of-content hold caps out on a master that stopped short, which is right
        // for a stalled one and wrong for a paused one: pausing on the last frame would
        // otherwise fire Ended and advance a playlist while the user sat on pause (#127).
        Assert.False(drain.IsCompleted, "a pause must not end the clip");

        // Resuming re-arms the cap. The master is still stopped at 0 and the last frame ends
        // at 33 ms, so it is the cap firing that ends the run — which is the behaviour the
        // pause was suppressing, now allowed to happen.
        var resumed = pacer.ParkGeneration;
        pacer.Resume();
        await pacer.WaitForParkAfterAsync(resumed);

        time.Advance(TimeSpan.FromMilliseconds(150)); // past the re-armed 120 ms cap.
        await drain;
    }

    [Fact]
    public async Task Dispose_DisposesBufferedFrames_ButNotInnerSink()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(500)); // far future, never due.
        var parked = pacer.ParkGeneration;
        await pacer.PresentAsync(f1, default);
        await pacer.WaitForParkAfterAsync(parked); // the frame is buffered and the loop is idle.

        await pacer.DisposeAsync();

        Assert.True(f1.IsDisposed, "buffered frame should be disposed on teardown");
        Assert.False(sink.IsDisposed, "the inner sink is owned by the session, not the pacer");
    }

    // ── Test doubles ──────────────────────────────────────────────

    /// <summary>
    /// A hand-driven <see cref="IClockSource"/>: <see cref="Latest"/> is whatever
    /// was last set via <see cref="Advance"/>, and <see cref="WaitUntilAsync"/>
    /// completes as soon as Latest reaches the target (re-checked on each Advance).
    /// Deterministic — no wall-clock, so tests don't race real time.
    /// </summary>
    // ── PresentationLag (#82) ────────────────────────────────────────────

    [Fact]
    public async Task PresentationLagIsNullUntilTheRunHasPresentedAFrame()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        Assert.Null(pacer.PresentationLag);

        // A clock that has run is still not a measurement: with nothing presented
        // there is no picture for the position to be ahead OF.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(pacer.PresentationLag);
    }

    [Fact]
    public async Task PresentationLagIsZeroWhileTheClockIsInsideThePresentedFramesWindow()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(holdForSettle: false);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(100)), default);
        await sink.WaitForCountAsync(1);

        // The frame occupies [100ms, 133ms). A clock anywhere in there has not run
        // past it, so a keeping-up pipeline reads exactly zero rather than jittering
        // across a frame duration.
        Assert.Equal(TimeSpan.Zero, pacer.PresentationLag);

        clock.Advance(TimeSpan.FromMilliseconds(132));
        Assert.Equal(TimeSpan.Zero, pacer.PresentationLag);
    }

    [Fact]
    public async Task PresentationLagMeasuresHowFarTheClockRanPastTheFrameOnScreen()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(holdForSettle: false);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(100)), default);
        await sink.WaitForCountAsync(1);

        // Nothing further arrives — the decoder is the bottleneck — while the clock
        // keeps going. That is the #82 shape, and this is the number that reveals it.
        clock.Advance(TimeSpan.FromSeconds(10));

        // 10s minus the frame's own 133ms window.
        Assert.Equal(TimeSpan.FromMilliseconds(9867), pacer.PresentationLag);
    }

    [Fact]
    public async Task PresentationLagResetsWithTheRunRatherThanReadingAsTheSeekTarget()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(holdForSettle: false);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(100)), default);
        await sink.WaitForCountAsync(1);

        // A seek: new run, and both clocks reseat onto the target (#164).
        pacer.BeginRun(TimeSpan.FromSeconds(20), holdForSettle: false);
        clock.Advance(TimeSpan.FromSeconds(20));

        // Null, not 20 seconds. The drain gate resets its own copy to Zero so a fresh
        // run drains immediately; measuring lag against that Zero would report the
        // whole seek target as drift until the first frame of the new run landed.
        Assert.Null(pacer.PresentationLag);
    }

    [Fact]
    public async Task PresentationLagIsNullWhenTheRunChangesUnderTheRead()
    {
        // The getter reads the frame under the gate, then the clock outside it. A seek
        // landing between the two reseats the clock by the size of the seek, so pairing
        // the old run's frame with the new run's clock would report that seek as drift.
        var clock = new FakeClock();
        var sink = new RecordingSink();
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(holdForSettle: false);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(100)), default);
        await sink.WaitForCountAsync(1);

        clock.OnLatestRead = () =>
        {
            // Exactly the interleaving the re-check exists for: the run advances while
            // the getter holds a frame from the previous one.
            clock.OnLatestRead = null;
            pacer.BeginRun(TimeSpan.FromSeconds(20), holdForSettle: false);
        };

        Assert.Null(pacer.PresentationLag);
    }

    private sealed class FakeClock : IClockSource
    {
        private readonly object _lock = new();
        private TimeSpan _now = TimeSpan.Zero;
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Runs once inside a Latest read, to interleave deterministically.</summary>
        public Action? OnLatestRead { get; set; }

        public TimeSpan Latest
        {
            get
            {
                OnLatestRead?.Invoke();
                lock (_lock)
                    return _now;
            }
        }

        public void Advance(TimeSpan to)
        {
            TaskCompletionSource old;
            lock (_lock)
            {
                _now = to;
                old = _pulse;
                _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            old.TrySetResult();
        }

        public async ValueTask WaitUntilAsync(TimeSpan target, CancellationToken ct = default)
        {
            while (true)
            {
                Task wait;
                lock (_lock)
                {
                    if (_now >= target)
                        return;
                    wait = _pulse.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Records the PTS of every frame the pacer delivers; disposes them.</summary>
    private sealed class RecordingSink : IVideoSink
    {
        private readonly object _lock = new();
        private readonly List<TimeSpan> _pts = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        public bool IsDisposed { get; private set; }

        public IReadOnlyList<TimeSpan> PresentedPts
        {
            get { lock (_lock) return _pts.ToArray(); }
        }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            List<TaskCompletionSource>? ready = null;
            lock (_lock)
            {
                _pts.Add(frame.Pts);
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_pts.Count >= _waiters[i].Count)
                    {
                        (ready ??= []).Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }
            frame.Dispose();
            if (ready is not null)
                foreach (var signal in ready)
                    signal.TrySetResult();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Completes when <paramref name="count"/> frames have been presented.
        /// </summary>
        /// <remarks>
        /// Signalled from <see cref="PresentAsync"/> rather than polled. The previous version
        /// woke every 5 ms against a 2000 ms cap and threw <see cref="TimeoutException"/> when
        /// it ran out, which on a slow machine is a second way for this file to fail without
        /// anything being wrong (#152). There is no cap here: the run settings already impose
        /// a 60 s per-test timeout, and a test that genuinely wedges should report as the
        /// hang it is rather than as a bespoke timeout from a test double.
        /// </remarks>
        public Task WaitForCountAsync(int count)
        {
            TaskCompletionSource signal;
            lock (_lock)
            {
                if (_pts.Count >= count)
                    return Task.CompletedTask;
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, signal));
            }
            return signal.Task;
        }

        public IFramePool FramePool => null!;
        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => default;
        public VideoSinkDiagnosticsSnapshot GetDiagnostics() => VideoSinkDiagnosticsSnapshot.Empty;
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return default;
        }
    }

    /// <summary>A minimal CPU <see cref="IVideoFrame"/> that tracks disposal.</summary>
    private sealed class TrackingFrame : IVideoFrame
    {
        private int _refCount = 1;
        public TrackingFrame(TimeSpan pts) => Pts = pts;
        public bool IsDisposed => Volatile.Read(ref _refCount) <= 0;
        public int Width => 4;
        public int Height => 4;
        public TimeSpan Pts { get; }
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;
        public IVideoFrame AddRef()
        {
            Interlocked.Increment(ref _refCount);
            return this;
        }
        public void Dispose() => Interlocked.Decrement(ref _refCount);
        public CpuFrameData? AsCpu() => null;
        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
