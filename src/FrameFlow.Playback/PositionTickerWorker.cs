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
    /// <summary>The sampling cadence. Public so a test can advance a fake clock by exactly one.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly IPlaybackClock _clock;
    private readonly PlaybackSubject<TimeSpan> _positionTickSubject;
    private readonly TimeProvider _timeProvider;
    private readonly Action? _onTickProcessed;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new <see cref="PositionTickerWorker"/> instance.
    /// </summary>
    /// <param name="clock">The playback clock to sample position from.</param>
    /// <param name="positionTickSubject">The subject to push position updates to.</param>
    /// <param name="timeProvider">
    /// Drives the tick cadence. A test advances a fake one to produce ticks on demand; without
    /// this the loop can only be driven by real elapsed time, which no test here may wait on.
    /// </param>
    /// <param name="onTickProcessed">
    /// Raised after a tick's observers have all run and before this worker waits for the next one.
    /// It exists so a test driving a fake clock can advance one interval at a time and know the
    /// worker is ready for the next: any signal raised from inside the notification instead can
    /// release a caller while observers are still running.
    /// </param>
    /// <param name="logger">Optional logger for structured diagnostics.</param>
    public PositionTickerWorker(
        IPlaybackClock clock,
        PlaybackSubject<TimeSpan> positionTickSubject,
        TimeProvider timeProvider,
        Action? onTickProcessed = null,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(positionTickSubject);

        _clock = clock;
        _positionTickSubject = positionTickSubject;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onTickProcessed = onTickProcessed;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogTickerLoopStarted(_logger);

        try
        {
            using var timer = new PeriodicTimer(TickInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _positionTickSubject.OnNext(_clock.Position);

                // OnNext has returned, so every observer of this tick — the loop-stall fold
                // included — has finished with it, and the next statement is the wait.
                _onTickProcessed?.Invoke();
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
