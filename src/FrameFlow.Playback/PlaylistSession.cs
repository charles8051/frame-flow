// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// An <see cref="IPlaybackSession"/> that presents an ordered, optionally
/// looping sequence of sources through ONE set of caller-supplied sinks. The
/// controller drives it as a single session; internally it composes a sequence
/// of per-item <see cref="SubstrateSession"/> runtimes over the <b>same</b> warm
/// sinks and the <b>same</b> clock, swapping only the decode runtime at each item
/// boundary. The video + audio sinks and their GPU resources are never disposed
/// here (ADR-0044), so the presenter stays warm across the whole playlist — the
/// per-item present-pipeline rebuild that a consumer pays today is eliminated.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a playlist looks to the controller.</b> Per-item end-of-stream is
/// intercepted here, not bubbled: when an item ends, this session advances to the
/// next item (disposing the finished runtime, rebasing the clock, warming and
/// playing the next) and the controller stays in <c>Playing</c>. The controller's
/// real <see cref="SessionCallbacks.OnEndOfStream"/> fires only when the queue is
/// exhausted <i>and</i> the repeat policy is not looping — so a looping playlist
/// looks like a session that simply never ends.
/// </para>
/// <para>
/// <b>Clock across items.</b> The controller's position clock is rebased to zero
/// at each boundary so every item reports a clean <c>0 → duration</c> timeline.
/// The per-item master pacing clock is selected inside each
/// <see cref="SubstrateSession"/> by whether <i>that</i> item has an activated
/// audio stream (audio sink when it does, wallclock otherwise) — which is what
/// lets one warm audio sink span a playlist of mixed audio/silent items.
/// </para>
/// <para>
/// <b>Robustness.</b> An item that fails to open or start, or faults while it
/// plays, is skipped, so a single corrupt file does not kill the rotation. Each
/// such failure is reported to the controller through
/// <see cref="SessionCallbacks.OnRecoverableError"/>, which raises it on
/// <c>ErrorOccurred</c> without changing state. <see cref="PlaylistFailureGuard"/>
/// bubbles a fatal error when items keep failing in a row, rather than looping
/// over a queue that cannot play.
/// </para>
/// </remarks>
internal sealed class PlaylistSession : IPlaybackSession
{
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly IPlaybackClock _clock;
    private readonly SessionCallbacks _controllerCallbacks;
    private readonly PlaylistCoordinator _coordinator;
    private readonly HardwareDecodeMode _hwMode;
    private readonly HardwareDecodeCapabilities? _hwCapabilities;
    private readonly bool _yieldHardwareFrames;
    private readonly Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? _videoConfigurator;
    private readonly Func<
        GraphChain<PcmAudioBufferRef>,
        GraphChain<PcmAudioBufferRef>
    >? _audioConfigurator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PlaylistSession> _logger;

    // Serializes the per-item advance against controller-driven Pause/Seek/Dispose.
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    // The runtime for the item presenting right now, and the coordinator's item it plays.
    private SubstrateSession? _current;
    private PlaylistItem? _currentItem;

    // Returned by the coordinator when this session attaches its skip and jump handlers.
    private object? _sessionToken;

    // Monotonic generation tag stamped into each item's callbacks so a stale
    // end-of-stream / fault from an already-replaced item is ignored.
    private int _currentGen;

    // The generation whose fault was last handled. A faulted item is never rewound
    // in place, so a second fault carrying it is another worker reporting the same
    // failure, and is not counted or reported again.
    private int _lastFaultedGen = -1;

    // The failure count lives on the coordinator, so it survives a replay from Ended.
    private PlaylistFailureGuard Failures => _coordinator.Failures;

    // Set once this session hands the controller a fatal error. The controller disposes
    // the session on its way into Error; until then a late notification must not start
    // another item.
    private bool _gaveUp;

    // What the controller last asked of this session, as a RunState. An advance follows it
    // (#182): it plays the next item only while Playing, and nothing advances before the
    // first play or after the queue has ended. Written under _transitionGate; the skip handler
    // also reads it without the gate, hence Volatile.
    private int _runState = (int)RunState.NotStarted;

    // Whether PlayAsync has been called on _current. An item an advance opened while paused
    // has not been, and neither has the first item before the first play.
    private bool _currentPlayed;

