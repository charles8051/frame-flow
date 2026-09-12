// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Tracks playback position using a configurable <see cref="TimeProvider"/>.
/// The default constructor uses real wall-clock time; inject a
/// <c>FakeTimeProvider</c> for deterministic testing.
/// </summary>
/// <remarks>
/// State transitions:
/// <list type="bullet">
///   <item><description>Idle → Running: <see cref="Start"/></description></item>
///   <item><description>Running → Paused: <see cref="Pause"/></description></item>
///   <item><description>Paused → Running: <see cref="Resume"/></description></item>
///   <item><description>Any → Idle: <see cref="Stop"/></description></item>
///   <item><description>Running or Paused → repositioned: <see cref="Seek"/></description></item>
/// </list>
/// </remarks>
public sealed class PlaybackClock : IPlaybackClock
{
    private readonly TimeProvider _timeProvider;

    private DateTimeOffset? _startedAt;
    private TimeSpan _basePosition;
    private TimeSpan _pausedAt;
    private bool _isPaused;

    /// <summary>
    /// Initializes a new <see cref="PlaybackClock"/> using real wall-clock time.
    /// </summary>
    public PlaybackClock()
        : this(TimeProvider.System) { }

    /// <summary>
    /// Initializes a new <see cref="PlaybackClock"/> reading from <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="timeProvider">
    /// Supplies the current time. Inject a fake for deterministic tests.
    /// </param>
    public PlaybackClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public TimeSpan Position =>
        _startedAt is null ? _basePosition
        : _isPaused ? _pausedAt
        : _basePosition + (_timeProvider.GetUtcNow() - _startedAt.Value);

    /// <inheritdoc/>
    public bool IsRunning => _startedAt is not null && !_isPaused;

    /// <inheritdoc/>
    public bool IsPaused => _isPaused;

    /// <inheritdoc/>
    public void Start(TimeSpan startPosition)
    {
        _basePosition = startPosition;
        _startedAt = _timeProvider.GetUtcNow();
        _isPaused = false;
        _pausedAt = startPosition;
    }

    /// <inheritdoc/>
    public void Pause()
    {
        if (_startedAt is null || _isPaused)
        {
            return;
        }

        _pausedAt = Position;
        _isPaused = true;
    }

    /// <inheritdoc/>
    public void Resume()
    {
        if (!_isPaused)
        {
            return;
        }

        Start(_pausedAt);
    }

    /// <inheritdoc/>
    public void Seek(TimeSpan position)
    {
        if (_isPaused || _startedAt is null)
        {
            _basePosition = position;
            _pausedAt = position;
            return;
        }

        Start(position);
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _startedAt = null;
        _basePosition = TimeSpan.Zero;
        _pausedAt = TimeSpan.Zero;
        _isPaused = false;
    }
}
