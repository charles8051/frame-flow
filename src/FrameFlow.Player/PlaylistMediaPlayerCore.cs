// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Player;

/// <summary>
/// Concrete <see cref="IMediaPlayer"/> built by
/// <see cref="FrameFlowPlayer"/>. Wraps an
/// <see cref="IPlaybackController"/> (driving one warm <c>PlaylistSession</c>)
/// and a shared <see cref="PlaylistCoordinator"/>, projecting both to the
/// playlist player surface.
/// </summary>
/// <remarks>
/// Transport + state come from the controller; the playlist facets (queue,
/// current source, transitions, per-item <see cref="MediaInfo"/> /
/// <see cref="Duration"/>) come from the coordinator. Reading current-item
/// metadata from the coordinator — rather than the controller's load-time
/// snapshot — is what lets the metadata follow the playlist without disturbing
/// the controller's "immutable loaded snapshot" model.
/// </remarks>
internal sealed class PlaylistMediaPlayerCore : IMediaPlayer
{
    private readonly IPlaybackController _controller;
    private readonly PlaylistCoordinator _coordinator;
    private readonly IAudioSink? _audioSink;
    private readonly ILogger _logger;

    private readonly ProjectionObservable<
        StateTransition<PlaybackState>,
        PlaybackState
    > _stateChanged;

    internal PlaylistMediaPlayerCore(
        IPlaybackController controller,
        PlaylistCoordinator coordinator,
        IAudioSink? audioSink,
        ILogger logger
    )
    {
        _controller = controller;
        _coordinator = coordinator;
        _audioSink = audioSink;
        _volumeControl = audioSink as IVolumeControl;
        _logger = logger;

        _stateChanged = new ProjectionObservable<StateTransition<PlaybackState>, PlaybackState>(
            controller.PlaybackStateChanged,
            t => t.Current
        );
    }

    // ── IMediaTransport: state ─────────────────────────────────────────────────

    public PlaybackState State => _controller.State;
    public TimeSpan Position => _controller.Position;
    public TimeSpan Duration => _coordinator.CurrentDuration;

    // Null while nothing is loaded, matching IPlaybackController.MediaInfo. It used to throw,
    // which made a reachable state look like a caller error and cost FrameFlowStreamSummary a
    // bare catch around a property read.
    public MediaInfo? MediaInfo => _coordinator.CurrentMediaInfo;

    public IObservable<PlaybackState> StateChanged => _stateChanged;
    public IObservable<TimeSpan> PositionTick => _controller.PositionTick;
    public IObservable<LoopRestarted> LoopRestarted => _controller.LoopRestarted;
    public IObservable<LoopStalled> LoopStalled => _controller.LoopStalled;
    public IObservable<PlaybackError> ErrorOccurred => _controller.ErrorOccurred;

    public PlaybackDiagnosticsSnapshot GetDiagnostics() => _controller.GetDiagnostics();

    // Same shape as MediaPlayerCore: forward to the sink when it implements
    // IVolumeControl, otherwise keep the write locally so the getters
    // round-trip for UI that reads the value back.
    private readonly IVolumeControl? _volumeControl;
    private float _detachedVolume = 1.0f;

    // Validation is the sink's contract, not the player's, but a write that
    // never reaches a sink still has to honour it — otherwise IMediaTransport.Volume
    // becomes the one path that can read back NaN. Mirrors the guard in
    // IVolumeControl implementations (ADR-0065).
    private static float ValidatedVolume(float value) =>
        float.IsNaN(value) || value < 0f
            ? throw new ArgumentOutOfRangeException(
                nameof(value),
                "Volume must be a non-negative, non-NaN float."
            )
            : value;

    private bool _detachedMuted;

    public bool SupportsVolumeControl => _volumeControl is not null;

    public float Volume
    {
        get => _volumeControl?.Volume ?? _detachedVolume;
        set
        {
            if (_volumeControl is not null)
                _volumeControl.Volume = value;
            else
                _detachedVolume = ValidatedVolume(value);
        }
    }

    public bool Muted
    {
        get => _volumeControl?.Muted ?? _detachedMuted;
        set
        {
            if (_volumeControl is not null)
                _volumeControl.Muted = value;
            else
                _detachedMuted = value;
        }
    }

    // ── IMediaTransport: transport ─────────────────────────────────────────────

    // ADR-0069: pass-throughs. The controller already answers in Result.
    public Task<Result> PlayAsync(CancellationToken cancellationToken = default) =>
        _controller.PlayAsync(cancellationToken);

    public Task<Result> PauseAsync(CancellationToken cancellationToken = default) =>
        _controller.PauseAsync(cancellationToken);

