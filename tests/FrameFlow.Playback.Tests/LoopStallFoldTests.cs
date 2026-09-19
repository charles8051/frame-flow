using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The controller's loop-stall path end to end: the position ticker sampling the clock, the fold
/// through <see cref="LoopStallEvaluator"/>, the rising edge, and the fan-out to
/// <c>ItemStalled</c> and <c>LoopStalled</c>.
/// </summary>
/// <remarks>
/// <para>
/// None of this was reachable before the clock seam (#314): the fold read
/// <c>Stopwatch.GetTimestamp()</c> inline and the ticker built its own timer, so a stall could only
/// be produced by waiting out real seconds.
/// </para>
/// <para>
/// One <see cref="FakeTimeProvider"/> drives the clock's position, the ticker's cadence and the
/// fold's timestamps, so advancing it moves all three together. <b>Advancing is what produces a
/// tick.</b> Waiting for one without advancing first hangs forever rather than eventually
/// arriving — which is not a timing flake but a deadlock, and it is the mistake this harness
/// exists to keep out of each test.
/// </para>
/// </remarks>
public sealed class LoopStallFoldTests
{
    private static readonly TimeSpan ItemDuration = TimeSpan.FromSeconds(10);

    /// <summary>Bounds a failure only. Every test here stays correct if it were ten times longer.</summary>
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task APositionPastTheDuration_WithARepeatExpected_StallsAndNamesTheItem()
    {
        var time = new FakeTimeProvider();
        var item = new PlaylistItem(new Source());
        var controller = New(time, new SessionPresentation(ExpectsRepeat: true, CurrentItem: item));
        await using var owned = controller;

        var stalls = new TaskCompletionSource<PlaylistItemStalled>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var bare = new TaskCompletionSource<LoopStalled>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // Arrival order, recorded rather than inferred: the contract is that the item-scoped event
        // comes first, so a consumer holding both sees the item before the bare report.
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var itemSub = controller.ItemStalled.Subscribe(
            new Relay<PlaylistItemStalled>(s =>
            {
                order.Enqueue("item");
                stalls.TrySetResult(s);
            })
        );
        using var bareSub = controller.LoopStalled.Subscribe(
            new Relay<LoopStalled>(s =>
            {
                order.Enqueue("bare");
                bare.TrySetResult(s);
            })
        );

        await PlayAsync(controller);

        // Past the item's end, then past the stall timeout on top of it.
        await Advance(
            time,
            ItemDuration + PlaybackControllerCore.LoopStallTimeout + TimeSpan.FromSeconds(2)
        );

        using var cts = new CancellationTokenSource(FailureBound);
        var stalled = await stalls.Task.WaitAsync(cts.Token);
        var loopStalled = await bare.Task.WaitAsync(cts.Token);

        Assert.Same(item, stalled.Item);
        Assert.Same(loopStalled, stalled.Stall);
        Assert.Equal(ItemDuration, loopStalled.Duration);
        Assert.True(
            loopStalled.Overrun >= PlaybackControllerCore.LoopStallTimeout,
            $"Overrun {loopStalled.Overrun} should have reached the timeout before reporting."
        );
        Assert.Equal(["item", "bare"], order);
    }

