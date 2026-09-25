using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// A playlist controller from <see cref="PlaybackController.CreatePlaylist"/> over a video sink
/// that counts and discards frames, with no audio sink and software decode. It records the
/// controller's errors and the coordinator's transitions, and hands out signals for states,
/// transitions and frame counts so a test can wait on them.
/// </summary>
internal sealed class PlaylistRun : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<PlaybackError> _errors = [];
    private readonly List<PlaylistTransition> _transitions = [];
    private readonly List<(PlaybackState State, TaskCompletionSource Signal)> _stateWaiters = [];
    private readonly List<(IMediaSource Source, TaskCompletionSource Signal)> _sourceWaiters = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _countWaiters = [];
    private readonly List<int> _loops = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _loopWaiters = [];
    private readonly TaskCompletionSource _gaveUp = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly IDisposable[] _subscriptions;
    private readonly IMediaSource _firstItem;

    private PlaylistRun(
        IPlaybackController controller,
        PlaylistCoordinator coordinator,
        PresentCountingVideoSink sink,
        IMediaSource firstItem
    )
    {
        _firstItem = firstItem;
        Controller = controller;
        Coordinator = coordinator;
        Sink = sink;
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
                    {
                        _transitions.Add(t);
                        foreach (var (source, signal) in _sourceWaiters)
                        {
                            if (ReferenceEquals(source, t.Item.Source))
                                signal.TrySetResult();
                        }
                        foreach (var (count, signal) in _countWaiters)
                        {
                            if (_transitions.Count >= count)
                                signal.TrySetResult();
                        }
                    }
                })
            ),
            controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(loop =>
                {
                    lock (_gate)
                    {
                        _loops.Add(loop.LoopCount);
                        foreach (var (count, signal) in _loopWaiters)
                        {
                            if (_loops.Count >= count)
                                signal.TrySetResult();
                        }
                    }
                })
            ),
            controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    lock (_gate)
                    {
                        foreach (var (state, signal) in _stateWaiters)
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

    public PresentCountingVideoSink Sink { get; }

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

    /// <summary>The loop count each <see cref="IPlaybackController.LoopRestarted"/> carried.</summary>
    public IReadOnlyList<int> Loops
    {
        get
        {
            lock (_gate)
                return _loops.ToArray();
        }
    }

    /// <summary>Completes once at least <paramref name="count"/> loops have been reported.</summary>
    public Task WhenLoops(int count)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_loops.Count >= count)
                signal.TrySetResult();
            else
                _loopWaiters.Add((count, signal));
        }
        return signal.Task;
    }

    /// <param name="items">The playlist, or the one source of a single-source run.</param>
    /// <param name="repeat">The repeat mode both the controller and the queue start with.</param>
    /// <param name="configureVideo">The video-chain configurator applied to every item.</param>
    /// <param name="clock">The controller's clock. A fresh <see cref="PlaybackClock"/> by default.</param>
    /// <param name="asSingleSource">
    /// Builds the run through <see cref="PlaybackController.Create"/>, the single-source entry
    /// point, instead of <see cref="PlaybackController.CreatePlaylist"/>. Both play a queue; the
    /// first seeds it from the source it loads. Only for one item.
    /// </param>
    public static PlaylistRun Create(
        IMediaSource[] items,
        RepeatMode repeat,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        IPlaybackClock? clock = null,
        bool asSingleSource = false
    )
    {
        var sink = new PresentCountingVideoSink();

        if (asSingleSource)
        {
            Assert.Single(items);

            var single = PlaybackController.Create(
                videoSink: sink,
                audioSink: null,
                hardwareDecodeMode: HardwareDecodeMode.Disabled,
                initialRepeatMode: repeat,
                clock: clock,
                configureVideo: configureVideo
            );
            var factory = (PlaylistSessionFactory)((PlaybackControllerCore)single).SessionFactory;
            return new PlaylistRun(single, factory.Coordinator, sink, items[0]);
        }

        var coordinator = new PlaylistCoordinator(items, repeat);
        var controller = PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: sink,
            audioSink: null,
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: repeat,
            clock: clock,
            configureVideo: configureVideo
        );
        return new PlaylistRun(controller, coordinator, sink, items[0]);
    }

    /// <summary>Loads the playlist, which starts its first item warming.</summary>
    public async Task LoadAsync()
    {
        // The session takes its first item from the coordinator's queue, not from the source
        // the controller is handed, so the controller is given the first item for form's sake.
        var load = await Controller.LoadAsync(_firstItem);
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
    }

    /// <summary>Loads the playlist and plays it.</summary>
    public async Task PlayAsync()
    {
        await LoadAsync();
        var play = await Controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
    }

    /// <summary>
    /// Completes when the player enters <paramref name="state"/>, or at once if it is already
    /// there.
    /// </summary>
    public Task Settled(PlaybackState state)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _stateWaiters.Add((state, signal));
        if (Controller.State == state)
            signal.TrySetResult();
        return signal.Task;
    }

    /// <summary>
    /// Completes when <paramref name="source"/> becomes the current item. Ask before the action
    /// that makes it current.
    /// </summary>
    public Task Transitioned(IMediaSource source)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _sourceWaiters.Add((source, signal));
        return signal.Task;
    }

    /// <summary>Completes once at least <paramref name="count"/> transitions have been raised.</summary>
    public Task WhenTransitions(int count)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_transitions.Count >= count)
                signal.TrySetResult();
            else
                _countWaiters.Add((count, signal));
        }
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
/// A video sink that counts the frames presented to it and discards them, and completes a
/// signal when the count reaches a given value.
/// </summary>
internal sealed class PresentCountingVideoSink : IVideoSink
{
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _presented;

