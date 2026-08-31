// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Player;

/// <summary>
/// Concrete <see cref="IMediaPlayer"/> built by
/// <see cref="PlayerBuilder.BuildAsync"/>. Wraps an
/// <see cref="IPlaybackController"/> and projects its observables to
/// the simpler <see cref="IMediaPlayer"/> shape.
/// </summary>
internal sealed class MediaPlayerCore : IMediaPlayer
{
    private readonly IPlaybackController _controller;
    private readonly IAudioSink? _audioSink;
    private readonly IServiceProvider? _ownedProvider;
    private readonly ILogger _logger;

    private readonly ProjectionObservable<
        StateTransition<PlaybackState>,
        PlaybackState
    > _stateChanged;

    internal MediaPlayerCore(
        IPlaybackController controller,
        IAudioSink? audioSink,
        IServiceProvider? ownedProvider,
        ILogger logger
    )
    {
        _controller = controller;
        _audioSink = audioSink;
        _volumeControl = audioSink as IVolumeControl;
        _ownedProvider = ownedProvider;
        _logger = logger;

        _stateChanged = new ProjectionObservable<StateTransition<PlaybackState>, PlaybackState>(
            controller.PlaybackStateChanged,
            t => t.Current
        );
    }

    public PlaybackState State => _controller.State;
    public TimeSpan Position => _controller.Position;
    public TimeSpan Duration => _controller.Duration;
    public MediaInfo MediaInfo =>
        _controller.MediaInfo
        ?? throw new InvalidOperationException(
            "MediaInfo is not yet available — the player hasn't finished loading."
        );

    public IObservable<PlaybackState> StateChanged => _stateChanged;
    public IObservable<TimeSpan> PositionChanged => _controller.PositionTick;
    public IObservable<LoopStalled> LoopStalled => _controller.LoopStalled;
    public IObservable<PlaybackDiagnosticsSnapshot> Diagnostics => DiagnosticsObservable.Instance;

    public PlaybackDiagnosticsSnapshot PollDiagnostics() => _controller.GetDiagnostics();

    // Volume / mute forward to the sink only when it implements
    // IVolumeControl. When it does not, the writes are kept here so the
    // getters round-trip: a UI reading the value back to render a slider
    // position or a speaker glyph shows what the user chose rather than a
    // value they never set. Callers that want to know whether the write
    // reaches a real gain stage read SupportsVolumeControl.
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

    /// <inheritdoc/>
    public bool SupportsVolumeControl => _volumeControl is not null;

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        var result = await _controller.PlayAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, nameof(PlayAsync));
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        var result = await _controller.PauseAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, nameof(PauseAsync));
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        var result = await _controller.SeekAsync(position, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, nameof(SeekAsync));
    }

    public async Task SetRepeatModeAsync(
        RepeatMode mode,
        CancellationToken cancellationToken = default
    )
    {
        var result = await _controller
            .SetRepeatModeAsync(mode, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfFailed(result, nameof(SetRepeatModeAsync));
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaPlayer controller dispose threw.");
        }

        if (_ownedProvider is IAsyncDisposable a)
        {
            try
            {
                await a.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MediaPlayer service provider dispose threw.");
            }
        }
        else if (_ownedProvider is IDisposable d)
        {
            try
            {
                d.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MediaPlayer service provider dispose threw.");
            }
        }
    }

    /// <summary>
    /// Empty diagnostics observable — Phase-1 implementation. The
    /// underlying <see cref="IPlaybackController"/> doesn't push
    /// diagnostics as an event stream today; consumers use
    /// <see cref="IMediaPlayer.PollDiagnostics"/> with a timer
    /// instead. A future revision can add a cadenced diagnostics
    /// pump driven by the controller's position ticker.
    /// </summary>
    private sealed class DiagnosticsObservable : IObservable<PlaybackDiagnosticsSnapshot>
    {
        public static readonly DiagnosticsObservable Instance = new();

        public IDisposable Subscribe(IObserver<PlaybackDiagnosticsSnapshot> observer) =>
            EmptySubscription.Instance;
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static readonly EmptySubscription Instance = new();

        public void Dispose() { }
    }
}

/// <summary>
/// Adapter that projects an <c>IObservable&lt;TSource&gt;</c> into
/// an <c>IObservable&lt;TTarget&gt;</c> via a selector. Replaces the
/// equivalent <c>System.Reactive.Linq.Observable.Select</c> call
/// without pulling in the package — keeps the dependency surface
/// small for FrameFlow consumers.
/// </summary>
internal sealed class ProjectionObservable<TSource, TTarget> : IObservable<TTarget>
{
    private readonly IObservable<TSource> _source;
    private readonly Func<TSource, TTarget> _selector;

    public ProjectionObservable(IObservable<TSource> source, Func<TSource, TTarget> selector)
    {
        _source = source;
        _selector = selector;
    }

    public IDisposable Subscribe(IObserver<TTarget> observer) =>
        _source.Subscribe(new ProjectingObserver(observer, _selector));

    private sealed class ProjectingObserver(
        IObserver<TTarget> downstream,
        Func<TSource, TTarget> selector
    ) : IObserver<TSource>
    {
        public void OnCompleted() => downstream.OnCompleted();

        public void OnError(Exception error) => downstream.OnError(error);

        public void OnNext(TSource value) => downstream.OnNext(selector(value));
    }
}
