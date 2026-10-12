// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Player.Core;

namespace FrameFlow.Player;

/// <summary>
/// Waits built from what <see cref="IMediaTransport"/> already reports.
/// </summary>
public static class MediaTransportExtensions
{
    /// <summary>
    /// Completes when playback ends: with success when the player reaches
    /// <see cref="PlaybackState.Ended"/>, and with a failed <see cref="Result"/> when it enters
    /// <see cref="PlaybackState.Error"/> or is disposed first.
    /// <code>
    /// await player.PlayAsync();
    /// var ended = await player.WaitForEndAsync(ct);
    /// </code>
    /// </summary>
    /// <param name="transport">The player to watch.</param>
    /// <param name="cancellationToken">
    /// Stops waiting, with <see cref="OperationCanceledException"/>. Playback is not affected.
    /// </param>
    /// <returns>
    /// Success once the player is in <see cref="PlaybackState.Ended"/>. A failure carrying the
    /// <see cref="PlaybackError"/> the player raised on <see cref="IMediaTransport.ErrorOccurred"/>
    /// when it entered <see cref="PlaybackState.Error"/>. A failure in
    /// <see cref="ErrorCategory.InvalidOperation"/> when the player is disposed before it ends.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Ended means heard, for an item that plays to its end.</b> The player raises
    /// <see cref="PlaybackState.Ended"/> once the audio sink has played the last sample out, not
    /// once it has been handed the last buffer (<see cref="IAudioSink.DrainAsync"/>), so a caller
    /// that tears down here cuts nothing off.
    /// </para>
    /// <para>
    /// <b>Success does not mean every item played.</b> An item failure that the player carries on
    /// from is raised on <see cref="IMediaTransport.ErrorOccurred"/> while the state stays
    /// <see cref="PlaybackState.Playing"/>, and does not end the wait. That includes the last
    /// item: under <see cref="RepeatMode.Off"/>, a last item that faults while it plays ends the
    /// queue in <see cref="PlaybackState.Ended"/>, which follows the fault rather than a drain,
    /// and the wait reports success. A caller that needs to know whether anything failed
    /// subscribes to <see cref="IMediaTransport.ErrorOccurred"/>, or to
    /// <see cref="IMediaPlayer.ItemFailed"/>, before it plays.
    /// </para>
    /// <para>
    /// <b>Called late.</b> It subscribes before it reads <see cref="IMediaTransport.State"/>, so a
    /// player that is already in <see cref="PlaybackState.Ended"/> completes it at once, and one
    /// that ends while it is being set up is not missed. A player already in
    /// <see cref="PlaybackState.Error"/> fails it at once, but with a generic error: the one that
    /// put it there may already have been raised before the wait subscribed, and is not replayed.
    /// A disposed player fails it.
    /// </para>
    /// <para>
    /// <b>Repeat.</b> Under <see cref="RepeatMode.One"/> the current item loops, and under
    /// <see cref="RepeatMode.All"/> the queue wraps, so the player does not reach
    /// <see cref="PlaybackState.Ended"/> and the wait stays pending until the mode is set back to
    /// <see cref="RepeatMode.Off"/> and playback then ends, or until an error, disposal or
    /// cancellation. Under <see cref="RepeatMode.Off"/> a queue ends after its last item. There
    /// is no per-item wait: <see cref="IMediaPlayer.SourceTransitioned"/> and
    /// <see cref="IMediaTransport.LoopRestarted"/> report the end of each item.
    /// </para>
    /// </remarks>
    public static Task<Result> WaitForEndAsync(
        this IMediaTransport transport,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<Result>(cancellationToken);

        return new EndOfPlaybackWait(transport).RunAsync(cancellationToken);
    }

    /// <summary>
    /// The shell around <see cref="PlaybackEndWatch"/>: subscribes, reads the transport, and
    /// feeds each reading to the watch under one lock. Nothing is called on the transport while
    /// the lock is held, so a transport that raises its events under a lock of its own cannot
    /// deadlock against a reading taken here.
    /// </summary>
    private sealed class EndOfPlaybackWait(IMediaTransport transport)
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource<Result> _done = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private PlaybackEndWatch _watch = PlaybackEndWatch.Waiting;

        public async Task<Result> RunAsync(CancellationToken cancellationToken)
        {
            IDisposable? errors = null;
            IDisposable? states = null;
            try
            {
                try
                {
                    errors = transport.ErrorOccurred.Subscribe(
                        new Observer<PlaybackError>(
                            error =>
                            {
                                var stateNow = transport.State;
                                Apply(w => w.ErrorRaised(error, stateNow));
                            },
                            () => Apply(static w => w.Completed()),
                            fault => Apply(w => w.Faulted(fault))
                        )
                    );
                    states = transport.StateChanged.Subscribe(
                        new Observer<PlaybackState>(
                            state => Apply(w => w.StateChanged(state)),
                            () => Apply(static w => w.Completed()),
                            fault => Apply(w => w.Faulted(fault))
                        )
                    );
                }
                catch (ObjectDisposedException)
                {
                    return Result.Fail(PlaybackEndWatch.Disposed);
                }

                // After subscribing, so an end that lands in between is seen one way or the other.
                var current = transport.State;
                Apply(w => w.Started(current));

                return await _done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                states?.Dispose();
                errors?.Dispose();
            }
        }

        private void Apply(Func<PlaybackEndWatch, PlaybackEndWatch> step)
        {
            Result? outcome;
            lock (_gate)
            {
                _watch = step(_watch);
                outcome = _watch.Outcome;
            }

            if (outcome is { } decided)
                _done.TrySetResult(decided);
        }
    }

    private sealed class Observer<T>(Action<T> onNext, Action onCompleted, Action<Exception> onError)
        : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnCompleted() => onCompleted();

        public void OnError(Exception error) => onError(error);
    }
}
