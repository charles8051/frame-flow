using FrameFlow.Media;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Deterministic tests for <see cref="ScrubSeekDispatcher"/> — the
/// coalescing, single-in-flight policy behind the seek bar's scrub handling.
/// </summary>
/// <remarks>
/// The dispatcher awaits the injected seek delegate without
/// <c>ConfigureAwait(false)</c>, so its continuations resume on the caller's
/// synchronization context. xUnit installs one around every test, which would
/// post those continuations asynchronously and make the pump's progress
/// non-deterministic. Each test clears it first (<see cref="Deterministic"/>)
/// so the <see cref="TaskCompletionSource"/> gates run continuations inline —
/// completing a gate then synchronously advances the pump, exactly as
/// asserted.
/// </remarks>
public sealed class ScrubSeekDispatcherTests
{
    private static void Deterministic() =>
        SynchronizationContext.SetSynchronizationContext(null);

    /// <summary>
    /// Fake seek IO: records every requested target and hands back a task the
    /// test completes (or faults) on demand, driving the dispatcher's pump one
    /// step at a time.
    /// </summary>
    private sealed class FakeSeek
    {
        private readonly Queue<TaskCompletionSource> _gates = new();

        public List<TimeSpan> Targets { get; } = new();

        /// <summary>Issued-but-not-yet-resolved seeks. Should never exceed 1.</summary>
        public int Outstanding => _gates.Count;

        public Task Seek(TimeSpan target)
        {
            Targets.Add(target);
            var gate = new TaskCompletionSource();
            _gates.Enqueue(gate);
            return gate.Task;
        }

        public void CompleteOne()
        {
            Assert.True(_gates.Count > 0, "no outstanding seek to complete");
            _gates.Dequeue().SetResult();
        }

        public void FaultOne()
        {
            Assert.True(_gates.Count > 0, "no outstanding seek to fault");
            _gates.Dequeue().SetException(new InvalidOperationException("seek failed"));
        }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void FirstRequest_IssuesSeekImmediately()
    {
        Deterministic();
        var fake = new FakeSeek();
        var d = new ScrubSeekDispatcher(fake.Seek);

        d.Request(S(5));

        Assert.Equal(new[] { S(5) }, fake.Targets);
        Assert.True(d.IsSeeking);
        Assert.Equal(S(5), d.LastRequested);
        Assert.Equal(1, fake.Outstanding);
    }

    [Fact]
    public void RequestsDuringInFlightSeek_CoalesceToLatest()
    {
        Deterministic();
        var fake = new FakeSeek();
        var d = new ScrubSeekDispatcher(fake.Seek);

        // A drag: the first target goes out immediately; the rest pile up
        // behind the in-flight seek.
        d.Request(S(1));
        d.Request(S(2));
        d.Request(S(3));
        d.Request(S(4));

        // 2 and 3 were superseded by 4 before any of them could be issued —
        // only the first seek has actually been sent so far.
        Assert.Equal(new[] { S(1) }, fake.Targets);
        Assert.Equal(1, fake.Outstanding);

        // Finishing the in-flight seek issues only the latest pending (4),
        // never the skipped 2 and 3.
        fake.CompleteOne();
        Assert.Equal(new[] { S(1), S(4) }, fake.Targets);

        // Nothing left queued → the pump goes idle.
        fake.CompleteOne();
        Assert.False(d.IsSeeking);
        Assert.Equal(0, fake.Outstanding);
    }

    [Fact]
    public void FinalTarget_AlwaysCommits_EvenAfterCoalescing()
    {
        Deterministic();
        var fake = new FakeSeek();
        var d = new ScrubSeekDispatcher(fake.Seek);

        d.Request(S(10)); // issued
        d.Request(S(20)); // pending, then superseded
        d.Request(S(30)); // pending — the pointer-release position
        fake.CompleteOne(); // finishes seek(10) → issues the latest, seek(30)

        Assert.Equal(S(30), fake.Targets[^1]);
        Assert.Equal(S(30), d.LastRequested);

        fake.CompleteOne();
        Assert.False(d.IsSeeking);
    }

    [Fact]
    public void LastRequested_TracksNewestTarget_Immediately()
    {
        Deterministic();
        var fake = new FakeSeek();
        var d = new ScrubSeekDispatcher(fake.Seek);

        d.Request(S(1));
        Assert.Equal(S(1), d.LastRequested);

        // Updates synchronously on each request, even while a seek is in
        // flight — this is what the seek bar pins the thumb to during a scrub.
        d.Request(S(7));
        Assert.Equal(S(7), d.LastRequested);
        Assert.True(d.IsSeeking);
    }

    [Fact]
    public void FaultedSeek_DoesNotAbandonQueuedTarget()
    {
        Deterministic();
        var fake = new FakeSeek();
        var d = new ScrubSeekDispatcher(fake.Seek);

        d.Request(S(1)); // issued
        d.Request(S(2)); // pending
        fake.FaultOne(); // seek(1) throws — the pump must keep draining

        Assert.Equal(new[] { S(1), S(2) }, fake.Targets);

        fake.CompleteOne();
        Assert.False(d.IsSeeking);
    }

    [Fact]
    public void RequestAfterDrain_StartsFreshPump()
    {
        Deterministic();
        var fake = new FakeSeek();
        var d = new ScrubSeekDispatcher(fake.Seek);

        d.Request(S(1));
        fake.CompleteOne();
        Assert.False(d.IsSeeking);

        // A later, independent seek (e.g. a keyboard arrow well after the
        // drag) starts a new pump rather than being dropped.
        d.Request(S(2));
        Assert.True(d.IsSeeking);
        Assert.Equal(new[] { S(1), S(2) }, fake.Targets);

        fake.CompleteOne();
        Assert.False(d.IsSeeking);
    }

    [Fact]
    public void Constructor_NullSeek_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ScrubSeekDispatcher(null!));
    }
}