    // Whether _current was opened by an advance while paused and is waiting for PlayAsync to
    // start it. If that start fails, the item is skipped like one that could not be started.
    private bool _currentAwaitsPlay;
    private bool _disposed;

    private enum RunState
    {
        /// <summary>
        /// Before the first <see cref="PlayAsync"/>: the controller is loading the first item
        /// or paused on it. Left once, so an item a pending skip starts inside that first
        /// PlayAsync counts as played.
        /// </summary>
        NotStarted,

        /// <summary>After <see cref="PlayAsync"/>.</summary>
        Playing,

        /// <summary>
        /// After <see cref="PauseAsync"/>, or the warm-up of a seek out of <see cref="Ended"/>.
        /// </summary>
        Paused,

        /// <summary>
        /// The queue ran out and end-of-stream was reported. The item that ended the queue is
        /// kept if it ended or was skipped without failing, and is not played again until a
        /// seek out of Ended. Only that seek leaves Ended: a Play, Pause or Seek the controller
        /// dispatched before it saw the end-of-stream must not replace it, because the
        /// controller ends when it does. A Play from Ended never reaches this session; the
        /// controller replays on a new one.
        /// </summary>
        Ended,
    }

    /// <summary>How an item's run came to an end.</summary>
    private enum ItemEnding
    {
        /// <summary>The item reported end-of-stream.</summary>
        EndOfStream,

        /// <summary>The caller skipped the item.</summary>
        Skip,

        /// <summary>The item faulted while it played.</summary>
        Fault,

        /// <summary>The item could not be started.</summary>
        FailedStart,
    }

    private RunState CurrentRunState => (RunState)Volatile.Read(ref _runState);

    private void SetRunState(RunState state) => Volatile.Write(ref _runState, (int)state);

    public PlaylistSession(
        PlaylistCoordinator coordinator,
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        IPlaybackClock clock,
        SessionCallbacks controllerCallbacks,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? audioConfigurator = null,
        bool yieldHardwareFrames = false
    )
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(clock);

