namespace FrameFlow.Playback.Tests;

/// <summary>
/// The events that start an item's timeline name it (ADR-0084, decision 7; #756). Pure steps of
/// <see cref="PlaylistSessionProtocol"/>, driven by a model of the runtimes' runs. No shell.
/// </summary>
/// <remarks>
/// The model follows <see cref="SubstrateSession"/>: a runtime's run number advances at a seek or a
/// rewind, a warm-up or a play starts the current run without advancing it, and each run number of
/// each runtime has one timeline, which every outcome reports with the run.
/// </remarks>
public sealed class PlaylistTransitionTimelineTests
{
    private const int Generation = 1;
    private const int Command = 7;

    private static readonly MediaInfo Info = new("test", TimeSpan.FromSeconds(3), [], []);

    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    /// <summary>The runtimes the session opens, and the timeline of each of their runs.</summary>
    private sealed class Runtimes
    {
        private readonly Dictionary<(int Runtime, int Run), TimelineId> _timelines = [];
        private int _opened;

        /// <summary>The runtime the session holds. The first is runtime 0.</summary>
        public int Current { get; private set; }

        /// <summary>The run number of the runtime the session holds.</summary>
        public int Run { get; private set; }

        public TimelineId TimelineOf(int runtime, int run)
        {
            if (!_timelines.TryGetValue((runtime, run), out var timeline))
                _timelines[(runtime, run)] = timeline = TimelineId.Next();
            return timeline;
        }

        public TimelineId Timeline => TimelineOf(Current, Run);

        public PlaylistSessionInput.Outcome Perform(PlaylistSessionAction call)
        {
            switch (call)
            {
                case PlaylistSessionAction.OpenItem:
                    Current = ++_opened;
                    Run = 0;
                    return new(PlaylistOutcome.Ok, Run, Info, Timeline: Timeline);
                case PlaylistSessionAction.RewindItem or PlaylistSessionAction.SeekItem:
                    Run++;
                    return new(PlaylistOutcome.Ok, Run, Timeline: Timeline);
                case PlaylistSessionAction.DisposeItem:
                    return new(PlaylistOutcome.Ok);
                default:
                    return new(PlaylistOutcome.Ok, Run, Timeline: Timeline);
            }
        }
    }

    private sealed record Drive(PlaylistSessionState State, PlaylistQueue Queue, List<PlaylistSessionAction> Actions);