    public Task<Result> SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default
    ) => _controller.SeekAsync(position, cancellationToken);

    public async Task<Result> SetRepeatModeAsync(
        RepeatMode mode,
        CancellationToken cancellationToken = default
    )
    {
        // The coordinator owns the loop behavior; the controller mirrors it so
        // RepeatMode reporting stays consistent. The controller goes first: it
        // is the one that can refuse, and a coordinator that adopted the mode
        // anyway would run the playlist on a setting the caller was told
        // failed. Under the old exception model this was the same bug, hidden
        // by the throw unwinding past the assignment.
        var result = await _controller
            .SetRepeatModeAsync(mode, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
            _coordinator.RepeatMode = mode;
        return result;
    }

    // ── IMediaPlayer ────────────────────────────────────────────────

    public IMediaSource? CurrentSource => _coordinator.CurrentSource;

    public IObservable<PlaylistTransition> SourceTransitioned => _coordinator.SourceTransitioned;

    public IObservable<PlaylistSnapshot> PlaylistChanged => _coordinator.PlaylistChanged;

    // Both come from the controller rather than the coordinator: the ordering against
    // ErrorOccurred / LoopRestarted, and the drop of a report from a superseded session, are
    // properties of the controller's dispatch loop, and a coordinator-side raise would have
    // neither.
    public IObservable<PlaylistItemFailed> ItemFailed => _controller.ItemFailed;

    public IObservable<PlaylistItemLooped> ItemLooped => _controller.ItemLooped;

    public IObservable<PlaylistItemStalled> ItemStalled => _controller.ItemStalled;

    public Task<PlaylistItem> EnqueueAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        return Task.FromResult(_coordinator.Enqueue(source));
    }

    public Task<PlaylistItem?> SetNextAsync(
        IMediaSource? source,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(_coordinator.SetNext(source));

    public Task<PlaylistItem> AddAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        return Task.FromResult(_coordinator.Add(source));
    }

    public PlaylistSnapshot GetPlaylist() => _coordinator.Snapshot();

    public Task SkipToNextAsync(CancellationToken cancellationToken = default)
    {
        _coordinator.RequestSkip();
        return Task.CompletedTask;
    }

    public Task<Result> JumpToAsync(PlaylistItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (RefusalWhileUnusable("jump") is { } refused)
            return Task.FromResult(Result.Fail(refused));

        return Task.FromResult(
            _coordinator.RequestJump(item) switch
            {
                JumpRequest.NotInPlayer => Result.Fail(
                    ErrorCategory.InvalidOperation,
                    $"Cannot jump to '{item}': it is not in the player."
                ),
                _ => Result.Ok(),
            }
        );
    }

    public Task<Result> RemoveAsync(PlaylistItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.FromResult(
            _coordinator.Remove(item)
                ? Result.Ok()
                : Result.Fail(
                    ErrorCategory.InvalidOperation,
                    $"Cannot remove '{item}': it is not in the player."
                )
        );
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _coordinator.Clear();
        return Task.CompletedTask;
    }

    public Task<Result<IReadOnlyList<PlaylistItem>>> ReplaceAsync(
        IEnumerable<IMediaSource> sources,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToList();
        if (list.Count == 0)
            throw new ArgumentException(
                "A replacement playlist requires at least one source.",
                nameof(sources)
            );

        if (RefusalWhileUnusable("replace the playlist") is { } refused)
            return Task.FromResult(Result<IReadOnlyList<PlaylistItem>>.Fail(refused));

        return Task.FromResult(
            Result<IReadOnlyList<PlaylistItem>>.Ok(_coordinator.Replace(list))
        );
    }

    // A jump or replace needs a player that can still play. In Error the player accepts no
    // further playback, and once disposed there is no session to take the jump.
    //
    // Returns the error rather than a refusing Result so that both callers can wrap it in
    // whichever Result shape they return. A Result? would carry the refusal in a type whose
    // Error is nullable, and the generic caller had to suppress that with a `!`.
    private PlaybackError? RefusalWhileUnusable(string what)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return new PlaybackError(
                ErrorCategory.InvalidOperation,
                $"Cannot {what}: the player is disposed."
            );
        if (_controller.State == PlaybackState.Error)
            return new PlaybackError(
                ErrorCategory.InvalidOperation,
                $"Cannot {what}: the player is in Error."
            );
        return null;
    }

    // ── Lifetime ────────────────────────────────────────────────────────────

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        try
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playlist player controller dispose threw.");
        }

        _coordinator.Dispose();
    }
}