        _coordinator = coordinator;
        _videoSink = videoSink;
        _audioSink = audioSink;
        _clock = clock;
        _controllerCallbacks = controllerCallbacks;
        _hwMode = hwMode;
        _hwCapabilities = hardwareDecodeCapabilities;
        _yieldHardwareFrames = yieldHardwareFrames;
        _videoConfigurator = videoConfigurator;
        _audioConfigurator = audioConfigurator;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PlaylistSession>();
    }

    // ── IPlaybackSession read-only surface (delegates to the current item) ──

    public MediaInfo? MediaInfo => _current?.MediaInfo;

    public TimeSpan Duration => _current?.Duration ?? TimeSpan.Zero;

    public PipelineDiagnosticsSnapshot GetPipelineDiagnostics() =>
        _current?.GetPipelineDiagnostics() ?? PipelineDiagnosticsSnapshot.Empty;

    // A replay from Ended loads a new PlaylistSession. The item it starts with is taken here,
    // before the controller unloads this one, so an edit in between cannot leave it nothing.
    public bool TryBeginReplay() => _coordinator.ReserveStart();

    // Read by the controller at Ended. The advance that ended the queue set _current before it
    // reported the end-of-stream, and nothing changes it while this session is Ended.
    public bool CanSeekFromEnded => _current is not null;

    // The coordinator runs the repeat mode. This session reports end-of-stream only when the
    // queue has ended.
    public bool LoopsInternally => true;

    // ── IPlaybackSession lifecycle ──────────────────────────────────────────

    public async ValueTask InitializeAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        // The controller hands us a source; the coordinator is the authority for the queue, so
        // take the first item from it. On the first load that is the source the controller was
        // given. On a replay from Ended it is the item TryBeginReplay reserved. Subsequent items
        // are taken by the advance path.
        var item =
            _coordinator.TakeStart()
            ?? throw new InvalidOperationException("The playlist has nothing queued to play.");

        SubstrateSession started;
        while (true)
        {
            var session = CreateItemSession(_currentGen);
            try
            {
                await session.InitializeAsync(item.Source, cancellationToken).ConfigureAwait(false);
                started = session;
                break;
            }
            catch (Exception ex)
                when (_coordinator.AnyStarted
                    && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                )
            {
                // Once the player has started any item, a new session's first item that cannot
                // be opened is passed over as an advance passes over one (#171). Only the
                // player's very first item still fails the load, like a single source's.
                await SafeDisposeAsync(session).ConfigureAwait(false);
                LogItemSkipped(_logger, item.Source.DisplayName, ex);
                ReportItemFailure(item.Source.DisplayName, "could not be started", ex);

                if (Failures.ItemFailed(playedFor: TimeSpan.Zero, itemLength: TimeSpan.Zero))
                    throw GiveUpException(ex);

                var next = _coordinator.DecideNext(PlaylistAdvance.FailedStart);
                if (next.Kind == PlaylistCoordinator.NextKind.End)
                    throw new InvalidOperationException(
                        "No playlist item could be opened.",
                        ex
                    );

                item = next.Item!;
                _currentGen++;
            }
            catch
            {
                await SafeDisposeAsync(session).ConfigureAwait(false);
                throw; // first-item failure surfaces as a load failure, like single-source.
            }
        }

        _current = started;
        _currentItem = item;
        _currentPlayed = false;
        _currentAwaitsPlay = false;

        _sessionToken = _coordinator.AttachSession(OnSkipRequested, OnJumpRequested);

        _coordinator.ReportCurrent(item, started.MediaInfo, wrapped: false);
    }

    public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
    {
        // Held for the whole warm-up, so no advance can dispose the item while it warms.
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            if (_current is not null)
                await _current.WarmUpAsync(cancellationToken).ConfigureAwait(false);

            // The controller warms up on load and on a seek out of Ended. The seek settles
            // it in Paused straight after this returns, and this is the last call it makes
            // before it gets there, so a skip issued once it is Paused sees the session paused.
            if (CurrentRunState == RunState.Ended)
                SetRunState(RunState.Paused);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // This Play was dispatched before the controller saw the end-of-stream that is on
            // its way, and the controller ends when it does.
            if (_disposed || CurrentRunState == RunState.Ended)
                return;

            SetRunState(RunState.Playing);

            // A skip or end-of-stream latched before this play, or a jump waiting for it, takes
            // effect now. A jump supersedes a latched skip, so both are consumed.
            var latched = _coordinator.ConsumeLatchedAdvance();
            if (_coordinator.HasPendingJump || latched is not null)
            {
                await AdvanceLockedAsync(
                        latched == PlaylistAdvance.EndOfStream && !_coordinator.HasPendingJump
                            ? ItemEnding.EndOfStream
                            : ItemEnding.Skip
                    )
                    .ConfigureAwait(false);
                return;
            }

            if (_current is null)
                return;

            try
            {
                await _current.PlayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (_currentAwaitsPlay
                    && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                )
            {
                // A caller that cancelled its Play gets the cancellation, and the item stays.
                // An item an advance opened while paused starts here. Failing to start is
                // what it would have done inside the advance, so handle it the same way.
                await ItemFailedToStartLockedAsync(ex).ConfigureAwait(false);
                return;
            }

            _currentPlayed = true;
            _currentAwaitsPlay = false;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            // Only a playing session pauses. At Ended the controller is about to end on the
            // end-of-stream already on its way.
            if (CurrentRunState == RunState.Playing)
                SetRunState(RunState.Paused);
            if (_current is not null)
                await _current.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default
    )
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _current is null)
                return;
            // A seek out of Ended has already warmed up, which left Ended. This one was
            // dispatched before the controller saw the end-of-stream on its way, and the
            // controller ends when it does, so it must not start the kept item.
            if (CurrentRunState == RunState.Ended)
                return;
            // Seek is scoped to the current item's timeline.
            await _current.SeekAsync(position, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask RewindToStartAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _current is null)
                return;
            // The loop rewind is scoped to the current item's timeline, same as a
            // seek to 0; delegate to the inner session's cheap rewind.
            await _current.RewindToStartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        // A skip or jump requested from here on waits on the coordinator for the next session.
        if (_sessionToken is not null)
            _coordinator.DetachSession(_sessionToken);

        // Drain any in-flight advance before tearing the current item down.
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _current;
            _current = null;
            if (current is not null)
                await SafeDisposeAsync(current).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }

        _transitionGate.Dispose();
        // The sinks, the clock, and the coordinator are owned by the caller —
        // not disposed here (ADR-0044).
    }

    // ── Advance orchestration ───────────────────────────────────────────────

    /// <summary>
    /// Callbacks handed to each per-item <see cref="SubstrateSession"/>. The
    /// buffer callbacks and recoverable errors bubble straight to the controller;
    /// end-of-stream and faults route into the advance path, tagged with the
    /// item's generation so a stale notification from a replaced item is ignored.
    /// An end-of-stream also carries the item's run number, read when it is raised,
    /// so one raised before a seek or rewind of the same item is ignored too.
    /// </summary>
    private SessionCallbacks CreateItemCallbacks(int gen, Func<int> runNumber) =>
        new(
            OnEndOfStream: () => OnItemEnded(gen, ItemEnding.EndOfStream, runNumber(), error: null),
            OnWorkerFaulted: ex => OnItemEnded(gen, ItemEnding.Fault, run: 0, error: ex),
            OnBufferReady: _controllerCallbacks.OnBufferReady,
            OnBufferUnderrun: _controllerCallbacks.OnBufferUnderrun,
            OnRecoverableError: _controllerCallbacks.OnRecoverableError,
            OnCurrentItemChanged: _controllerCallbacks.OnCurrentItemChanged
        );

    private SubstrateSession CreateItemSession(int gen)
    {
        // The item raises callbacks only once it is initialized, so the session is assigned
        // before the end-of-stream callback reads it.
        SubstrateSession? session = null;
        session = new SubstrateSession(
            _videoSink,
            _audioSink,
            _clock,
            CreateItemCallbacks(gen, () => session!.RunNumber),
            _hwMode,
            _hwCapabilities,
            _loggerFactory,
            _videoConfigurator,
            _audioConfigurator,
            _yieldHardwareFrames
        );
        return session;
    }

    /// <summary>
    /// The coordinator's skip entry point. A skip is an end-of-stream the caller asked for
    /// and follows the same run-state rules, but the two states where it does not advance are
    /// settled here, when it is requested, so a caller that skips and then plays sees the
    /// skip's effect in order.
    /// </summary>
    private void OnSkipRequested()
    {
        if (_disposed)
            return;

        switch (CurrentRunState)
        {
            case RunState.Ended:
                // The queue has already ended, so there is nothing to end. PlayAsync from
                // Ended plays whatever is queued.
                LogAdvanceIgnoredAtEnd(_logger, faulted: false);
                return;

            case RunState.NotStarted:
                // Nothing has played: the skip takes effect when the first PlayAsync does.
                _coordinator.LatchAdvance(PlaylistAdvance.Skip);
                // That PlayAsync may have started between the read above and the latch, and
                // missed it. If it has and the latch is still set, take it back and advance.
                if (
                    CurrentRunState == RunState.NotStarted
                    || _coordinator.ConsumeLatchedAdvance() is null
                )
                    return;
                break;
        }

        // Tagged with the current generation, so a skip and a natural end-of-stream that race
        // collapse to a single advance via the gen check under the gate. A skip is never
        // stale by run: a seek between the request and the advance does not cancel it.
        OnItemEnded(_currentGen, ItemEnding.Skip, run: 0, error: null);
    }

    /// <summary>
    /// The coordinator's jump entry point. The jump itself waits on the coordinator; this only
    /// asks for an advance to take it.
    /// </summary>
    /// <remarks>
    /// The advance is not tagged with a generation, because a jump does not end a particular
    /// item. It runs under the gate and takes the jump only if one is still pending, so it
    /// collapses with an advance already under way, which looks for a pending jump once its item
    /// has started. At <c>Ended</c> and before the first play it does nothing: the next
    /// <see cref="PlayAsync"/>, or the replay the controller loads, takes the jump.
    /// </remarks>
    private void OnJumpRequested()
    {
        if (_disposed)
            return;
        _ = Task.Run(AdvanceForJumpAsync);
    }

    private async Task AdvanceForJumpAsync()
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _gaveUp || !_coordinator.HasPendingJump)
                return;
            if (CurrentRunState is RunState.Ended or RunState.NotStarted)
                return;

            await AdvanceLockedAsync(ItemEnding.Skip).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unexpected orchestration failure is fatal — surface it.
            _controllerCallbacks.OnWorkerFaulted(ex);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void OnItemEnded(int gen, ItemEnding how, int run, Exception? error)
    {
        if (_disposed)
            return;

        // How far a faulted item played is read here, when it faulted. The advance may
        // wait on the gate behind a seek or a slow SourceTransitioned subscriber, and the
        // position clock keeps running meanwhile. The clock starts from zero for each
        // item and each in-place rewind, and stops while paused.
        var playedFor = how == ItemEnding.Fault ? _clock.Position : TimeSpan.Zero;

        // Hop off the worker thread that raised the callback; the advance does
        // heavy work (dispose + open + warmup) that must not block the graph.
        _ = Task.Run(() => AdvanceAsync(gen, how, run, error, playedFor));
    }

    private async Task AdvanceAsync(
        int gen,
        ItemEnding how,
        int run,
        Exception? error,
        TimeSpan playedFor
    )
    {
        var faulted = how == ItemEnding.Fault;

        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _gaveUp || gen != _currentGen)
                return; // stale notification from an already-replaced item.

            // The end-of-stream came from a run of this item that a seek or rewind has since
            // replaced (#170). The item is playing again, so acting on it would end the item
            // that was just sought, or end the playlist while it plays.
            if (how == ItemEnding.EndOfStream && run != _current?.RunNumber)
            {
                LogStaleEndOfStream(_logger, run, _current?.RunNumber ?? -1);
                return;
            }

            switch (CurrentRunState)
            {
                case RunState.Ended:
                    // The item that ended the queue has already been handled: a late
                    // notification from it has nothing to act on, and a skip that raced the
                    // end has nothing to end. Advancing here would start a queued item while
                    // the controller says Ended; PlayAsync from Ended plays it instead.
                    LogAdvanceIgnoredAtEnd(_logger, faulted);
                    return;

                case RunState.NotStarted when !faulted:
                    // Nothing has played. The advance waits for the first PlayAsync, as a
                    // skip issued before the playlist loaded does, and keeps why it happened.
                    _coordinator.LatchAdvance(
                        how == ItemEnding.EndOfStream
                            ? PlaylistAdvance.EndOfStream
                            : PlaylistAdvance.Skip
                    );
                    return;
            }

            if (faulted)
            {
                if (gen == _lastFaultedGen)
                    return;
                _lastFaultedGen = gen;

                var source = _currentItem?.Source.DisplayName ?? "(unknown)";

                if (CurrentRunState == RunState.NotStarted)
                {
                    // Nothing has played, so this is the first item failing to start, as
                    // a single source's would. Hand it to the controller as one.
                    _gaveUp = true;
                    _controllerCallbacks.OnWorkerFaulted(
                        error
                            ?? new InvalidOperationException(
                                $"Playlist item '{source}' faulted before playback started."
                            )
                    );
                    return;
                }

                LogItemFaulted(_logger, source, error);
                ReportItemFailure(source, "faulted during playback", error);

                if (Failures.ItemFailed(playedFor, _current?.Duration ?? TimeSpan.Zero))
                {
                    GiveUp(error);
                    return;
                }
            }

            await AdvanceLockedAsync(how).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unexpected orchestration failure is fatal — surface it.
            _controllerCallbacks.OnWorkerFaulted(ex);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>
    /// Advances the playlist when the current item ends, faults, or is skipped.
    /// Caller must hold <see cref="_transitionGate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same-source replay (the gapless single-clip loop).</b> The next decision
    /// is consumed <i>before</i> any teardown. When it is a
    /// <see cref="PlaylistCoordinator.NextKind.Replay"/> — a single-clip
    /// <see cref="RepeatMode.All"/> wrap, or <see cref="RepeatMode.One"/> — and the
    /// runtime is intact (not faulted), the current <see cref="SubstrateSession"/>
    /// is reused in place via its cheap rewind (<see cref="SubstrateSession.RewindToStartAsync"/>):
    /// the retained graph re-runs on the <i>same decode device</i>, so nothing is
    /// disposed, the decode device never changes, and the warm presenter never
    /// rebinds — the loop seam costs ~one frame, not a full open + decoder-bind +
    /// converter-rebind. A faulted item is never replayed in place (its runtime is
    /// suspect); a rewind that itself faults falls back to the rebuild path below.
    /// </para>
    /// <para>
    /// <b>Rebuild path.</b> A genuine source change (or a replay fallback) tears
    /// down the finished item and starts the next playable one, skipping and
    /// reporting items that fail to open/start, and bubbling a fatal error only when
    /// <see cref="PlaylistFailureGuard"/> gives up.
    /// </para>
    /// <para>
    /// <b>Following the controller (#182).</b> The next item is played only while the
    /// controller is playing. While it is paused, the next item is opened and warmed and
    /// stays paused until <see cref="PlayAsync"/>. A same-source replay rewinds in place only
    /// while playing and only an item that has played, because the in-place rewind is built
    /// for a loop reached while playing; otherwise it rebuilds. When the queue runs out while
    /// paused, the end-of-stream takes the controller from <c>Paused</c> to <c>Ended</c>.
    /// </para>
    /// <para>
    /// <b>The end of the queue (#170).</b> An item that ends the queue without failing is
    /// kept, not disposed, so at <c>Ended</c> it can be sought and played again and its
    /// counters read, as a single source's can. A skipped item is paused first, so it does
    /// not go on presenting. A faulted item is disposed as before.
    /// </para>
    /// </remarks>
    private async ValueTask AdvanceLockedAsync(ItemEnding how)
    {
        await AdvanceOnceLockedAsync(how).ConfigureAwait(false);

        // A jump recorded while that advance was under way is taken now, before the gate is
        // released, so the item the advance started does not play through (#171). A jump
        // recorded after this check is taken by the advance its own request queues.
        while (
            !_disposed
            && !_gaveUp
            && CurrentRunState is RunState.Playing or RunState.Paused
            && _coordinator.HasPendingJump
        )
        {
            await AdvanceOnceLockedAsync(ItemEnding.Skip).ConfigureAwait(false);
        }
    }

    private async ValueTask AdvanceOnceLockedAsync(ItemEnding how)
    {
        // A fault and a failed start both leave a runtime that is not replayed in place or kept.
        var failed = how is ItemEnding.Fault or ItemEnding.FailedStart;

        // An item that reached its end or was skipped ended without failing.
        if (!failed)
            Failures.ItemEnded();

        var playing = CurrentRunState == RunState.Playing;

        // Decide what plays next BEFORE any teardown, so a same-source replay can
        // reuse the live runtime instead of rebuilding it.
        var decision = _coordinator.DecideNext(ToAdvance(how));

        if (
            playing
            && _currentPlayed
            && !failed
            && _current is not null
            && decision.Kind == PlaylistCoordinator.NextKind.Replay
            && await TryReplayCurrentLockedAsync(decision).ConfigureAwait(false)
        )
        {
            return;
        }

        if (
            !failed
            && _current is not null
            && decision.Kind == PlaylistCoordinator.NextKind.End
        )
        {
            // Only a skip needs the pause. An item that reached its end has stopped, and
            // pausing it would hold back the audio still queued on the device. An item that has
            // not played has not opened its gates. Pausing an item that is already paused
            // changes nothing.
            await EndQueueKeepingCurrentLockedAsync(pause: how == ItemEnding.Skip && _currentPlayed)
                .ConfigureAwait(false);
            return;
        }

        // Tear down the item that just ended/faulted/was-skipped (stops its graph +
        // deactivates audio; never disposes the sinks).
        var old = _current;
        _current = null;
        if (old is not null)
            await SafeDisposeAsync(old).ConfigureAwait(false);

        // Rebase the position clock so the next item plays 0 → duration. The next
        // item's first PlayAsync calls _clock.Start(Position == 0).
        _clock.Stop();

        // Seed the skip loop with the decision already taken above; re-decide only
        // when an item fails to start.
        var pending = decision;
        while (!_disposed)
        {
            if (pending.Kind == PlaylistCoordinator.NextKind.End)
            {
                _currentItem = null;
                SetRunState(RunState.Ended);
                _controllerCallbacks.OnEndOfStream();
                return;
            }

            var nextItem = pending.Item!;
            var gen = ++_currentGen; // invalidates the outgoing item's callbacks.
            var session = CreateItemSession(gen);

            try
            {
                await session.InitializeAsync(nextItem.Source).ConfigureAwait(false);
                _current = session;
                _currentItem = nextItem;
                _currentPlayed = false;
                _currentAwaitsPlay = !playing;
                await session.WarmUpAsync().ConfigureAwait(false);
                if (playing)
                {
                    await session.PlayAsync().ConfigureAwait(false);
                    _currentPlayed = true;
                }
            }
            catch (Exception ex)
            {
                _current = null;
                await SafeDisposeAsync(session).ConfigureAwait(false);
                LogItemSkipped(_logger, nextItem.Source.DisplayName, ex);
                ReportItemFailure(nextItem.Source.DisplayName, "could not be started", ex);

                if (Failures.ItemFailed(playedFor: TimeSpan.Zero, itemLength: TimeSpan.Zero))
                {
                    GiveUp(ex);
                    return;
                }

                // The coordinator moved past the failed item when it was taken, so this takes
                // the one after it, under every repeat mode.
                pending = _coordinator.DecideNext(PlaylistAdvance.FailedStart);
                continue;
            }

            // A successful start does not reset the failure count; see PlaylistFailureGuard.
            // The controller's Duration and MediaInfo follow the new item. Reported under the
            // gate, before the transition, so the controller keeps the latest in hand-off
            // order and a subscriber to the transition can wait on the controller. An
            // in-place replay keeps the same item and reports nothing.
            _controllerCallbacks.OnCurrentItemChanged(session.MediaInfo);
            _coordinator.ReportCurrent(nextItem, session.MediaInfo, pending.Wrapped);
            return;
        }
    }

    private static PlaylistAdvance ToAdvance(ItemEnding how) =>
        how switch
        {
            ItemEnding.EndOfStream => PlaylistAdvance.EndOfStream,
            ItemEnding.Skip => PlaylistAdvance.Skip,
            ItemEnding.Fault => PlaylistAdvance.Fault,
            _ => PlaylistAdvance.FailedStart,
        };

    /// <summary>
    /// Handles <see cref="_current"/> failing to start when <see cref="PlayAsync"/> starts an
    /// item an advance opened while paused: reports it, counts it, and advances past it, as
    /// the advance does for an item that fails to start inside it. Caller must hold
    /// <see cref="_transitionGate"/>.
    /// </summary>
    private async ValueTask ItemFailedToStartLockedAsync(Exception error)
    {
        var source = _currentItem?.Source.DisplayName ?? "(unknown)";
        LogItemSkipped(_logger, source, error);
        ReportItemFailure(source, "could not be started", error);

        if (Failures.ItemFailed(playedFor: TimeSpan.Zero, itemLength: TimeSpan.Zero))
        {
            GiveUp(error);
            return;
        }

        await AdvanceLockedAsync(ItemEnding.FailedStart).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the queue and keeps <see cref="_current"/>, pausing it first when
    /// <paramref name="pause"/> is set. Caller must hold <see cref="_transitionGate"/>.
    /// </summary>
    /// <remarks>
    /// The pause goes to the item itself, which pauses its workers, clock and audio sink.
    /// <see cref="PauseAsync"/> would wait on the gate the caller holds. If the pause fails,
    /// the item is disposed instead, as it was before items were kept, so it cannot go on
    /// presenting while the controller says <c>Ended</c>.
    /// </remarks>
    private async ValueTask EndQueueKeepingCurrentLockedAsync(bool pause)
    {
        if (pause)
        {
            try
            {
                await _current!.PauseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogKeptItemPauseFailed(_logger, _currentItem?.Source.DisplayName ?? "(unknown)", ex);
                var current = _current!;
                _current = null;
                _currentItem = null;
                await SafeDisposeAsync(current).ConfigureAwait(false);
            }
        }

        SetRunState(RunState.Ended);
        _controllerCallbacks.OnEndOfStream();
    }

    /// <summary>
    /// Reports a failed item to the controller, which raises it on <c>ErrorOccurred</c>
    /// and stays in its current state.
    /// </summary>
    private void ReportItemFailure(string source, string what, Exception? error) =>
        _controllerCallbacks.OnRecoverableError(
            new PlaybackError(
                ErrorCategory.System,
                $"Playlist item '{source}' {what}: {error?.Message}",
                error
            )
        );

    /// <summary>
    /// Stops advancing and hands the controller a fatal error, which puts it in
    /// <c>Error</c> and disposes this session.
    /// </summary>
    private void GiveUp(Exception? last)
    {
        _gaveUp = true;
        _controllerCallbacks.OnWorkerFaulted(GiveUpException(last));
    }

    private InvalidOperationException GiveUpException(Exception? last) =>
        new(
            $"Playlist advance gave up after {Failures.ConsecutiveFailures} "
                + "consecutive item failures.",
            last
        );

    /// <summary>
    /// Reuses the live item runtime for a same-source boundary via the cheap
    /// in-place rewind — no teardown, no decode-device change, no presenter rebind,
    /// so the loop seam is gapless. Returns <see langword="true"/> on success;
    /// returns <see langword="false"/> (leaving the runtime intact) if the rewind
    /// faults, so the caller falls back to a full rebuild of the same source.
    /// </summary>
    private async ValueTask<bool> TryReplayCurrentLockedAsync(
        PlaylistCoordinator.NextDecision decision
    )
    {
        var current = _current!;
        try
        {
            // RewindToStartAsync reseats BOTH the position clock and the master
            // pacing clock to zero and re-runs the retained graph on the same decode
            // device — the same primitive the controller uses for a single-source
            // RepeatMode.One loop. The next item plays the same source object, so the
            // open demuxer, decoders, and warm presenter binding all carry over.
            await current.RewindToStartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReplayFellBack(_logger, _currentItem?.Source.DisplayName ?? "(unknown)", ex);
            return false;
        }

        // The item may be a different item of the same source, such as a back-to-back duplicate.
        _currentItem = decision.Item;
        _coordinator.ReportCurrent(decision.Item!, current.MediaInfo, decision.Wrapped);
        return true;
    }

    private async ValueTask SafeDisposeAsync(SubstrateSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogItemDisposeFailed(_logger, ex);
        }
    }

    // ── Logging ─────────────────────────────────────────────────────────────

    private static readonly Action<ILogger, string, Exception?> LogItemFaultedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogItemFaulted)),
            "Playlist item '{Source}' faulted during playback; advancing."
        );

    private static readonly Action<ILogger, string, Exception?> LogItemSkippedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogItemSkipped)),
            "Playlist item '{Source}' could not be started; skipping."
        );

    private static readonly Action<ILogger, Exception?> LogItemDisposeFailedMessage =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(LogItemDisposeFailed)),
            "Disposing a finished playlist item runtime threw."
        );

    private static readonly Action<ILogger, string, Exception?> LogReplayFellBackMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogReplayFellBack)),
            "In-place rewind of same-source playlist item '{Source}' faulted; "
                + "falling back to a full rebuild of the item."
        );

    private static readonly Action<ILogger, bool, Exception?> LogAdvanceIgnoredAtEndMessage =
        LoggerMessage.Define<bool>(
            LogLevel.Debug,
            new EventId(5, nameof(LogAdvanceIgnoredAtEnd)),
            "Playlist advance ignored: the queue has ended (faulted: {Faulted})."
        );

    private static readonly Action<ILogger, int, int, Exception?> LogStaleEndOfStreamMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(6, nameof(LogStaleEndOfStream)),
            "Playlist end-of-stream ignored: raised by run {Run}, and a seek or rewind has "
                + "since started run {CurrentRun}."
        );

    private static readonly Action<ILogger, string, Exception?> LogKeptItemPauseFailedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(7, nameof(LogKeptItemPauseFailed)),
            "Pausing skipped playlist item '{Source}' at the end of the queue threw; "
                + "disposing it instead."
        );

    private static void LogStaleEndOfStream(ILogger logger, int run, int currentRun) =>
        LogStaleEndOfStreamMessage(logger, run, currentRun, null);

    private static void LogKeptItemPauseFailed(ILogger logger, string source, Exception? error) =>
        LogKeptItemPauseFailedMessage(logger, source, error);

    private static void LogItemFaulted(ILogger logger, string source, Exception? error) =>
        LogItemFaultedMessage(logger, source, error);

    private static void LogItemSkipped(ILogger logger, string source, Exception? error) =>
        LogItemSkippedMessage(logger, source, error);

    private static void LogItemDisposeFailed(ILogger logger, Exception? error) =>
        LogItemDisposeFailedMessage(logger, error);

    private static void LogReplayFellBack(ILogger logger, string source, Exception? error) =>
        LogReplayFellBackMessage(logger, source, error);

    private static void LogAdvanceIgnoredAtEnd(ILogger logger, bool faulted) =>
        LogAdvanceIgnoredAtEndMessage(logger, faulted, null);
}
