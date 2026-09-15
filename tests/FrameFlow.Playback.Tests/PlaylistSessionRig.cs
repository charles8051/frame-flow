namespace FrameFlow.Playback.Tests;

/// <summary>
/// Runs a <see cref="PlaylistSession"/> over fake item runtimes and records what it does as a
/// transcript. No FFmpeg, no clips and no real time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The transcript.</b> One line per effect, in the order the session caused them:
/// <list type="bullet">
///   <item><description>
///   <c>a#1.Open</c>: a call on an item runtime. <c>a#1</c> is the first runtime opened for
///   source <c>a</c>.
///   </description></item>
///   <item><description>
///   <c>ctl.EndOfStream</c>, <c>ctl.Fatal(message)</c>, <c>ctl.RecoverableError(message)</c>,
///   <c>ctl.ItemChanged(b)</c>: a report to the controller.
///   </description></item>
///   <item><description>
///   <c>clock.Stop</c>: a call on the controller's position clock.
///   </description></item>
///   <item><description>
///   <c>transition(b)</c> or <c>transition(a, wrapped)</c>: the coordinator's
///   <c>SourceTransitioned</c>.
///   </description></item>
/// </list>
/// A request that has not yet had an effect adds nothing.
/// </para>
/// <para>
/// <b>Scheduling.</b> The session's reader takes one input at a time from its channel. Item
/// notifications and the coordinator's requests are delivered through the rig's scheduler, which
/// posts them at once. Fake item calls complete at once unless a test holds or fails them.
/// <see cref="SettleAsync"/> waits until the reader has handled every input posted, after which
/// nothing more happens until the test acts. While the reader awaits a held call, an input posted
/// meanwhile waits in the channel.
/// </para>
/// <para>
/// <b>Deferred delivery.</b> A notification can reach the session after a later call, with what was
/// read when it was raised: an end-of-stream's run number, and the generation an end-of-stream or
/// skip is tagged with. <see cref="DeferHops"/> holds deliveries back and
/// <see cref="StartDeferredHops"/> posts them, in order. Before step 3 of the protocol ADR these
/// were thread-pool hops that started late; the names are kept from then.
/// </para>
/// </remarks>
internal sealed class PlaylistSessionRig : IAsyncDisposable
{
    // Bounds a failure only. Every wait here ends in microseconds when the session is right.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly List<string> _log = [];
    private readonly Dictionary<(string Source, ItemOp Op), Queue<Script>> _scripts = [];
    private readonly Dictionary<string, int> _opened = [];
    private readonly Dictionary<string, FakeItem> _runtimes = [];
    private readonly List<Hold> _holds = [];
    private readonly List<Action<PlaylistTransition>> _transitionHandlers = [];
    private readonly InlineScheduler _scheduler = new();
    private readonly IDisposable _transitions;

    private PlaylistSessionRig(RepeatMode repeat, string[] sources)
    {
        Coordinator = new PlaylistCoordinator(sources.Select(s => new FakeSource(s)), repeat);
        _transitions = Coordinator.SourceTransitioned.Subscribe(new Observer(OnTransition));
        Session = new PlaylistSession(
            Coordinator,
            new FakeClock(this),
            new SessionCallbacks(
                OnEndOfStream: () => Record("ctl.EndOfStream"),
                OnWorkerFaulted: ex => Record($"ctl.Fatal({ex.Message})"),
                OnBufferReady: () => Record("ctl.BufferReady"),
                OnBufferUnderrun: () => Record("ctl.BufferUnderrun"),
                OnRecoverableError: error => Record($"ctl.RecoverableError({error.Message})"),
                OnCurrentItemChanged: info => Record($"ctl.ItemChanged({info?.ContainerName})"),
                OnLoopRestarted: count => Record($"ctl.LoopRestarted({count})")
            ),
            new FakeItemFactory(this),
            scheduler: _scheduler
        );
    }

    public PlaylistCoordinator Coordinator { get; }

    public PlaylistSession Session { get; }

    /// <summary>A rig whose session has not been initialized.</summary>
    public static PlaylistSessionRig Create(RepeatMode repeat, params string[] sources) =>
        new(repeat, sources);

