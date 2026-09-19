namespace FrameFlow.Playback.Tests;

/// <summary>
/// The validation rows of <c>docs/adr/playlist-events-name-their-item.md</c> that live in the pure
/// core: which item a failure and a loop name, and which item a transition's reason is about.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PlaylistSessionProtocol.Step"/> is a total function of a state, a queue and one
/// input, so every assertion here reads a returned value. That matters for the rows that assert an
/// event does <i>not</i> fire — the first item, and a disposing session. A negative assertion made
/// after a wait also passes when the thing has simply not happened yet, and fails in the unsafe
/// direction on a loaded runner; asserting over the action list a step returned has no such window,
/// because the step is finished when the list exists.
/// </para>
/// <para>
/// The ordering and reference-identity halves of the contract are the controller's, not the
/// protocol's, and are covered in <c>PlaybackDispatchProtocolTests</c>.
/// </para>
/// </remarks>
public sealed class PlaylistItemEventsTests
{
    private const int Generation = 3;
    private const int Run = 5;

    private static readonly MediaInfo Info = new("test", TimeSpan.FromSeconds(3), [], []);
    private static readonly InvalidOperationException Boom = new("boom");

    // ── Which item a failure names ──────────────────────────────────────────

    [Fact]
    public void AFailedItem_NamesTheItemThatFailed_NotTheOneThatFollows()
    {
        // Row 1: a queue of three whose middle item faults. The report must name b, the item that
        // failed, and not c, which the same fold has already decided to take next.
        var (state, queue, items) = Playing("a", "b", "c", current: 1);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.Fault(Generation, Boom, TimeSpan.FromSeconds(1)));

