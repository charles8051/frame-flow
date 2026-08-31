namespace FrameFlow.Playback.Tests;

/// <summary>
/// Deterministic unit tests for <see cref="PlaylistCoordinator"/> — the queue /
/// loop / wrap / skip brain of gapless playlist playback. No FFmpeg or corpus
/// required: this exercises the advance decisions in isolation from the decode
/// runtime.
/// </summary>
public sealed class PlaylistCoordinatorTests
{
    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private static FakeSource S(string name) => new(name);

    private static MediaInfo Info(double seconds = 3) =>
        new("test", TimeSpan.FromSeconds(seconds), [], []);

    [Fact]
    public void EmptyInitialQueue_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new PlaylistCoordinator(Array.Empty<IMediaSource>(), RepeatMode.Off)
        );
    }

    [Fact]
    public void Off_AdvancesThroughQueueThenEnds()
    {
        var (a, b, c) = (S("a"), S("b"), S("c"));
        var coord = new PlaylistCoordinator([a, b, c], RepeatMode.Off);

        Assert.Same(a, coord.First());

        var d1 = coord.DecideNext(a);
        Assert.Equal(PlaylistCoordinator.NextKind.Advance, d1.Kind);
        Assert.Same(b, d1.Source);
        Assert.False(d1.Wrapped);

        var d2 = coord.DecideNext(b);
        Assert.Equal(PlaylistCoordinator.NextKind.Advance, d2.Kind);
        Assert.Same(c, d2.Source);

        var d3 = coord.DecideNext(c);
        Assert.Equal(PlaylistCoordinator.NextKind.End, d3.Kind);
        Assert.Null(d3.Source);
    }

    [Fact]
    public void All_LoopsAndFlagsTheWrap()
    {
        var (a, b) = (S("a"), S("b"));
        var coord = new PlaylistCoordinator([a, b], RepeatMode.All);

        Assert.Same(a, coord.First());

        var toB = coord.DecideNext(a);
        Assert.Equal(PlaylistCoordinator.NextKind.Advance, toB.Kind);
        Assert.Same(b, toB.Source);
        Assert.False(toB.Wrapped);

        // End of the queue under All wraps back to the first item, flagged.
        var wrap = coord.DecideNext(b);
        Assert.Equal(PlaylistCoordinator.NextKind.Advance, wrap.Kind);
        Assert.Same(a, wrap.Source);
        Assert.True(wrap.Wrapped);

        // ...and keeps looping a, b, a, b, ... indefinitely.
        var toB2 = coord.DecideNext(a);
        Assert.Same(b, toB2.Source);
        Assert.False(toB2.Wrapped);

        var wrap2 = coord.DecideNext(b);
        Assert.Same(a, wrap2.Source);
        Assert.True(wrap2.Wrapped);
    }

    [Fact]
    public void One_ReplaysCurrentForever()
    {
        var (a, b) = (S("a"), S("b"));
        var coord = new PlaylistCoordinator([a, b], RepeatMode.One);

        Assert.Same(a, coord.First());

        for (var i = 0; i < 3; i++)
        {
            var d = coord.DecideNext(a);
            Assert.Equal(PlaylistCoordinator.NextKind.Replay, d.Kind);
            Assert.Same(a, d.Source);
        }
    }

    [Fact]
    public void All_SingleClipWrap_IsReplayNotAdvance()
    {
        // The canonical signage attract/panel loop: ONE clip, RepeatMode.All. Each
        // wrap lands back on the very same source object, so the decision must be a
        // Replay (reuse the live runtime in place — the gapless single-clip loop),
        // NOT an Advance (which the session would service with a full teardown +
        // rebuild). This is the deterministic guard for the gapless reuse routing.
        var a = S("a");
        var coord = new PlaylistCoordinator([a], RepeatMode.All);

        Assert.Same(a, coord.First());

        for (var i = 0; i < 4; i++)
        {
            var d = coord.DecideNext(a);
            Assert.Equal(PlaylistCoordinator.NextKind.Replay, d.Kind);
            Assert.Same(a, d.Source);
            // Wrapping a single-item queue under All still flags the wrap so the
            // transition report is unchanged.
            Assert.True(d.Wrapped);
        }
    }

    [Fact]
    public void All_BackToBackSameInstance_IsReplay()
    {
        // The same source object queued twice in a row reuses the runtime across the
        // duplicate boundary (same demuxer/decoders/device) — a Replay, not a rebuild.
        var a = S("a");
        var coord = new PlaylistCoordinator([a, a], RepeatMode.All);

        Assert.Same(a, coord.First());

        var d = coord.DecideNext(a);
        Assert.Equal(PlaylistCoordinator.NextKind.Replay, d.Kind);
        Assert.Same(a, d.Source);
        Assert.False(d.Wrapped); // still consuming the initial queue, no wrap yet.
    }

    [Fact]
    public void All_DistinctButEqualSources_RebuildNotReplay()
    {
        // Reference identity — not value equality — gates reuse: only the exact same
        // source object is guaranteed to share an open demuxer + decoders + decode
        // device + warm presenter binding. Two distinct sources that happen to be
        // value-equal (same path) must take the safe rebuild (Advance) path.
        var a1 = S("same");
        var a2 = S("same");
        Assert.Equal(a1, a2); // value-equal (record equality)...
        Assert.NotSame(a1, a2); // ...but distinct instances.

        var coord = new PlaylistCoordinator([a1, a2], RepeatMode.All);
        Assert.Same(a1, coord.First());

        var d = coord.DecideNext(a1);
        Assert.Equal(PlaylistCoordinator.NextKind.Advance, d.Kind);
        Assert.Same(a2, d.Source);
    }

    [Fact]
    public void Enqueue_AppendsToTail()
    {
        var (a, b, c) = (S("a"), S("b"), S("c"));
        var coord = new PlaylistCoordinator([a], RepeatMode.Off);

        coord.Enqueue(b);
        coord.Enqueue(c);

        Assert.Same(a, coord.First());
        Assert.Same(b, coord.DecideNext(a).Source);
        Assert.Same(c, coord.DecideNext(b).Source);
        Assert.Equal(PlaylistCoordinator.NextKind.End, coord.DecideNext(c).Kind);
    }

    [Fact]
    public void Off_ContinuousRotation_NeverEndsWhileConsumerEnqueues()
    {
        // Models the signage pattern: enqueue the next item on each transition, so
        // the queue never empties under RepeatMode.Off.
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);

        var current = coord.First();
        for (var i = 0; i < 25; i++)
        {
            coord.Enqueue(S($"item-{i}"));
            var d = coord.DecideNext(current);
            Assert.Equal(PlaylistCoordinator.NextKind.Advance, d.Kind);
            current = d.Source!;
        }
    }

    [Fact]
    public void SetNext_JumpsAheadOfTheQueue()
    {
        var (a, b, jump) = (S("a"), S("b"), S("jump"));
        var coord = new PlaylistCoordinator([a, b], RepeatMode.Off);

        Assert.Same(a, coord.First());
        coord.SetNext(jump);

        // jump plays before b.
        Assert.Same(jump, coord.DecideNext(a).Source);
        Assert.Same(b, coord.DecideNext(jump).Source);
    }

    [Fact]
    public void SetNext_Null_IsNoOp()
    {
        var (a, b) = (S("a"), S("b"));
        var coord = new PlaylistCoordinator([a, b], RepeatMode.Off);

        Assert.Same(a, coord.First());
        coord.SetNext(null);
        Assert.Same(b, coord.DecideNext(a).Source);
    }

    [Fact]
    public void ReportCurrent_UpdatesStateAndFiresTransition()
    {
        var (a, b) = (S("a"), S("b"));
        var coord = new PlaylistCoordinator([a, b], RepeatMode.All);
        _ = coord.First();

        var seen = new List<PlaylistTransition>();
        using var sub = coord.SourceTransitioned.Subscribe(new Collector(seen.Add));

        coord.ReportCurrent(a, Info(3), wrapped: false);
        Assert.Same(a, coord.CurrentSource);
        Assert.Equal(TimeSpan.FromSeconds(3), coord.CurrentDuration);

        coord.ReportCurrent(b, Info(5), wrapped: true);
        Assert.Same(b, coord.CurrentSource);
        Assert.Equal(TimeSpan.FromSeconds(5), coord.CurrentDuration);

        Assert.Equal(2, seen.Count);
        Assert.Equal(0, seen[0].Index);
        Assert.Same(a, seen[0].Source);
        Assert.False(seen[0].Wrapped);
        Assert.Equal(1, seen[1].Index);
        Assert.Same(b, seen[1].Source);
        Assert.True(seen[1].Wrapped);
    }

    [Fact]
    public void RepeatMode_AllToOff_StopsLooping()
    {
        var (a, b) = (S("a"), S("b"));
        var coord = new PlaylistCoordinator([a, b], RepeatMode.All);
        Assert.Same(a, coord.First());
        Assert.Same(b, coord.DecideNext(a).Source);

        // Under All this would wrap to a; switching to Off ends it instead.
        coord.RepeatMode = RepeatMode.Off;
        Assert.Equal(PlaylistCoordinator.NextKind.End, coord.DecideNext(b).Kind);
    }

    [Fact]
    public void RequestSkip_InvokesAttachedHandler()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        var skips = 0;
        coord.AttachSkipHandler(() => Interlocked.Increment(ref skips));

        coord.RequestSkip();
        coord.RequestSkip();

        Assert.Equal(2, skips);
    }

    [Fact]
    public void RequestSkip_BeforeHandlerAttached_LatchesForNextPlay()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);

        coord.RequestSkip(); // no handler yet → latched
        Assert.True(coord.ConsumeSkipRequest());
        Assert.False(coord.ConsumeSkipRequest()); // consumed once
    }

    private sealed class Collector : IObserver<PlaylistTransition>
    {
        private readonly Action<PlaylistTransition> _onNext;

        public Collector(Action<PlaylistTransition> onNext) => _onNext = onNext;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(PlaylistTransition value) => _onNext(value);
    }
}
