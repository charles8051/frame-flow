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

    // The runtime for the item presenting right now.
    private SubstrateSession? _current;
    private IMediaSource? _currentSource;

    // Monotonic generation tag stamped into each item's callbacks so a stale
    // end-of-stream / fault from an already-replaced item is ignored.
    private int _currentGen;

    // The generation whose fault was last handled. A faulted item is never rewound
    // in place, so a second fault carrying it is another worker reporting the same
    // failure, and is not counted or reported again.
    private int _lastFaultedGen = -1;

    private readonly PlaylistFailureGuard _failures = new();

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
        /// The queue ran out and end-of-stream was reported. Nothing is current. Only a seek
        /// out of Ended leaves it: a Play or Pause the controller dispatched before it saw the
        /// end-of-stream must not replace it, because the controller ends when it does. A Play
        /// from Ended never reaches this session; the controller replays on a new one.
        /// </summary>
        Ended,
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

    // ── IPlaybackSession lifecycle ──────────────────────────────────────────

    public async ValueTask InitializeAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        // The controller hands us the first source; the coordinator is the
        // authority for the queue, so pop the first item from it (it is the same
        // object the controller was asked to load). Subsequent items are pulled
        // by the advance path.
        var first = _coordinator.First();
        var gen = _currentGen; // 0
        var session = CreateItemSession(gen);
        try
        {
            await session.InitializeAsync(first, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await SafeDisposeAsync(session).ConfigureAwait(false);
            throw; // first-item failure surfaces as a load failure, like single-source.
        }

        _current = session;
        _currentSource = first;
        _currentPlayed = false;
        _currentAwaitsPlay = false;

        _coordinator.AttachSkipHandler(OnSkipRequested);

        _coordinator.ReportCurrent(first, session.MediaInfo, wrapped: false);
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
            // At Ended nothing is current. This Play was dispatched before the controller saw
            // the end-of-stream that is on its way, and the controller ends when it does.
            if (_disposed || CurrentRunState == RunState.Ended)
                return;

            SetRunState(RunState.Playing);

            // A pending skip request taking effect at the moment of (re)play.
            if (_coordinator.ConsumeSkipRequest())
            {
                await AdvanceLockedAsync(faulted: false).ConfigureAwait(false);
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
    /// </summary>
    private SessionCallbacks CreateItemCallbacks(int gen) =>
        new(
            OnEndOfStream: () => OnItemEnded(gen, faulted: false, error: null),
            OnWorkerFaulted: ex => OnItemEnded(gen, faulted: true, error: ex),
            OnBufferReady: _controllerCallbacks.OnBufferReady,
            OnBufferUnderrun: _controllerCallbacks.OnBufferUnderrun,
            OnRecoverableError: _controllerCallbacks.OnRecoverableError
        );

    private SubstrateSession CreateItemSession(int gen) =>
        new(
            _videoSink,
            _audioSink,
            _clock,
            CreateItemCallbacks(gen),
            _hwMode,
            _hwCapabilities,
            _loggerFactory,
            _videoConfigurator,
            _audioConfigurator,
            _yieldHardwareFrames
        );

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
                // Nothing is current, so there is nothing to end. PlayAsync from Ended plays
                // whatever is queued.
                LogAdvanceIgnoredAtEnd(_logger, faulted: false);
                return;

            case RunState.NotStarted:
                // Nothing has played: the skip takes effect when the first PlayAsync does.
                _coordinator.LatchSkip();
                // That PlayAsync may have started between the read above and the latch, and
                // missed it. If it has and the latch is still set, take it back and advance.
                if (CurrentRunState == RunState.NotStarted || !_coordinator.ConsumeSkipRequest())
                    return;
                break;
        }

        // Tagged with the current generation, so a skip and a natural end-of-stream that race
        // collapse to a single advance via the gen check under the gate.
        OnItemEnded(_currentGen, faulted: false, error: null);
    }

    private void OnItemEnded(int gen, bool faulted, Exception? error)
    {
        if (_disposed)
            return;

        // How far a faulted item played is read here, when it faulted. The advance may
        // wait on the gate behind a seek or a slow SourceTransitioned subscriber, and the
        // position clock keeps running meanwhile. The clock starts from zero for each
        // item and each in-place rewind, and stops while paused.
        var playedFor = faulted ? _clock.Position : TimeSpan.Zero;

        // Hop off the worker thread that raised the callback; the advance does
        // heavy work (dispose + open + warmup) that must not block the graph.
        _ = Task.Run(() => AdvanceAsync(gen, faulted, error, playedFor));
    }

    private async Task AdvanceAsync(int gen, bool faulted, Exception? error, TimeSpan playedFor)
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _gaveUp || gen != _currentGen)
                return; // stale notification from an already-replaced item.

            switch (CurrentRunState)
            {
                case RunState.Ended:
                    // The item that ended the queue is gone, so a late notification from it
                    // has nothing to act on, and a skip that raced the end has nothing to
                    // end. Advancing here would start a queued item while the controller
                    // says Ended; PlayAsync from Ended plays it instead.
                    LogAdvanceIgnoredAtEnd(_logger, faulted);
                    return;

                case RunState.NotStarted when !faulted:
                    // Nothing has played. The advance waits for the first PlayAsync, as a
                    // skip issued before the playlist loaded does.
                    _coordinator.LatchSkip();
                    return;
            }

            if (faulted)
            {
                if (gen == _lastFaultedGen)
                    return;
                _lastFaultedGen = gen;

                var source = _currentSource?.DisplayName ?? "(unknown)";

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

                if (_failures.ItemFailed(playedFor, _current?.Duration ?? TimeSpan.Zero))
                {
                    GiveUp(error);
                    return;
                }
            }

            await AdvanceLockedAsync(faulted).ConfigureAwait(false);
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
    /// </remarks>
    private async ValueTask AdvanceLockedAsync(bool faulted)
    {
        // An item that reached its end or was skipped ended without failing.
        if (!faulted)
            _failures.ItemEnded();

        var playing = CurrentRunState == RunState.Playing;

        // Decide what plays next BEFORE any teardown, so a same-source replay can
        // reuse the live runtime instead of rebuilding it.
        var decision = _coordinator.DecideNext(_currentSource);

        if (
            playing
            && _currentPlayed
            && !faulted
            && _current is not null
            && decision.Kind == PlaylistCoordinator.NextKind.Replay
            && await TryReplayCurrentLockedAsync(decision).ConfigureAwait(false)
        )
        {
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
                _currentSource = null;
                SetRunState(RunState.Ended);
                _controllerCallbacks.OnEndOfStream();
                return;
            }

            var nextSource = pending.Source!;
            var gen = ++_currentGen; // invalidates the outgoing item's callbacks.
            var session = CreateItemSession(gen);

            try
            {
                await session.InitializeAsync(nextSource).ConfigureAwait(false);
                _current = session;
                _currentSource = nextSource;
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
                LogItemSkipped(_logger, nextSource.DisplayName, ex);
                ReportItemFailure(nextSource.DisplayName, "could not be started", ex);

                if (_failures.ItemFailed(playedFor: TimeSpan.Zero, itemLength: TimeSpan.Zero))
                {
                    GiveUp(ex);
                    return;
                }

                pending = _coordinator.DecideNext(_currentSource);
                continue; // skip the bad item, try the next one.
            }

            // A successful start does not reset the failure count; see PlaylistFailureGuard.
            _coordinator.ReportCurrent(nextSource, session.MediaInfo, pending.Wrapped);
            return;
        }
    }

    /// <summary>
    /// Handles <see cref="_current"/> failing to start when <see cref="PlayAsync"/> starts an
    /// item an advance opened while paused: reports it, counts it, and advances past it, as
    /// the advance does for an item that fails to start inside it. Caller must hold
    /// <see cref="_transitionGate"/>.
    /// </summary>
    private async ValueTask ItemFailedToStartLockedAsync(Exception error)
    {
        var source = _currentSource?.DisplayName ?? "(unknown)";
        LogItemSkipped(_logger, source, error);
        ReportItemFailure(source, "could not be started", error);

        if (_failures.ItemFailed(playedFor: TimeSpan.Zero, itemLength: TimeSpan.Zero))
        {
            GiveUp(error);
            return;
        }

        await AdvanceLockedAsync(faulted: true).ConfigureAwait(false);
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
        _controllerCallbacks.OnWorkerFaulted(
            new InvalidOperationException(
                $"Playlist advance gave up after {_failures.ConsecutiveFailures} "
                    + "consecutive item failures.",
                last
            )
        );
    }

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
            // RepeatMode.One loop. _currentSource is unchanged (same object), so the
            // open demuxer, decoders, and warm presenter binding all carry over.
            await current.RewindToStartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReplayFellBack(_logger, _currentSource?.DisplayName ?? "(unknown)", ex);
            return false;
        }

        _coordinator.ReportCurrent(decision.Source!, current.MediaInfo, decision.Wrapped);
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