        var failed = Assert.Single(step.Actions.OfType<PlaylistSessionAction.ReportItemFailed>());
        Assert.Same(items[1], failed.Item);
        Assert.Equal(PlaylistItemFailure.FaultedDuringPlayback, failed.What);
        Assert.Same(Boom, failed.Error);
    }

    [Fact]
    public void TwoItemsOfOneSource_AreToldApartByTheReport()
    {
        // Row 2: PlaylistItem compares by reference precisely so the same source added twice is two
        // items. A display name cannot separate them, which is why the report carries the item.
        var source = new FakeSource("same");
        var first = new PlaylistItem(source);
        var second = new PlaylistItem(source);
        var (state, queue) = Playing([first, second], current: 0);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.Fault(Generation, Boom, TimeSpan.FromSeconds(1)));

        var failed = Assert.Single(step.Actions.OfType<PlaylistSessionAction.ReportItemFailed>());
        Assert.Same(first, failed.Item);
        Assert.NotSame(second, failed.Item);
    }

    [Fact]
    public void AnItemThatCannotBeStarted_ReportsTheOtherFailure()
    {
        // Row 3: the session already separates the two, and before this it discarded the
        // distinction into the message text.
        var (state, queue, items) = Playing("a", "b", current: 0);

        var step = RunToIdle(
            state,
            queue,
            new PlaylistSessionInput.EndOfStream(Generation, Run),
            fail: a => a is PlaylistSessionAction.PlayItem
        );

        var failed = Assert.Single(step.Actions.OfType<PlaylistSessionAction.ReportItemFailed>());
        Assert.Same(items[1], failed.Item);
        Assert.Equal(PlaylistItemFailure.CouldNotStart, failed.What);
    }

    // ── Where the event does not fire ───────────────────────────────────────

    [Fact]
    public void TheFirstItemFailing_IsFatal_AndReportsNoItemFailure()
    {
        // Row 1a. The first item is treated as a single source's, so its failure fails the load
        // rather than being skipped. ItemFailed never fires for it, and a host learns of it from
        // the state instead. Row 1 deliberately uses a MIDDLE item; without this row the suite
        // would not notice that the first one takes a different path.
        var (state, queue, _) = NotStarted("a", "b");

        var step = RunToIdle(state, queue, new PlaylistSessionInput.Fault(Generation, Boom, TimeSpan.Zero));

        Assert.Empty(step.Actions.OfType<PlaylistSessionAction.ReportItemFailed>());
        Assert.Single(step.Actions.OfType<PlaylistSessionAction.ReportFatal>());
    }

    [Fact]
    public void WhileDisposing_AFailedItemReportsNothing()
    {
        // Row 1c. The step is handed a disposing context and returns its whole action list, so the
        // absence is read off a finished value rather than waited for.
        var (state, queue, _) = Playing("a", "b", current: 0);

        var (_, _, step) = PlaylistSessionProtocol.Step(
            state,
            queue,
            new PlaylistSessionInput.Fault(Generation, Boom, TimeSpan.FromSeconds(1)),
            new PlaylistStepContext(Disposing: true)
        );

        Assert.Empty(step.Actions.OfType<PlaylistSessionAction.ReportItemFailed>());
        Assert.Empty(step.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>());
    }

    [Fact]
    public void TheGiveUpFailure_IsReportedBeforeTheFatal()
    {
        // Row 1b. The last failure of a losing run still raises ItemFailed; the fatal follows it.
        // The record says "raises both once" with exceptions, and this is the one that is easy to
        // get backwards.
        var (state, queue, items) = Playing("a", "b", current: 0);
        // Bank failures up to the guard's ceiling so the one this test drives is the one that gives
        // up. Built through the queue's own operation rather than by writing the counter, so the
        // ceiling stays whatever PlaylistFailureGuard says it is.
        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            (queue, _) = queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.Fault(Generation, Boom, TimeSpan.FromSeconds(1)));

        var kinds = step
            .Actions.Where(a =>
                a is PlaylistSessionAction.ReportItemFailed or PlaylistSessionAction.ReportFatal
            )
            .Select(a => a.GetType())
            .ToArray();

        Assert.Equal(
            [typeof(PlaylistSessionAction.ReportItemFailed), typeof(PlaylistSessionAction.ReportFatal)],
            kinds
        );
        Assert.Same(items[0], step.Actions.OfType<PlaylistSessionAction.ReportItemFailed>().Single().Item);
    }

    // ── Which item a transition's reason is about ───────────────────────────

    [Fact]
    public void AfterAFailedItem_TheTransitionNamesTheFailedOneAsPrevious()
    {
        // Row 9, and the defect the independent review of the record found: every other field of a
        // transition describes the item that STARTED, so a reason describing the item that ENDED
        // misattributes without Previous. On [a, b] where a fails, the transition names b and must
        // name a as Previous.
        var (state, queue, items) = Playing("a", "b", current: 0);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.Fault(Generation, Boom, TimeSpan.FromSeconds(1)));

        var transition = Assert.Single(step.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(items[1], transition.Item);
        Assert.Same(items[0], transition.Previous);
        Assert.Equal(PlaylistTransitionReason.ItemFailed, transition.Reason);
    }

    [Fact]
    public void AnItemThatPlayedToItsEnd_ReportsTheEndOfItemReason()
    {
        var (state, queue, items) = Playing("a", "b", current: 0);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.EndOfStream(Generation, Run));

        var transition = Assert.Single(step.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(items[1], transition.Item);
        Assert.Same(items[0], transition.Previous);
        Assert.Equal(PlaylistTransitionReason.EndOfItem, transition.Reason);
    }

    [Fact]
    public void ASkip_ReportsTheSkippedReason()
    {
        var (state, queue, items) = Playing("a", "b", current: 0);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.SkipRequested(Generation, PlaylistRunState.Playing));

        var transition = Assert.Single(step.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(items[0], transition.Previous);
        Assert.Equal(PlaylistTransitionReason.Skipped, transition.Reason);
    }

    [Fact]
    public void ALoop_ReportsTheLoopReason_WithPreviousEqualToTheItemThatStarted()
    {
        // Row 11. Under One the item is taken again, so Previous and Item are the same object, and
        // a consumer counting completed passes reads Loop rather than EndOfItem.
        var a = new PlaylistItem(new FakeSource("a"));
        var (state, queue) = Playing([a], current: 0, RepeatMode.One);

        var step = RunToIdle(state, queue, new PlaylistSessionInput.EndOfStream(Generation, Run));

        var transition = Assert.Single(step.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(a, transition.Item);
        Assert.Same(a, transition.Previous);
        Assert.Equal(PlaylistTransitionReason.Loop, transition.Reason);

        var loop = Assert.Single(step.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>());
        Assert.Same(a, loop.Item);
        Assert.Equal(1, loop.LoopCount);
    }

    [Fact]
    public void TheFirstItemsTransition_HasNoPrevious()
    {
        // Row 10. Previous is nullable for this one case, and a consumer attributing a reason has
        // to handle it.
        var a = new PlaylistItem(new FakeSource("a"));
        var queue = PlaylistQueue.Create([a], RepeatMode.Off);
        var state = PlaylistSessionState.Initial with { Generation = Generation };

        var step = RunToIdle(state, queue, new PlaylistSessionInput.Initialize(Command: 1));

        var transition = Assert.Single(step.Actions.OfType<PlaylistSessionAction.RaiseTransition>());
        Assert.Same(a, transition.Item);
        Assert.Null(transition.Previous);
        Assert.Equal(PlaylistTransitionReason.FirstItem, transition.Reason);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static (PlaylistSessionState State, PlaylistQueue Queue, PlaylistItem[] Items) Playing(
        string a,
        string b,
        int current
    ) => Build([a, b], current, RepeatMode.Off, PlaylistRunState.Playing, played: true);

    private static (PlaylistSessionState State, PlaylistQueue Queue, PlaylistItem[] Items) Playing(
        string a,
        string b,
        string c,
        int current
    ) => Build([a, b, c], current, RepeatMode.Off, PlaylistRunState.Playing, played: true);

    private static (PlaylistSessionState State, PlaylistQueue Queue, PlaylistItem[] Items) NotStarted(
        string a,
        string b
    ) => Build([a, b], 0, RepeatMode.Off, PlaylistRunState.NotStarted, played: false);

    private static (PlaylistSessionState State, PlaylistQueue Queue) Playing(
        PlaylistItem[] items,
        int current,
        RepeatMode repeat = RepeatMode.Off
    )
    {
        var (state, queue, _) = From(items, current, repeat, PlaylistRunState.Playing, played: true);
        return (state, queue);
    }

    private static (PlaylistSessionState State, PlaylistQueue Queue, PlaylistItem[] Items) Build(
        string[] names,
        int current,
        RepeatMode repeat,
        PlaylistRunState run,
        bool played
    ) => From([.. names.Select(n => new PlaylistItem(new FakeSource(n)))], current, repeat, run, played);

    /// <summary>
    /// A queue whose cursor has reached <paramref name="current"/>, with that item started and the
    /// state's slot pointing at it. Everything before it has already played.
    /// </summary>
    private static (PlaylistSessionState State, PlaylistQueue Queue, PlaylistItem[] Items) From(
        PlaylistItem[] items,
        int current,
        RepeatMode repeat,
        PlaylistRunState run,
        bool played
    )
    {
        var queue = PlaylistQueue.Create(items, repeat);
        (queue, _) = queue.TakeStart();
        for (var i = 0; i < current; i++)
        {
            queue = queue.ItemEnded();
            (queue, _) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        }
        (queue, _) = queue.ReportCurrent(items[current], Info);

        var slot = new PlaylistItemSlot(
            Generation,
            items[current],
            played,
            AwaitsPlay: false,
            Run,
            Info
        );

        return (
            PlaylistSessionState.Initial with
            {
                Run = run,
                Item = slot,
                Generation = Generation,
            },
            queue,
            items
        );
    }

    private static Drive RunToIdle(
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
                var run = s.Item?.KnownRun ?? 0;
                PlaylistSessionInput.Outcome outcome =
                    fail?.Invoke(call) == true
                        ? new(PlaylistOutcome.Failed, run, Error: Boom)
                        : call switch
                        {
                            PlaylistSessionAction.OpenItem => new(PlaylistOutcome.Ok, 0, Info),
                            PlaylistSessionAction.RewindItem or PlaylistSessionAction.SeekItem =>
                                new(PlaylistOutcome.Ok, run + 1),
                            _ => new(PlaylistOutcome.Ok, run),
                        };
                (s, q, step) = PlaylistSessionProtocol.Step(s, q, outcome, default);
            }
            else if (!step.Done)
            {
                (s, q, step) = PlaylistSessionProtocol.Step(
                    s,
                    q,
                    new PlaylistSessionInput.Continue(),
                    default
                );
            }
            else
            {
                return new Drive(actions);
            }
        }

        throw new InvalidOperationException("The input was not handled within 50 steps.");
    }

    private sealed record Drive(List<PlaylistSessionAction> Actions);

    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }
}
