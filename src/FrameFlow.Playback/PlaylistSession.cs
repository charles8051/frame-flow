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
/// <b>Robustness.</b> An item that fails to open or start is skipped (logged),
/// so a single corrupt file does not kill the rotation. A spin guard bubbles a
/// fatal error if too many consecutive items fail, rather than looping hot over
/// an all-bad queue.
/// </para>
/// </remarks>
internal sealed class PlaylistSession : IPlaybackSession
{
    /// <summary>
    /// Consecutive open/start failures tolerated before a fatal error is bubbled
    /// to the controller. Bounds a hot spin loop when every queued item is bad.
    /// </summary>
    private const int MaxConsecutiveFailures = 8;

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
    private int _consecutiveFailures;
    private bool _disposed;

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

        // Wire skip: poking the coordinator schedules an advance tagged with the
        // current generation, so a skip and a natural end-of-stream that race
        // collapse to a single advance via the gen check under the gate.
        _coordinator.AttachSkipHandler(() => OnItemEnded(_currentGen, faulted: false, error: null));

        _coordinator.ReportCurrent(first, session.MediaInfo, wrapped: false);
    }

    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
        _current?.WarmUpAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            // A pending skip request taking effect at the moment of (re)play.
            if (_coordinator.ConsumeSkipRequest())
            {
                await AdvanceLockedAsync(faulted: false).ConfigureAwait(false);
                return;
            }

            if (_current is not null)
                await _current.PlayAsync(cancellationToken).ConfigureAwait(false);
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
            if (_disposed || _current is null)
                return;
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
    /// buffer callbacks bubble straight to the controller; end-of-stream and
    /// faults route into the advance path, tagged with the item's generation so
    /// a stale notification from a replaced item is ignored.
    /// </summary>
    private SessionCallbacks CreateItemCallbacks(int gen) =>
        new(
            OnEndOfStream: () => OnItemEnded(gen, faulted: false, error: null),
            OnWorkerFaulted: ex => OnItemEnded(gen, faulted: true, error: ex),
            OnBufferReady: _controllerCallbacks.OnBufferReady,
            OnBufferUnderrun: _controllerCallbacks.OnBufferUnderrun
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

    private void OnItemEnded(int gen, bool faulted, Exception? error)
    {
        if (_disposed)
            return;

        // Hop off the worker thread that raised the callback; the advance does
        // heavy work (dispose + open + warmup) that must not block the graph.
        _ = Task.Run(() => AdvanceAsync(gen, faulted, error));
    }

    private async Task AdvanceAsync(int gen, bool faulted, Exception? error)
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || gen != _currentGen)
                return; // stale notification from an already-replaced item.

            if (faulted)
            {
                _consecutiveFailures++;
                LogItemFaulted(_logger, _currentSource?.DisplayName ?? "(unknown)", error);
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
    /// down the finished item and starts the next playable one, skipping items that
    /// fail to open/start and bubbling a fatal error only after too many consecutive
    /// failures.
    /// </para>
    /// </remarks>
    private async ValueTask AdvanceLockedAsync(bool faulted)
    {
        // Decide what plays next BEFORE any teardown, so a same-source replay can
        // reuse the live runtime instead of rebuilding it.
        var decision = _coordinator.DecideNext(_currentSource);

        if (
            !faulted
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
                await session.WarmUpAsync().ConfigureAwait(false);
                await session.PlayAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _current = null;
                await SafeDisposeAsync(session).ConfigureAwait(false);
                _consecutiveFailures++;
                LogItemSkipped(_logger, nextSource.DisplayName, ex);

                if (_consecutiveFailures > MaxConsecutiveFailures)
                {
                    _controllerCallbacks.OnWorkerFaulted(
                        new InvalidOperationException(
                            $"Playlist advance gave up after {_consecutiveFailures} "
                                + "consecutive item failures.",
                            ex
                        )
                    );
                    return;
                }

                pending = _coordinator.DecideNext(_currentSource);
                continue; // skip the bad item, try the next one.
            }

            _consecutiveFailures = 0;
            _coordinator.ReportCurrent(nextSource, session.MediaInfo, pending.Wrapped);
            return;
        }
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

        _consecutiveFailures = 0;
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

    private static void LogItemFaulted(ILogger logger, string source, Exception? error) =>
        LogItemFaultedMessage(logger, source, error);

    private static void LogItemSkipped(ILogger logger, string source, Exception? error) =>
        LogItemSkippedMessage(logger, source, error);

    private static void LogItemDisposeFailed(ILogger logger, Exception? error) =>
        LogItemDisposeFailedMessage(logger, error);

    private static void LogReplayFellBack(ILogger logger, string source, Exception? error) =>
        LogReplayFellBackMessage(logger, source, error);
}
