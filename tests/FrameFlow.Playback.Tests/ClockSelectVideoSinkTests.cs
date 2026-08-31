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

        await pacer.PresentAsync(f1, default);
        await pacer.PresentAsync(f2, default);
        // Not yet due — clock still at 0.
        await Task.Delay(30);
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

        // Third enqueue must block — the ring is full and nothing is due yet.
        var third = pacer.PresentAsync(new TrackingFrame(TimeSpan.FromMilliseconds(30)), default);
        await Task.Delay(40);
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
        await pacer.PresentAsync(f1, default);
        await pacer.PresentAsync(f2, default);
        // Give the loop a moment to pick up f1 as "earliest" and park on the clock.
        await Task.Delay(30);

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
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7), holdForSettle: true);
        clock.Advance(TimeSpan.FromSeconds(7.73)); // the clock ran through the decode-forward

        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7)), default);

        Assert.Equal(
            TimeSpan.FromSeconds(7),
            await pacer.WaitForSeekTargetAsync(pacer.CurrentRunId, TimeSpan.FromSeconds(5), default)
        );

        // Long enough that an unheld loop would have delivered it several times over.
        await Task.Delay(120);
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
        await using var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        pacer.BeginRun(TimeSpan.FromSeconds(7), holdForSettle: true);
        var staleRun = pacer.CurrentRunId;
        clock.Advance(TimeSpan.FromSeconds(7.73));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(7)), default);

        // The next seek supersedes it before the first settle got anywhere.
        pacer.Flush();
        pacer.BeginRun(TimeSpan.FromSeconds(20), holdForSettle: true);
        clock.Advance(TimeSpan.FromSeconds(20.5));
        await pacer.PresentAsync(new TrackingFrame(TimeSpan.FromSeconds(20)), default);

        pacer.ReleaseSeekSettle(staleRun);

        // Shorter than the backstop, so this is measuring the scoping and not it.
        await Task.Delay(120);
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

        pacer.SignalInputComplete();
        var drain = pacer.WaitForDrainAsync(default);

        // f1 isn't due yet, so drain must NOT complete.
        await Task.Delay(40);
        Assert.False(drain.IsCompleted, "drain must wait for the last buffered frame to play");

        // f1 is now due and delivered, but its 33ms display interval has NOT elapsed
        // (clock 60 < end 83). Draining here would cut the final frame short and fire
        // Ended ~one frame early — the drain must keep waiting.
        clock.Advance(TimeSpan.FromMilliseconds(60));
        await sink.WaitForCountAsync(2);
        await Task.Delay(40);
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
        await Task.Delay(40); // let the loop enter the end-of-content hold (clock 0 < 33).

        pacer.Flush(); // a seek/loop discontinuity mid-hold.

        // A Flush is NOT end-of-stream: the hold must break and re-evaluate, never fire
        // EOS on the discontinuity (which would advance/loop a signage playlist spuriously).
        await Task.Delay(80);
        Assert.False(drain.IsCompleted, "Flush during the end-of-content hold must not fire EOS");
    }

    [Fact]
    public async Task Dispose_DisposesBufferedFrames_ButNotInnerSink()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4);

        var f1 = new TrackingFrame(TimeSpan.FromMilliseconds(500)); // far future, never due.
        await pacer.PresentAsync(f1, default);
        await Task.Delay(20);

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
    private sealed class FakeClock : IClockSource
    {
        private readonly object _lock = new();
        private TimeSpan _now = TimeSpan.Zero;
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Latest
        {
            get { lock (_lock) return _now; }
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
        public bool IsDisposed { get; private set; }

        public IReadOnlyList<TimeSpan> PresentedPts
        {
            get { lock (_lock) return _pts.ToArray(); }
        }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            lock (_lock)
                _pts.Add(frame.Pts);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, int timeoutMs = 2000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (true)
            {
                lock (_lock)
                {
                    if (_pts.Count >= count)
                        return;
                }
                try
                { await Task.Delay(5, cts.Token); }
                catch (OperationCanceledException)
                {
                    int have;
                    lock (_lock)
                        have = _pts.Count;
                    throw new TimeoutException($"Expected {count} presented frames; have {have}.");
                }
            }
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
