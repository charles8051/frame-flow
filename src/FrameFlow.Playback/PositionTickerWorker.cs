// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Position ticker worker that periodically samples the playback clock and
/// pushes position updates to the subject. Lifecycle bound to the Playing
/// state via <see cref="WorkerBinding{TWorker}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="PeriodicTimer"/> with a 250ms interval for efficient
/// CPU-friendly polling of the playback clock position.
/// </para>
/// <para>
/// The long-running loop itself is returned from <see cref="StartAsync"/> so
/// <see cref="WorkerBinding{TWorker}"/> can track cancellation, completion,
/// and faults for the active worker instance.
/// </para>
/// </remarks>
internal sealed partial class PositionTickerWorker : IStateBoundWorker
{
    private readonly IPlaybackClock _clock;
    private readonly PlaybackSubject<TimeSpan> _positionTickSubject;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new <see cref="PositionTickerWorker"/> instance.
    /// </summary>
    /// <param name="clock">The playback clock to sample position from.</param>
    /// <param name="positionTickSubject">The subject to push position updates to.</param>
    /// <param name="logger">Optional logger for structured diagnostics.</param>
    public PositionTickerWorker(
        IPlaybackClock clock,
        PlaybackSubject<TimeSpan> positionTickSubject,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(positionTickSubject);

        _clock = clock;
        _positionTickSubject = positionTickSubject;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogTickerLoopStarted(_logger);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _positionTickSubject.OnNext(_clock.Position);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop — normal shutdown.
        }
        catch (Exception ex)
        {
            LogTickerLoopError(_logger, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        LogTickerLoopStopped(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
