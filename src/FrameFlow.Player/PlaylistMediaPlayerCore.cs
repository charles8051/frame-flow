// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Player;

/// <summary>
/// Concrete <see cref="IMediaPlaylistPlayer"/> built by
/// <see cref="MediaPlaylistPlayer.CreateAsync"/>. Wraps an
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
internal sealed class PlaylistMediaPlayerCore : IMediaPlaylistPlayer
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

    // ── IMediaPlayer: state ─────────────────────────────────────────────────

    public PlaybackState State => _controller.State;
    public TimeSpan Position => _controller.Position;
    public TimeSpan Duration => _coordinator.CurrentDuration;

    public MediaInfo MediaInfo =>
        _coordinator.CurrentMediaInfo
        ?? throw new InvalidOperationException(
            "MediaInfo is not yet available — the playlist hasn't loaded its first item."
        );

    public IObservable<PlaybackState> StateChanged => _stateChanged;
    public IObservable<TimeSpan> PositionChanged => _controller.PositionTick;
    public IObservable<LoopStalled> LoopStalled => _controller.LoopStalled;
    public IObservable<PlaybackDiagnosticsSnapshot> Diagnostics => EmptyDiagnostics.Instance;

    public PlaybackDiagnosticsSnapshot PollDiagnostics() => _controller.GetDiagnostics();

    // Same shape as MediaPlayerCore: forward to the sink when it implements
    // IVolumeControl, otherwise keep the write locally so the getters
    // round-trip for UI that reads the value back.
    private readonly IVolumeControl? _volumeControl;
    private float _detachedVolume = 1.0f;

    // Validation is the sink's contract, not the player's, but a write that
    // never reaches a sink still has to honour it — otherwise IMediaPlayer.Volume
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

    // ── IMediaPlayer: transport ─────────────────────────────────────────────

    public async Task PlayAsync(CancellationToken cancellationToken = default) =>
        ThrowIfFailed(
            await _controller.PlayAsync(cancellationToken).ConfigureAwait(false),
            nameof(PlayAsync)
        );

    public async Task PauseAsync(CancellationToken cancellationToken = default) =>
        ThrowIfFailed(
            await _controller.PauseAsync(cancellationToken).ConfigureAwait(false),
            nameof(PauseAsync)
        );

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        ThrowIfFailed(
            await _controller.SeekAsync(position, cancellationToken).ConfigureAwait(false),
            nameof(SeekAsync)
        );

    public async Task SetRepeatModeAsync(
        RepeatMode mode,
        CancellationToken cancellationToken = default
    )
    {
        // The coordinator owns the loop behavior; the controller mirrors it so
        // RepeatMode reporting stays consistent.
        _coordinator.RepeatMode = mode;
        ThrowIfFailed(
            await _controller.SetRepeatModeAsync(mode, cancellationToken).ConfigureAwait(false),
            nameof(SetRepeatModeAsync)
        );
    }

    // ── IMediaPlaylistPlayer ────────────────────────────────────────────────

    public IMediaSource? CurrentSource => _coordinator.CurrentSource;

    public IObservable<PlaylistTransition> SourceTransitioned => _coordinator.SourceTransitioned;

    public Task EnqueueAsync(IMediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _coordinator.Enqueue(source);
        return Task.CompletedTask;
    }

    public Task SetNextAsync(IMediaSource? source, CancellationToken cancellationToken = default)
    {
        _coordinator.SetNext(source);
        return Task.CompletedTask;
    }

    public Task SkipToNextAsync(CancellationToken cancellationToken = default)
    {
        _coordinator.RequestSkip();
        return Task.CompletedTask;
    }

    // ── Lifetime ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
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

    private static void ThrowIfFailed(Result result, string op)
    {
        if (result.IsSuccess)
            return;
        var err = result.Error;
        throw new InvalidOperationException(
            $"{op} failed: {err?.Category} — {err?.Message}",
            err?.Inner
        );
    }

    private sealed class EmptyDiagnostics : IObservable<PlaybackDiagnosticsSnapshot>
    {
        public static readonly EmptyDiagnostics Instance = new();

        public IDisposable Subscribe(IObserver<PlaybackDiagnosticsSnapshot> observer) =>
            NoopSubscription.Instance;

        private sealed class NoopSubscription : IDisposable
        {
            public static readonly NoopSubscription Instance = new();

            public void Dispose() { }
        }
    }
}