    [Fact]
    public void TheFirstItem_NamesTheTimelineItsOpenReported()
    {
        var a = new PlaylistItem(new FakeSource("a"));
        var runtimes = new Runtimes();

        var drive = RunToIdle(runtimes, PlaylistSessionState.Initial, PlaylistQueue.Create([a], RepeatMode.Off), new PlaylistSessionInput.Initialize(Command));

        var transition = Assert.Single(drive.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Equal(PlaylistTransitionReason.FirstItem, transition.Reason);
        Assert.Equal(runtimes.TimelineOf(1, 0), transition.Timeline);
        Assert.NotEqual(TimelineId.None, transition.Timeline);
    }

    [Fact]
    public void TheNextItem_NamesItsOwnRunsTimeline_NotTheEndedItems()
    {
        var (runtimes, state, queue, b) = PlayingFirstOf(RepeatMode.Off, "a", "b");

        var drive = RunToIdle(runtimes, state, queue, new PlaylistSessionInput.EndOfStream(Generation, 0));

        var transition = Assert.Single(drive.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(b, transition.Item);
        Assert.Equal(runtimes.TimelineOf(1, 0), transition.Timeline);
        Assert.NotEqual(runtimes.TimelineOf(0, 0), transition.Timeline);
    }

    [Fact]
    public void ALoop_NamesTheTimelineItsRewindStarted_OnTheTransitionAndTheLoopReport()
    {
        var (runtimes, state, queue, _) = PlayingFirstOf(RepeatMode.One, "a");

        var drive = RunToIdle(runtimes, state, queue, new PlaylistSessionInput.EndOfStream(Generation, 0));

        var transition = Assert.Single(drive.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        var loop = Assert.Single(drive.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>());
        Assert.Equal(PlaylistTransitionReason.Loop, transition.Reason);
        Assert.Equal(runtimes.TimelineOf(0, 1), transition.Timeline);
        Assert.Equal(runtimes.TimelineOf(0, 1), loop.Timeline);
        Assert.NotEqual(runtimes.TimelineOf(0, 0), loop.Timeline);
    }

    [Fact]
    public void ALoopWhilePaused_NamesTheTimelineOfTheRunThePlayStarts()
    {
        // The rewind holds the paused item, so its run has a number and a timeline and has not
        // started. The Play starts that run, so its first frame carries the timeline the loop named.
        var (runtimes, playing, queue, _) = PlayingFirstOf(RepeatMode.One, "a");
        var paused = playing with { Run = PlaylistRunState.Paused };

        var looped = RunToIdle(runtimes, paused, queue, new PlaylistSessionInput.EndOfStream(Generation, 0));
        var loop = Assert.Single(looped.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>());
        Assert.Equal(runtimes.TimelineOf(0, 1), loop.Timeline);

        var played = RunToIdle(runtimes, looped.State, looped.Queue, new PlaylistSessionInput.Play(Command));
        Assert.Equal(1, runtimes.Run);
        Assert.Equal(loop.Timeline, played.State.Item!.KnownTimeline);
    }

    [Fact]
    public void ALoopThatRebuilds_NamesTheRebuiltRuntimesTimeline()
    {
        var (runtimes, state, queue, _) = PlayingFirstOf(RepeatMode.One, "a");

        var drive = RunToIdle(
            runtimes,
            state,
            queue,
            new PlaylistSessionInput.EndOfStream(Generation, 0),
            fail: a => a is PlaylistSessionAction.RewindItem
        );

        var transition = Assert.Single(drive.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        var loop = Assert.Single(drive.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>());
        Assert.Equal(runtimes.TimelineOf(1, 0), transition.Timeline);
        Assert.Equal(transition.Timeline, loop.Timeline);
    }

    /// <summary>
    /// The race in #756. Under <see cref="RepeatMode.One"/> the item loops just before a replace.
    /// Both start a timeline, so "the first frame of a new timeline" picks the loop's frame. The
    /// replacement's transition names its own timeline, and only its frames carry it.
    /// </summary>
    [Fact]
    public void ALoopJustBeforeAReplace_NamesTwoTimelines_SoTheCallerCanTellTheirFramesApart()
    {
        var (runtimes, state, queue, _) = PlayingFirstOf(RepeatMode.One, "a");
        var b = new PlaylistItem(new FakeSource("b"));

        var looped = RunToIdle(runtimes, state, queue, new PlaylistSessionInput.EndOfStream(Generation, 0));
        var loop = Assert.Single(looped.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>());
        var replaced = RunToIdle(runtimes, looped.State, looped.Queue.Replace([b]), new PlaylistSessionInput.JumpRequested());
        var transition = Assert.Single(replaced.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(b, transition.Item);
        Assert.NotEqual(loop.Timeline, transition.Timeline);

        // What a caller sees presented, in order: a before the loop, a after it, then b. Each frame
        // carries its run's timeline.
        TimelineId[] presented = [runtimes.TimelineOf(0, 0), runtimes.TimelineOf(0, 1), runtimes.TimelineOf(1, 0)];

        // The rule the issue reports: the first frame on a timeline other than the one on screen
        // when the replace was asked for. It picks a's loop.
        var firstNew = Array.FindIndex(presented, t => t != presented[0]);
        Assert.Equal(1, firstNew);

        // The rule the transition allows: the first frame on the timeline it names. It picks b.
        var firstOfB = Array.IndexOf(presented, transition.Timeline);
        Assert.Equal(2, firstOfB);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A session playing the first of <paramref name="sources"/> on runtime 0, run 0, which has
    /// played. Returns the second item, if there is one.
    /// </summary>
    private static (Runtimes, PlaylistSessionState, PlaylistQueue, PlaylistItem?) PlayingFirstOf(
        RepeatMode repeat,
        params string[] sources
    )
    {
        var items = sources.Select(s => new PlaylistItem(new FakeSource(s))).ToArray();
        var queue = PlaylistQueue.Create(items, repeat);
        (queue, _) = queue.TakeStart();
        (queue, _) = queue.ReportCurrent(items[0], Info);
        var runtimes = new Runtimes();
        var state = PlaylistSessionState.Initial with
        {
            Run = PlaylistRunState.Playing,
            Generation = Generation,
            Item = new PlaylistItemSlot(Generation, items[0], Played: true, AwaitsPlay: false, 0, Info, runtimes.Timeline),
        };
        return (runtimes, state, queue, items.Length > 1 ? items[1] : null);
    }

    private static Drive RunToIdle(
        Runtimes runtimes,
        PlaylistSessionState state,
        PlaylistQueue queue,
        PlaylistSessionInput input,
        Func<PlaylistSessionAction, bool>? fail = null
    )
    {
        var actions = new List<PlaylistSessionAction>();
        var (s, q, step) = PlaylistSessionProtocol.Step(state, queue, input, default);
        for (var guard = 0; guard < 50; guard++)
        {
            actions.AddRange(step.Actions);
            if (step.Awaited is { } call)
            {
                var outcome = fail?.Invoke(call) == true
                    ? new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, runtimes.Run, Error: new InvalidOperationException("boom"), Timeline: runtimes.Timeline)
                    : runtimes.Perform(call);
                (s, q, step) = PlaylistSessionProtocol.Step(s, q, outcome, default);
            }
            else if (!step.Done)
            {
                (s, q, step) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Continue(), default);
            }
            else
            {
                return new Drive(s, q, actions);
            }
        }
        throw new InvalidOperationException("The input was not handled within 50 steps.");
    }
}