    /// <summary>
    /// A rig whose session is initialized and warmed, as the controller's load leaves it.
    /// </summary>
    public static async Task<PlaylistSessionRig> LoadedAsync(
        RepeatMode repeat,
        params string[] sources
    )
    {
        var rig = Create(repeat, sources);
        await rig.Session.InitializeAsync(rig.Coordinator.Snapshot().Playlist[0].Source);
        await rig.Session.WarmUpAsync();
        await rig.SettleAsync();
        rig.TakeLog();
        return rig;
    }

    /// <summary>A rig whose session has loaded and is playing its first item.</summary>
    public static async Task<PlaylistSessionRig> PlayingAsync(
        RepeatMode repeat,
        params string[] sources
    )
    {
        var rig = await LoadedAsync(repeat, sources);
        await rig.Session.PlayAsync();
        await rig.SettleAsync();
        rig.TakeLog();
        return rig;
    }

    /// <summary>
    /// Returns the transcript recorded since the last call, and starts a new one.
    /// </summary>
    public string[] TakeLog()
    {
        lock (_gate)
        {
            var taken = _log.ToArray();
            _log.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Waits until the session has handled every input posted to it. A deferred delivery has not
    /// been posted.
    /// </summary>
    public async Task SettleAsync()
    {
        await _scheduler.IdleAsync().WaitAsync(Bound);
        await Session.WhenIdleAsync().WaitAsync(Bound);
    }

    /// <summary>Holds the session's deliveries from now on instead of posting them.</summary>
    public void DeferHops() => _scheduler.Defer();

    /// <summary>Posts the held deliveries in order, and posts later ones at once again.</summary>
    public void StartDeferredHops() => _scheduler.StartDeferred();

    /// <summary>The runtime named in the transcript, such as <c>a#1</c>.</summary>
    public FakeItem Runtime(string name)
    {
        lock (_gate)
            return _runtimes[name];
    }

    /// <summary>The playlist item whose source is <paramref name="source"/>.</summary>
    public PlaylistItem PlaylistItem(string source) =>
        Coordinator.Snapshot().Playlist.Single(i => i.Source.DisplayName == source);

    /// <summary>Enqueues a one-shot item for <paramref name="source"/>.</summary>
    public PlaylistItem Enqueue(string source) => Coordinator.Enqueue(new FakeSource(source));

    /// <summary>
    /// Holds the next <paramref name="op"/> on a runtime of <paramref name="source"/> until
    /// released.
    /// </summary>
    /// <param name="source">The source whose runtime's call is held.</param>
    /// <param name="op">The call to hold.</param>
    /// <param name="runAdvancesFirst">
    /// For a seek or rewind: advance the run number before holding, as <see cref="SubstrateSession"/>
    /// does once it has stopped the run it interrupts. A seek held this way and then cancelled leaves
    /// the new run stopped. Without it, the hold is before the run advances.
    /// </param>
    public Hold Hold(string source, ItemOp op, bool runAdvancesFirst = false)
    {
        var hold = new Hold(runAdvancesFirst);
        lock (_gate)
        {
            _holds.Add(hold);
            ScriptsLocked(source, op).Enqueue(new Script(hold, null));
        }
        return hold;
    }

    /// <summary>
    /// Makes the next <paramref name="op"/> on a runtime of <paramref name="source"/> throw.
    /// </summary>
    public void Fail(string source, ItemOp op, Exception error)
    {
        lock (_gate)
            ScriptsLocked(source, op).Enqueue(new Script(null, error));
    }

    /// <summary>
    /// Runs <paramref name="handler"/> for each <c>SourceTransitioned</c>, after the transcript
    /// records it.
    /// </summary>
    public void OnSourceTransitioned(Action<PlaylistTransition> handler)
    {
        lock (_gate)
            _transitionHandlers.Add(handler);
    }

    public async ValueTask DisposeAsync()
    {
        Hold[] holds;
        lock (_gate)
            holds = [.. _holds];
        foreach (var hold in holds)
            hold.Release();

        await Session.DisposeAsync().AsTask().WaitAsync(Bound);
        StartDeferredHops();
        await SettleAsync();
        _transitions.Dispose();
        Coordinator.Dispose();
    }

    private void Record(string line)
    {
        lock (_gate)
            _log.Add(line);
    }

    private void OnTransition(PlaylistTransition t)
    {
        Record(
            t.Wrapped
                ? $"transition({t.Source.DisplayName}, wrapped)"
                : $"transition({t.Source.DisplayName})"
        );
        Action<PlaylistTransition>[] handlers;
        lock (_gate)
            handlers = [.. _transitionHandlers];
        foreach (var handler in handlers)
            handler(t);
    }

    private Queue<Script> ScriptsLocked(string source, ItemOp op)
    {
        if (!_scripts.TryGetValue((source, op), out var queue))
            _scripts[(source, op)] = queue = new Queue<Script>();
        return queue;
    }

    private string Opened(FakeItem item, string source)
    {
        lock (_gate)
        {
            _opened.TryGetValue(source, out var count);
            _opened[source] = ++count;
            var name = $"{source}#{count}";
            _runtimes[name] = item;
            return name;
        }
    }

    private async ValueTask PerformAsync(
        FakeItem item,
        ItemOp op,
        string line,
        CancellationToken cancellationToken
    )
    {
        var script = TakeScript(item, op, line);
        if (script?.Error is { } error)
            throw error;
        if (script?.Hold is { } hold)
            await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // A seek or rewind follows the model through the point where its run advances: before a hold
    // that asks for it, or once the call completes.
    private async ValueTask RepositionAsync(
        FakeItem item,
        ItemOp op,
        string line,
        CancellationToken cancellationToken
    )
    {
        var script = TakeScript(item, op, line);
        if (script?.Error is { } error)
            throw error;

        if (script?.Hold is { RunAdvancesFirst: true } advancing)
        {
            item.Follow(m => m.RunAdvanced());
            await advancing.WaitAsync(cancellationToken).ConfigureAwait(false);
            item.Follow(m => m.Relaunched());
            return;
        }

        if (script?.Hold is { } hold)
            await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        item.Follow(m => m.Repositioned());
    }

    private Script? TakeScript(FakeItem item, ItemOp op, string line)
    {
        lock (_gate)
        {
            _log.Add($"{item.Name}.{line}");
            return _scripts.TryGetValue((item.Source, op), out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : null;
        }
    }

    private sealed record Script(Hold? Hold, Exception? Error);

    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private sealed class FakeItemFactory(PlaylistSessionRig rig) : IPlaylistItemRuntimeFactory
    {
        public IPlaylistItemRuntime CreateItem(IPlaybackClock clock, SessionCallbacks callbacks) =>
            new FakeItem(rig, callbacks);
    }

    /// <summary>
    /// An item runtime that records each call, and follows <see cref="PlaylistItemModel"/> when a
    /// call completes. A seek or a rewind advances the run number then, as
    /// <see cref="SubstrateSession"/> advances it once the run it interrupts has stopped; a hold on
    /// either is a hold before that point.
    /// </summary>
    internal sealed class FakeItem(PlaylistSessionRig rig, SessionCallbacks callbacks)
        : IPlaylistItemRuntime
    {
        private PlaylistItemModel _model = PlaylistItemModel.Opened;

        public string Name { get; private set; } = "?";

        public string Source { get; private set; } = "?";

        public MediaInfo? MediaInfo { get; private set; }

        public TimeSpan Duration => MediaInfo?.Duration ?? TimeSpan.Zero;

        public int RunNumber => Volatile.Read(ref _model).Run;

        /// <summary>
        /// Raises end-of-stream, as the item's last worker does when the run drains.
        /// </summary>
        public void RaiseEndOfStream() => callbacks.OnEndOfStream();

        /// <summary>Raises a worker fault.</summary>
        public void RaiseFault(Exception error) => callbacks.OnWorkerFaulted(error);

        public async ValueTask InitializeAsync(
            IMediaSource source,
            CancellationToken cancellationToken = default
        )
        {
            Source = source.DisplayName;
            Name = rig.Opened(this, Source);
            await rig.PerformAsync(this, ItemOp.Open, "Open", cancellationToken);
            MediaInfo = new MediaInfo(Source, TimeSpan.FromSeconds(3), [], []);
        }

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
            rig.PerformAsync(this, ItemOp.WarmUp, "WarmUp", cancellationToken);

        public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
        {
            await rig.PerformAsync(this, ItemOp.Play, "Play", cancellationToken);
            Follow(m => m.Played());
        }

        public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
        {
            await rig.PerformAsync(this, ItemOp.Pause, "Pause", cancellationToken);
            Follow(m => m.PausedNow());
        }

        public async ValueTask SeekAsync(
            TimeSpan position,
            CancellationToken cancellationToken = default
        )
        {
            await rig.RepositionAsync(this, ItemOp.Seek, $"Seek({position})", cancellationToken);
        }

        public async ValueTask RewindToStartAsync(CancellationToken cancellationToken = default)
        {
            await rig.RepositionAsync(this, ItemOp.Rewind, "Rewind", cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await rig.PerformAsync(this, ItemOp.Dispose, "Dispose", CancellationToken.None);
            Follow(m => m.DisposedNow());
        }

        internal void Follow(Func<PlaylistItemModel, PlaylistItemModel> transition) =>
            Volatile.Write(ref _model, transition(Volatile.Read(ref _model)));
    }

    private sealed class FakeClock(PlaylistSessionRig rig) : IPlaybackClock
    {
        public TimeSpan Position => TimeSpan.Zero;

        public bool IsRunning => false;

        public bool IsPaused => false;

        public void Start(TimeSpan startPosition) => rig.Record($"clock.Start({startPosition})");

        public void Pause() => rig.Record("clock.Pause");

        public void Resume() => rig.Record("clock.Resume");

        public void Seek(TimeSpan position) => rig.Record($"clock.Seek({position})");

        public void Stop() => rig.Record("clock.Stop");
    }

    /// <summary>
    /// Runs each delivery on the calling thread, and keeps its task so <see cref="IdleAsync"/> can
    /// wait for it. While deferring, it queues deliveries instead, until
    /// <see cref="StartDeferred"/>.
    /// </summary>
    private sealed class InlineScheduler : IPlaylistSessionScheduler
    {
        private readonly object _gate = new();
        private readonly List<Task> _running = [];
        private readonly Queue<Func<Task>> _deferred = new();
        private bool _deferring;

        public void Schedule(Func<Task> work)
        {
            lock (_gate)
            {
                if (_deferring)
                {
                    _deferred.Enqueue(work);
                    return;
                }
            }
            Start(work);
        }

        public void Defer()
        {
            lock (_gate)
                _deferring = true;
        }

        /// <summary>
        /// Stops deferring, and runs the queued deliveries in the order they were scheduled. A
        /// delivery they schedule runs at once, before the rest of the queue.
        /// </summary>
        public void StartDeferred()
        {
            while (true)
            {
                Func<Task> work;
                lock (_gate)
                {
                    _deferring = false;
                    if (!_deferred.TryDequeue(out work!))
                        return;
                }
                Start(work);
            }
        }

        private void Start(Func<Task> work)
        {
            var task = work();
            lock (_gate)
                _running.Add(task);
        }

        public async Task IdleAsync()
        {
            while (true)
            {
                Task[] running;
                lock (_gate)
                {
                    _running.RemoveAll(t => t.IsCompleted);
                    running = [.. _running];
                }
                if (running.Length == 0)
                    return;
                await Task.WhenAll(running);
            }
        }
    }

    private sealed class Observer(Action<PlaylistTransition> onNext) : IObserver<PlaylistTransition>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(PlaylistTransition value) => onNext(value);
    }
}

/// <summary>A call on an item runtime that a test can hold or fail.</summary>
internal enum ItemOp
{
    Open,
    WarmUp,
    Play,
    Pause,
    Seek,
    Rewind,
    Dispose,
}

/// <summary>
/// Holds one item call until the test releases it, or the call's token is cancelled.
/// </summary>
internal sealed class Hold(bool runAdvancesFirst = false)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>For a seek or rewind: whether the run advances before the hold.</summary>
    public bool RunAdvancesFirst => runAdvancesFirst;

    private readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>Completes when the held call has started.</summary>
    public Task EnteredAsync() => _entered.Task.WaitAsync(Bound);

    /// <summary>Lets the held call complete.</summary>
    public void Release() => _released.TrySetResult();

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