    public int Presented => Volatile.Read(ref _presented);

    /// <summary>Completes once at least <paramref name="count"/> frames have been presented.</summary>
    public Task WhenPresented(int count)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_presented >= count)
                signal.TrySetResult();
            else
                _waiters.Add((count, signal));
        }
        return signal.Task;
    }

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        frame.Dispose();
        lock (_gate)
        {
            var presented = Interlocked.Increment(ref _presented);
            foreach (var (count, signal) in _waiters)
            {
                if (presented >= count)
                    signal.TrySetResult();
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A <see cref="PlaybackClock"/> that can hold the thread which next calls <see cref="Pause"/>
/// or <see cref="Stop"/> until the test releases it. A playlist advance makes both calls while it
/// holds its session's transition gate: a skip that ends the queue pauses the item it keeps, and
/// an advance to another item stops the clock after it has taken that item and before it starts
/// it. Holding the call holds the gate, so a test can act at a known point inside the advance.
/// </summary>
internal sealed class HoldableClock : IPlaybackClock, IDisposable
{
    private static readonly TimeSpan HoldBound = TimeSpan.FromSeconds(30);

    private readonly PlaybackClock _inner = new();
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _released = new(initialState: true);
    private TaskCompletionSource? _holdPause;
    private TaskCompletionSource? _holdStop;

    public TimeSpan Position => _inner.Position;

    public bool IsRunning => _inner.IsRunning;

    public bool IsPaused => _inner.IsPaused;

    /// <summary>
    /// Arms a hold on the next <see cref="Pause"/>. The returned task completes when a thread
    /// is held there; <see cref="Release"/> lets it go.
    /// </summary>
    public Task HoldNextPause() => Arm(ref _holdPause);

    /// <summary>
    /// Arms a hold on the next <see cref="Stop"/>. The returned task completes when a thread
    /// is held there; <see cref="Release"/> lets it go.
    /// </summary>
    public Task HoldNextStop() => Arm(ref _holdStop);

    public void Release() => _released.Set();

    public void Pause()
    {
        HoldIfArmed(ref _holdPause);
        _inner.Pause();
    }

    public void Stop()
    {
        HoldIfArmed(ref _holdStop);
        _inner.Stop();
    }

    public void Start(TimeSpan startPosition) => _inner.Start(startPosition);

    public void Resume() => _inner.Resume();

    public void Seek(TimeSpan position) => _inner.Seek(position);

    public void Dispose() => _released.Dispose();

    private Task Arm(ref TaskCompletionSource? slot)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _released.Reset();
            slot = holding;
        }
        return holding.Task;
    }

    private void HoldIfArmed(ref TaskCompletionSource? slot)
    {
        TaskCompletionSource? holding;
        lock (_gate)
        {
            holding = slot;
            slot = null;
        }

        if (holding is null)
            return;

        holding.TrySetResult();
        // Bounded so a test that fails before releasing does not hang the run.
        _released.Wait(HoldBound);
    }
}
