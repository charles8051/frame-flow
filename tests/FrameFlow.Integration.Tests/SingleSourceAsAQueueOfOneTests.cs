using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A single source runs as a queue of one on the playlist session: the spike the end-of-queue
/// record's <i>Deferred: one player type</i> asks for, and rows 6 and 8 of
/// <c>docs/adr/looping-on-both-players.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every run here is built by <see cref="PlaybackController.Create"/>, the public single-source
/// entry point. The queue it plays holds the loaded source and nothing else.
/// </para>
/// <para>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6). Each
/// wait completes on a signal; the bound only stops a failing run.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SingleSourceAsAQueueOfOneTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string ShortClip = "test-subsecond.mp4";
    private const string LongClip = "test-video-h264-yuv420p.mp4";

    // The session gives up after this many failures in a row without progress.
    private const int FailuresBeforeGivingUp = 9;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public SingleSourceAsAQueueOfOneTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AMidStreamFault_IsReported_AndThePlayerEnds()
    {
        // The end-of-queue record's decision 7: a fault that reaches the end of the queue is
        // reported and ends the player, rather than being swallowed. Before this change a single
        // source went to Error instead, with the fault as its terminal error.
        var faults = new FaultInjector(breaks: _ => true);
        await using var run = Run(LongClip, RepeatMode.Off, faults);

        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        var error = Assert.Single(run.Errors);
        Assert.True(InjectedFault.Caused(error), $"Unexpected error: {error}");
        Assert.Equal(PlaybackState.Ended, run.Controller.State);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AFaultOnEveryPass_IsReportedEachPass_ThenThePlayerGivesUp()
    {
        // Under One the item is rebuilt after each fault, so a source that always faults is a run
        // of failures, and the failure guard ends it in Error.
        var faults = new FaultInjector(breaks: _ => true);
        await using var run = Run(LongClip, RepeatMode.One, faults);

        await run.PlayAsync();
        // The controller projects Error before it raises the error that put it there.
        await run.GaveUp.WaitAsync(Bound);

        Assert.Equal(PlaybackState.Error, run.Controller.State);
        Assert.Equal(FailuresBeforeGivingUp + 1, run.Errors.Count);
        Assert.All(run.Errors, e => Assert.True(InjectedFault.Caused(e), $"Unexpected error: {e}"));
        Assert.Contains("gave up", run.Errors[^1].Message);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task UnderAll_ItLoops_AndReportsEachLoop()
    {
        // Row 6. A single source under All used to play one pass and end.
        await using var run = Run(ShortClip, RepeatMode.All);

        await run.PlayAsync();
        await run.WhenLoops(2).WaitAsync(Bound);

        Assert.Equal([1, 2], run.Loops.Take(2));
        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task UnderOne_ItLoopsInPlace_AndBuildsItsVideoChainOnce()
    {
        // Row 8. The controller's loop was a full seek, which rebuilt the graph on every pass, so
        // the configurator ran once per loop. The session rewinds the item in place instead.
        var chains = new FaultInjector(breaks: _ => false);
        await using var run = Run(ShortClip, RepeatMode.One, chains);

        await run.PlayAsync();
        await run.WhenLoops(2).WaitAsync(Bound);

        Assert.Equal([1, 2], run.Loops.Take(2));
        Assert.Equal(1, chains.Chains);
        Assert.Equal(PlaybackState.Playing, run.Controller.State);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AStatefulOperator_IsToldWhenTheLoopStartsItsItemOver()
    {
        // #217. The loop keeps the graph, so an operator's state outlives the rewind while its
        // timestamps go back to zero. The graph resets the node before each run, so an operator
        // that remembers the last timestamp it saw starts each pass over.
        var timeline = new TimestampWatcher();
        await using var run = PlaylistRun.Create(
            [SourceOf(ShortClip)],
            RepeatMode.One,
            configureVideo: timeline.Configure,
            asSingleSource: true
        );

        await run.PlayAsync();
        await run.WhenLoops(2).WaitAsync(Bound);

        // One chain, so one operator instance across every pass.
        Assert.Equal(1, timeline.Chains);
        Assert.True(timeline.Resets >= 2, $"The operator was reset {timeline.Resets} times.");
        Assert.Equal(0, timeline.BackwardStepsWithoutAReset);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnItemEnqueuedAtEnded_PlaysWhenThePlayerReplays()
    {
        // The load makes the source the queue's only item, and a replay from Ended reloads that
        // same source. It must keep the queue: a load that rebuilt it would drop the enqueued item
        // and play the source again. This is the queue a single player type hands the caller.
        var enqueued = SourceOf(ShortClip);
        await using var run = Run(ShortClip, RepeatMode.Off);

        var ended = run.Settled(PlaybackState.Ended);
        await run.PlayAsync();
        await ended.WaitAsync(Bound);

        var enqueuedIsCurrent = run.Transitioned(enqueued);
        run.Coordinator.Enqueue(enqueued);
        var framesAtEnded = run.Sink.Presented;

        var play = await run.Controller.PlayAsync();

        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        await enqueuedIsCurrent.WaitAsync(Bound);
        await run.Sink.WhenPresented(framesAtEnded + 1).WaitAsync(Bound);
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AReload_ReplacesTheQueue_SoNothingEnqueuedBeforeItSurvives()
    {
        // Every load but a replay makes the loaded source the queue's only item. A reload that
        // kept the queue would play an item enqueued before it, which is not what the caller
        // asked for.
        var source = SourceOf(ShortClip);
        await using var run = PlaylistRun.Create([source], RepeatMode.Off, asSingleSource: true);
        await run.LoadAsync();

        run.Coordinator.Enqueue(SourceOf(LongClip));
        Assert.True((await run.Controller.UnloadAsync()).IsSuccess);
        await run.LoadAsync();

        // The load takes the item it will start with, so the current item says what the reload
        // plays. A kept queue would have taken the enqueued item instead.
        var snapshot = run.Coordinator.Snapshot();
        Assert.Same(source, snapshot.Current?.Source);
        Assert.Empty(snapshot.Queued);
        var only = Assert.Single(snapshot.Playlist);
        Assert.Same(source, only.Source);
    }

    private static IMediaSource SourceOf(string clip)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }

    /// <summary>
    /// A video operator that watches the timestamps it is handed, and counts a step backwards that
    /// no reset preceded. A loop hands it a timestamp before the one it saw last, so without the
    /// reset the count rises on the first frame of the second pass.
    /// </summary>
    private sealed class TimestampWatcher
    {
        private int _chains;
        private int _resets;
        private int _backwardSteps;
        private TimeSpan _last = TimeSpan.MinValue;
        private bool _reset = true;

        public int Chains => Volatile.Read(ref _chains);

        public int Resets => Volatile.Read(ref _resets);

        public int BackwardStepsWithoutAReset => Volatile.Read(ref _backwardSteps);

        public GraphChain<VideoFrameRef> Configure(GraphChain<VideoFrameRef> chain)
        {
            Interlocked.Increment(ref _chains);
            return chain.Then(
                new OperatorNode<VideoFrameRef, VideoFrameRef>(
                    "watch-timestamps",
                    (frame, _) =>
                    {
                        // The pump is single-threaded, and a reset runs before it starts.
                        if (frame.Frame.Pts < _last && !_reset)
                            Interlocked.Increment(ref _backwardSteps);
                        _reset = false;
                        _last = frame.Frame.Pts;
                        return ValueTask.FromResult<VideoFrameRef?>(frame);
                    }
                )
                {
                    OnReset = () =>
                    {
                        Interlocked.Increment(ref _resets);
                        _last = TimeSpan.MinValue;
                        _reset = true;
                    },
                }
            );
        }
    }

    private static PlaylistRun Run(string clip, RepeatMode repeat, FaultInjector? faults = null)
    {
        return PlaylistRun.Create(
            [SourceOf(clip)],
            repeat,
            configureVideo: faults is null ? null : faults.Configure,
            asSingleSource: true
        );
    }
}