    [Fact]
    public async Task WithNoRepeatExpected_APositionPastTheDuration_DoesNotStall()
    {
        // The negative case. It is safe to assert here because the barrier below is a tick the
        // ticker itself raised, carrying a position well past the duration — so the fold has
        // demonstrably seen the state that would stall, rather than not having run yet.
        var time = new FakeTimeProvider();
        var controller = New(time, SessionPresentation.Empty);
        await using var owned = controller;

        var stalled = false;
        using var sub = controller.LoopStalled.Subscribe(
            new Relay<LoopStalled>(_ => Volatile.Write(ref stalled, true))
        );

        var threshold = ItemDuration + PlaybackControllerCore.LoopStallTimeout;
        var pastTheEnd = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var tickSub = controller.PositionTick.Subscribe(
            new Relay<TimeSpan>(p =>
            {
                if (p > threshold)
                    pastTheEnd.TrySetResult();
            })
        );

        await PlayAsync(controller);
        await Advance(time, threshold + TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(FailureBound);
        await pastTheEnd.Task.WaitAsync(cts.Token);

        Assert.False(Volatile.Read(ref stalled));
    }

    [Fact]
    public async Task AStallReportsOnce_NotOnEveryTickItPersists()
    {
        var time = new FakeTimeProvider();
        var item = new PlaylistItem(new Source());
        var controller = New(time, new SessionPresentation(ExpectsRepeat: true, CurrentItem: item));
        await using var owned = controller;

        var reports = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = controller.LoopStalled.Subscribe(
            new Relay<LoopStalled>(_ =>
            {
                Interlocked.Increment(ref reports);
                first.TrySetResult();
            })
        );

        await PlayAsync(controller);
        await Advance(
            time,
            ItemDuration + PlaybackControllerCore.LoopStallTimeout + TimeSpan.FromSeconds(1)
        );

        using var cts = new CancellationTokenSource(FailureBound);
        await first.Task.WaitAsync(cts.Token);

        // Ten more intervals of the same stall. The rising edge has already fired, so none report.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tickSub = controller.PositionTick.Subscribe(
            new Relay<TimeSpan>(_ => settled.TrySetResult())
        );
        await Advance(time, PositionTickerWorker.TickInterval * 10);
        await settled.Task.WaitAsync(cts.Token);

        Assert.Equal(1, Volatile.Read(ref reports));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances in whole tick intervals. A single jump of the whole span would coalesce into one
    /// tick, and the fold would never see the intermediate positions it needs to open an overrun
    /// episode and then time it out.
    /// </summary>
    private static async Task Advance(FakeTimeProvider time, TimeSpan by)
    {
        var steps = (int)Math.Ceiling(by / PositionTickerWorker.TickInterval);
        for (var i = 0; i < steps; i++)
        {
            time.Advance(PositionTickerWorker.TickInterval);
            // The ticker's loop resumes on the thread pool. Without yielding, a tight burst of
            // advances runs entirely between two of its waits, and PeriodicTimer keeps no backlog,
            // so every tick in the burst is dropped.
            await Task.Yield();
        }
    }

    private static async Task PlayAsync(PlaybackControllerCore controller)
    {
        Assert.True((await controller.LoadAsync(new Source())).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.Equal(ItemDuration, controller.Duration);
    }

    private static PlaybackControllerCore New(
        FakeTimeProvider time,
        SessionPresentation presentation
    ) =>
        new(
            NullLogger<PlaybackControllerCore>.Instance,
            new StallSessionFactory(new StallSession { Presentation = presentation }),
            new PlaybackClock(time),
            Microsoft.Extensions.Options.Options.Create(
                new FrameFlowPlaybackOptions { InitialRepeatMode = RepeatMode.One }
            ),
            time
        );

    private sealed record Source : IMediaSource
    {
        public string DisplayName => "stalling";
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    /// <summary>
    /// The minimum session these tests need. It <b>starts the clock on play</b>, which is not
    /// optional scaffolding: ADR-0028 makes the session the only thing allowed to move the playback
    /// clock, so a fake that skips it leaves Position pinned at zero. The ticker still ticks, the
    /// fold still runs, and every sample reports a position that never reaches the item duration —
    /// so nothing stalls and the test hangs on a signal that cannot arrive.
    /// </summary>
    private sealed class StallSession : IPlaybackSession
    {
        private static readonly MediaInfo Info = new("stall", ItemDuration, [], []);

        private IPlaybackClock? _clock;

        public SessionPresentation Presentation { get; set; } = SessionPresentation.Empty;

        public MediaInfo? MediaInfo => Info;

        public TimeSpan Duration => Info.Duration;

        public bool TryBeginReplay() => true;

        public bool CanSeekFromEnded => true;

        public void Bind(IPlaybackClock clock, SessionCallbacks callbacks) => _clock = clock;

        public ValueTask InitializeAsync(IMediaSource source, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WarmUpAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask PlayAsync(CancellationToken ct = default)
        {
            _clock?.Start(TimeSpan.Zero);
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StallSessionFactory(StallSession session) : IPlaybackSessionFactory
    {
        public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks)
        {
            session.Bind(clock, callbacks);
            return session;
        }

        public IMediaSource? ReserveStart() => null;

        public void ReleaseStart() { }
    }

    private sealed class Relay<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => onNext(value);
    }
}
