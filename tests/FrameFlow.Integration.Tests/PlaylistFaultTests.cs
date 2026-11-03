using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Faults inside playlist items, over real corpus media through
/// <see cref="PlaybackController.CreatePlaylist"/> (#180). An item that fails is reported on
/// <see cref="IPlaybackController.ErrorOccurred"/> and the playlist moves on. The player ends in
/// <see cref="PlaybackState.Ended"/> when items keep failing (#810).
/// </summary>
/// <remarks>
/// <para>
/// Faults are injected by a video operator that throws on the 21st frame of the chains it is
/// told to break. An item gets a new chain each time it is built, and a faulted item is always
/// rebuilt rather than rewound, so breaking every chain breaks every pass.
/// </para>
/// <para>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6).
/// Each wait completes on a signal; the bound only stops a failing run.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaylistFaultTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    // The queue ends on the failure after this many in a row.
    private const int FailuresBeforeGivingUp = 9;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public PlaylistFaultTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultOnTheLastItem_IsReported_AndThePlaylistEnds(bool asSingleSource)
    {
        var faults = new FaultInjector(breaks: _ => true);
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, faults.Configure, asSingleSource: asSingleSource);

        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        Assert.Equal(PlaybackState.Ended, run.Controller.State);
        var error = Assert.Single(run.Errors);
        Assert.True(InjectedFault.Caused(error), $"Unexpected error: {error}");
        // The graph's fault names the node that threw and carries what it threw (#499).
        Assert.Contains(
            "Node 'inject-fault' faulted: Injected fault in chain 0.",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekFromEnded_AfterTheLastItemFaulted_IsRefused_AndPlayStartsThePlaylistAgain(bool asSingleSource)
    {
        // A faulted item is not kept at the end of the queue, so Ended holds nothing to seek.
        // The seek used to succeed, and the play after it reported Playing with nothing current
        // (#170). The playlist keeps its items, so Play starts it again (#171); here the item
        // faults again, is reported again, and the playlist ends again.
        var faults = new FaultInjector(breaks: _ => true);
        var item = ClipSource();
        await using var run = PlaylistRun.Create([item], RepeatMode.Off, faults.Configure, asSingleSource: asSingleSource);
        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        var seek = await run.Controller.SeekAsync(TimeSpan.Zero);
        Assert.False(seek.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, seek.Error!.Category);
        Assert.Equal(PlaybackState.Ended, run.Controller.State);

        var itemAgain = run.Transitioned(item);
        var play = await run.Controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        await itemAgain.WaitAsync(Bound);
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        Assert.Equal(2, run.Errors.Count);
        Assert.All(run.Errors, e => Assert.True(InjectedFault.Caused(e), $"Unexpected error: {e}"));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task FaultBeforeAnotherItem_IsReported_AndTheNextItemPlays()
    {
        // Only the first chain breaks, so the second item plays to its end.
        var faults = new FaultInjector(breaks: chain => chain == 0);
        var first = ClipSource();
        var second = ClipSource();
        await using var run = PlaylistRun.Create([first, second], RepeatMode.Off, faults.Configure);

        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        Assert.Equal(PlaybackState.Ended, run.Controller.State);
        var error = Assert.Single(run.Errors);
        Assert.True(InjectedFault.Caused(error), $"Unexpected error: {error}");
        Assert.Equal([first, second], run.Transitions.Select(t => t.Item.Source));
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(RepeatMode.All, false)]
    [InlineData(RepeatMode.One, false)]
    [InlineData(RepeatMode.All, true)]
    [InlineData(RepeatMode.One, true)]
    public async Task ItemThatFaultsOnEveryPass_IsReportedEachTime_ThenThePlaylistEnds(
        RepeatMode repeat,
        bool asSingleSource
    )
    {
        var faults = new FaultInjector(breaks: _ => true);
        await using var run = PlaylistRun.Create(
            [ClipSource()],
            repeat,
            faults.Configure,
            asSingleSource: asSingleSource
        );

        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        var errors = run.Errors;
        Assert.Equal(FailuresBeforeGivingUp, errors.Count);
        Assert.All(errors, e => Assert.True(InjectedFault.Caused(e), $"Unexpected error: {e}"));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AReplaceFromTheHandlerOfTheFailureThatEndsTheQueue_PlaysTheNewPlaylist()
    {
        // #809. The session has ended the queue by the time the handler runs. The first chain is the
        // item that faults; the replacement's is left alone.
        var replacement = ClipSource();
        var faults = new FaultInjector(breaks: chain => chain == 0);
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, faults.Configure);

        var replaced = run.Transitioned(replacement);
        using var replace = run.Controller.ErrorOccurred.Subscribe(
            new ActionObserver<PlaybackError>(_ => run.Coordinator.Replace([replacement]))
        );

        await run.PlayAsync();
        await replaced.WaitAsync(Bound);
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        Assert.True(InjectedFault.Caused(Assert.Single(run.Errors)));
        Assert.Same(replacement, run.Transitions[^1].Item.Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SkippingAnItemThatPlays_ResetsTheCount()
    {
        // A rotation driven by skips never lets an item reach its end. The item between two
        // faults started and was skipped without failing, so the faults are not in a row, and
        // the rotation must survive more of them than the player tolerates in a row.
        var failing = ClipSource();
        var skipped = ClipSource();
        // Items alternate, and each is built once per pass, so even chains are the failing item.
        var faults = new FaultInjector(breaks: chain => chain % 2 == 0);
        await using var run = PlaylistRun.Create(
            [failing, skipped],
            RepeatMode.All,
            faults.Configure
        );

        var target = FailuresBeforeGivingUp + 3;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var skip = run.Coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(t =>
            {
                if (ReferenceEquals(t.Item.Source, skipped))
                    run.Coordinator.RequestSkip();
            })
        );
        // Counted here rather than read from run.Errors: the subject does not promise to call
        // its observers in the order they subscribed.
        var reported = 0;
        using var count = run.Controller.ErrorOccurred.Subscribe(
            new ActionObserver<PlaybackError>(_ =>
            {
                if (Interlocked.Increment(ref reported) >= target)
                    enough.TrySetResult();
            })
        );

        await run.PlayAsync();
        // Either enough faults were reported, or the queue ended first.
        await Task.WhenAny(enough.Task, run.Settled(PlaybackState.Ended)).WaitAsync(Bound);

        Assert.NotEqual(PlaybackState.Ended, run.Controller.State);
        Assert.True(run.Errors.Count >= target, $"Only {run.Errors.Count} faults were reported.");
        Assert.All(run.Errors, e => Assert.True(InjectedFault.Caused(e), $"Unexpected error: {e}"));
    }

    private static IMediaSource ClipSource()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
