using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Faults inside playlist items, over real corpus media through
/// <see cref="PlaybackController.CreatePlaylist"/> (#180). An item that fails is reported on
/// <see cref="IPlaybackController.ErrorOccurred"/> and the playlist moves on. The player enters
/// <see cref="PlaybackState.Error"/> only when items keep failing.
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
    private const int FaultFrame = 21;

    // PlaylistSession gives up on the failure after this many in a row.
    private const int FailuresBeforeGivingUp = 9;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public PlaylistFaultTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task FaultOnTheLastItem_IsReported_AndThePlaylistEnds()
    {
        var faults = new FaultInjector(breaks: _ => true);
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, faults);

        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        Assert.Equal(PlaybackState.Ended, run.Controller.State);
        var error = Assert.Single(run.Errors);
        Assert.True(InjectedFault.Caused(error), $"Unexpected error: {error}");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task FaultBeforeAnotherItem_IsReported_AndTheNextItemPlays()
    {
        // Only the first chain breaks, so the second item plays to its end.
        var faults = new FaultInjector(breaks: chain => chain == 0);
        var first = ClipSource();
        var second = ClipSource();
        await using var run = PlaylistRun.Create([first, second], RepeatMode.Off, faults);

        await run.PlayAsync();
        await run.Settled(PlaybackState.Ended).WaitAsync(Bound);

        Assert.Equal(PlaybackState.Ended, run.Controller.State);
        var error = Assert.Single(run.Errors);
        Assert.True(InjectedFault.Caused(error), $"Unexpected error: {error}");
        Assert.Equal([first, second], run.Transitions.Select(t => t.Source));
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public async Task ItemThatFaultsOnEveryPass_IsReportedEachTime_ThenPutsThePlayerInError(
        RepeatMode repeat
    )
    {
        var faults = new FaultInjector(breaks: _ => true);
        await using var run = PlaylistRun.Create([ClipSource()], repeat, faults);

        await run.PlayAsync();
        // Wait for the give-up error, not the state: the controller projects Error before it
        // raises the error that put it there.
        await run.GaveUp.WaitAsync(Bound);

        Assert.Equal(PlaybackState.Error, run.Controller.State);
        var errors = run.Errors;
        Assert.Equal(FailuresBeforeGivingUp + 1, errors.Count);
        Assert.All(errors, e => Assert.True(InjectedFault.Caused(e), $"Unexpected error: {e}"));
        Assert.Contains("gave up", errors[^1].Message);
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
        await using var run = PlaylistRun.Create([failing, skipped], RepeatMode.All, faults);

        var target = FailuresBeforeGivingUp + 3;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var skip = run.Coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(t =>
            {
                if (ReferenceEquals(t.Source, skipped))
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
        // Either enough faults were reported, or the player gave up first.
        await Task.WhenAny(enough.Task, run.Settled(PlaybackState.Error)).WaitAsync(Bound);

        Assert.NotEqual(PlaybackState.Error, run.Controller.State);
        Assert.True(run.Errors.Count >= target, $"Only {run.Errors.Count} faults were reported.");
        Assert.All(run.Errors, e => Assert.True(InjectedFault.Caused(e), $"Unexpected error: {e}"));
    }

    private static IMediaSource ClipSource()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }

    /// <summary>
    /// A playlist controller over a video sink that discards frames, with no audio sink,
    /// recording its errors, transitions and public states.
    /// </summary>
    private sealed class PlaylistRun : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<PlaybackError> _errors = [];
        private readonly List<PlaylistTransition> _transitions = [];
        private readonly List<(PlaybackState State, TaskCompletionSource Signal)> _waiters = [];
        private readonly TaskCompletionSource _gaveUp = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly IDisposable[] _subscriptions;

        private PlaylistRun(IPlaybackController controller, PlaylistCoordinator coordinator)
        {
            Controller = controller;
            Coordinator = coordinator;
            _subscriptions =
            [
                controller.ErrorOccurred.Subscribe(
                    new ActionObserver<PlaybackError>(e =>
                    {
                        lock (_gate)
                            _errors.Add(e);
                        if (e.Message.Contains("gave up", StringComparison.Ordinal))
                            _gaveUp.TrySetResult();
                    })
                ),
                coordinator.SourceTransitioned.Subscribe(
                    new ActionObserver<PlaylistTransition>(t =>
                    {
                        lock (_gate)
                            _transitions.Add(t);
                    })
                ),
                controller.PlaybackStateChanged.Subscribe(
                    new ActionObserver<StateTransition<PlaybackState>>(t =>
                    {
                        lock (_gate)
                        {
                            foreach (var (state, signal) in _waiters)
                            {
                                if (state == t.Current)
                                    signal.TrySetResult();
                            }
                        }
                    })
                ),
            ];
        }

        public IPlaybackController Controller { get; }

        public PlaylistCoordinator Coordinator { get; }

        /// <summary>Completes when the playlist's give-up error is raised.</summary>
        public Task GaveUp => _gaveUp.Task;

        public IReadOnlyList<PlaybackError> Errors
        {
            get
            {
                lock (_gate)
                    return _errors.ToArray();
            }
        }

        public IReadOnlyList<PlaylistTransition> Transitions
        {
            get
            {
                lock (_gate)
                    return _transitions.ToArray();
            }
        }

        public static PlaylistRun Create(
            IMediaSource[] items,
            RepeatMode repeat,
            FaultInjector faults
        )
        {
            var coordinator = new PlaylistCoordinator(items, repeat);
            var controller = PlaybackController.CreatePlaylist(
                coordinator,
                videoSink: new DiscardingVideoSink(),
                audioSink: null,
                hardwareDecodeMode: HardwareDecodeMode.Disabled,
                initialRepeatMode: repeat,
                configureVideo: faults.Configure
            );
            return new PlaylistRun(controller, coordinator);
        }

        /// <summary>Loads the first item and plays it.</summary>
        public async Task PlayAsync()
        {
            // The session takes its first item from the coordinator's queue, not from the
            // source the controller is handed, so any source satisfies the controller.
            var load = await Controller.LoadAsync(ClipSource());
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
            var play = await Controller.PlayAsync();
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        }

        /// <summary>
        /// Completes when the player enters <paramref name="state"/>, or at once if it is
        /// already there.
        /// </summary>
        public Task Settled(PlaybackState state)
        {
            var signal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            lock (_gate)
                _waiters.Add((state, signal));
            if (Controller.State == state)
                signal.TrySetResult();
            return signal.Task;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            await Controller.DisposeAsync();
        }
    }

    /// <summary>
    /// Supplies the playlist's video configurator. Chains are numbered in the order they are
    /// built, and the chains <c>breaks</c> selects throw on their 21st frame.
    /// </summary>
    private sealed class FaultInjector(Func<int, bool> breaks)
    {
        private int _chains;

        public GraphChain<VideoFrameRef> Configure(GraphChain<VideoFrameRef> chain)
        {
            var index = Interlocked.Increment(ref _chains) - 1;
            if (!breaks(index))
                return chain;

            var frames = 0;
            return chain.Then(
                new OperatorNode<VideoFrameRef, VideoFrameRef>(
                    "inject-fault",
                    (frame, _) =>
                        ++frames == FaultFrame
                            ? throw new InjectedFault(index)
                            : ValueTask.FromResult<VideoFrameRef?>(frame)
                )
            );
        }
    }

    private sealed class InjectedFault(int chain) : Exception($"Injected fault in chain {chain}.")
    {
        /// <summary>Whether the error's exception chain contains an injected fault.</summary>
        public static bool Caused(PlaybackError error)
        {
            var pending = new Stack<Exception>();
            if (error.Inner is { } inner)
                pending.Push(inner);
            while (pending.TryPop(out var ex))
            {
                if (ex is InjectedFault)
                    return true;
                if (ex is AggregateException aggregate)
                {
                    foreach (var child in aggregate.InnerExceptions)
                        pending.Push(child);
                }
                else if (ex.InnerException is { } next)
                {
                    pending.Push(next);
                }
            }
            return false;
        }
    }

    private sealed class DiscardingVideoSink : IVideoSink
    {
        public IFramePool FramePool => null!;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
