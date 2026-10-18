// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Pins what a controller's dispose does to the work its command loop is running (ADR-0086,
/// decision 3; #719). It cancels the session operation the loop is waiting on before it waits for
/// the loop, so an open or a play that only its token can end does not hold the dispose. The
/// command that was waiting fails as disposed rather than as a load that failed, and the session is
/// disposed only once the operation has ended. A ticker the loop starts as it finishes its command
/// is stopped before the dispose returns, and a seek still going is waited for before the session
/// goes. The commands queued behind a seek are the loop's until it ends.
/// </summary>
/// <remarks>
/// The session's held operations end on their token and nothing else, as an open over a stalled
/// source does once its read can be interrupted. Every wait is on a signal the session raises; the
/// bound only limits a failure, which on the code before #719 was a dispose that never ended.
/// </remarks>
public sealed class ControllerDisposeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ADispose_CancelsAnOpenTheLoopIsWaitingOn_ThenDisposesTheSession()
    {
        var session = new HeldSession { HoldOpen = true };
        var controller = NewController(session);
        var errors = new ConcurrentQueue<PlaybackError>();
        using var subscription = controller.ErrorOccurred.Subscribe(new Relay<PlaybackError>(errors.Enqueue));
        var load = controller.LoadAsync(new FakeSource());
        await session.OpenEntered.WaitAsync(Bound);
        Assert.True(session.OpenTokenCanBeCanceled, "The open was given a token nothing can cancel.");

        await controller.DisposeAsync().AsTask().WaitAsync(Bound);

        var result = await load.WaitAsync(Bound);
        Assert.False(result.IsSuccess);
        Assert.Equal("PlaybackController is disposing.", result.Error!.Message);
        Assert.Equal(["open cancelled", "session disposed"], session.Log);
        Assert.Empty(errors); // a cancelled open is not a load that failed
    }

    [Fact]
    public async Task ADispose_CancelsAPlayTheLoopIsWaitingOn_ThenDisposesTheSession()
    {
        var session = new HeldSession { HoldPlay = true };
        var controller = NewController(session);
        Assert.True((await controller.LoadAsync(new FakeSource()).WaitAsync(Bound)).IsSuccess);
        var play = controller.PlayAsync();
        await session.PlayEntered.WaitAsync(Bound);
        Assert.True(session.PlayTokenCanBeCanceled, "The play was given a token nothing can cancel.");

        await controller.DisposeAsync().AsTask().WaitAsync(Bound);

        var result = await play.WaitAsync(Bound);
        Assert.False(result.IsSuccess);
        Assert.Equal("PlaybackController is disposing.", result.Error!.Message);
        Assert.Equal(["play cancelled", "session disposed"], session.Log);
        Assert.False(controller.TickerRunning);
    }

    [Fact]
    public async Task ADisposeThatCancelsTheOpenOfAPlayAtIdle_GivesTheQueuesReservationBack()
    {
        // A play at Idle reserves the item it loads. A load that ends without loading it gives the
        // reservation back, and a dispose that cancels the open is one.
        var session = new HeldSession { HoldOpen = true };
        var factory = new OneSessionFactory(session, new FakeSource());
        var controller = NewController(factory);
        var play = controller.PlayAsync();
        await session.OpenEntered.WaitAsync(Bound);
        Assert.True(session.OpenTokenCanBeCanceled, "The open was given a token nothing can cancel.");

        await controller.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.False((await play.WaitAsync(Bound)).IsSuccess);
        Assert.Equal(1, factory.Releases);
        Assert.Equal(["open cancelled", "session disposed"], session.Log);
    }

    [Fact]
    public async Task ATickerTheLoopStartsAsItFinishesItsCommand_IsStoppedBeforeTheDisposeReturns()
    {
        // A play at Idle loads and then plays in one command, and the play starts the ticker
        // before it plays the session. The warm-up ignores its token and is released only once
        // the dispose has run up to its wait for the loop, past its first stop of the ticker, so
        // the loop starts the ticker after that stop.
        var session = new HeldSession { HoldWarmUp = true };
        var controller = NewController(session, reservable: new FakeSource());
        var play = controller.PlayAsync();
        await session.WarmUpEntered.WaitAsync(Bound);

        var disposing = controller.DisposeAsync().AsTask();
        session.ReleaseWarmUp();
        await disposing.WaitAsync(Bound);

        // The session was played, so the ticker was started just before.
        Assert.Equal(["play", "session disposed"], session.Log);
        Assert.False(controller.TickerRunning);
        await play.WaitAsync(Bound);
    }

    [Fact]
    public async Task ADispose_WaitsForASeekStillGoing_BeforeItDisposesTheSession()
    {
        // The seek ignores its token and ends only when released, so a dispose that waits for its
        // runner cannot dispose the session before the release. One that did not wait disposed the
        // session under the seek.
        var session = new HeldSession { HoldSeek = true };
        var controller = NewController(session);
        Assert.True((await controller.LoadAsync(new FakeSource()).WaitAsync(Bound)).IsSuccess);
        var awaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OnAwaitingSeekRunners = () => awaiting.TrySetResult();
        Assert.True((await controller.SeekAsync(TimeSpan.FromSeconds(0.5)).WaitAsync(Bound)).IsSuccess);
        await session.SeekEntered.WaitAsync(Bound);

        var disposing = controller.DisposeAsync().AsTask();
        var first = await Task.WhenAny(awaiting.Task, session.DisposeEntered).WaitAsync(Bound);

        Assert.Same(awaiting.Task, first);
        session.ReleaseSeek();
        await disposing.WaitAsync(Bound);
        Assert.Equal(["seek ended", "session disposed"], session.Log);
    }

    [Fact]
    public async Task ADispose_LeavesTheCommandsQueuedBehindASeekToTheLoopDrainingThem()
    {
        // A pause and an unload sent during a seek are queued behind it, in a queue only the loop
        // touches (ADR-0023). The seek ends, and the loop is running the pause, which ignores its
        // token and is held, with the unload still queued, when the dispose begins. A dispose that
        // failed the unload as it began took it off that queue on its own thread while the loop was
        // draining it: the two could take the same command, and the one that found the queue empty
        // threw.
        var session = new HeldSession { HoldSeek = true, HoldPause = true };
        var controller = NewController(session);
        Assert.True((await controller.LoadAsync(new FakeSource()).WaitAsync(Bound)).IsSuccess);
        Assert.True((await controller.PlayAsync().WaitAsync(Bound)).IsSuccess);
        Assert.True((await controller.SeekAsync(TimeSpan.FromSeconds(0.5)).WaitAsync(Bound)).IsSuccess);
        await session.SeekEntered.WaitAsync(Bound);
        var pause = controller.PauseAsync();
        var unload = controller.UnloadAsync();
        // Through the same queue, so once it completes the loop has queued both behind the seek.
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off).WaitAsync(Bound)).IsSuccess);
        session.ReleaseSeek();
        await session.PauseEntered.WaitAsync(Bound);

        // The dispose runs on this thread up to its first wait, so its first step is done before
        // the pause is released.
        var disposing = controller.DisposeAsync().AsTask();
        session.ReleasePause();
        await disposing.WaitAsync(Bound);

        Assert.True((await pause.WaitAsync(Bound)).IsSuccess);
        // The loop took the unload in before the dispose began, so the loop runs it.
        var unloaded = await unload.WaitAsync(Bound);
        Assert.True(unloaded.IsSuccess, unloaded.Error?.Message);
        Assert.Equal(["play", "seek ended", "pause ended", "session disposed"], session.Log);
    }

    [Fact]
    public async Task ADisposeWhoseFirstStepThrows_StillWaitsForTheLoopAndTheSeek_BeforeTheSessionGoes()
    {
        // The seek registers a callback on its token that throws, so cancelling the seek, the
        // dispose's first step, throws. The dispose still waits for the loop and the seek before it
        // disposes the session, and then throws what the step threw.
        var session = new HeldSession { HoldSeek = true, SeekCallbackThrows = true };
        var controller = NewController(session);
        Assert.True((await controller.LoadAsync(new FakeSource()).WaitAsync(Bound)).IsSuccess);
        var awaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OnAwaitingSeekRunners = () => awaiting.TrySetResult();
        Assert.True((await controller.SeekAsync(TimeSpan.FromSeconds(0.5)).WaitAsync(Bound)).IsSuccess);
        await session.SeekEntered.WaitAsync(Bound);

        var disposing = controller.DisposeAsync().AsTask();
        var first = await Task.WhenAny(awaiting.Task, session.DisposeEntered).WaitAsync(Bound);

        Assert.Same(awaiting.Task, first);
        session.ReleaseSeek();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => disposing.WaitAsync(Bound));
        Assert.Contains(
            HeldSession.SeekCallbackFailure,
            (thrown is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [thrown]).Select(e => e.Message)
        );
        Assert.Equal(["seek ended", "session disposed"], session.Log);
    }

    [Fact]
    public async Task ADisposeWhoseFirstStepThrows_StillFailsTheCommandsQueuedBehindASeek()
    {
        // A pause sent during a seek is queued behind it and cancels it. A second seek replaces the
        // first, and has a callback on its token that throws, so cancelling it, the dispose's first
        // step, throws. The queue is the loop's until it ends, so the dispose fails the pause only
        // after that. One that let the throw skip that step left the pause waiting for good.
        var session = new HeldSession { HoldSeek = true };
        var controller = NewController(session);
        Assert.True((await controller.LoadAsync(new FakeSource()).WaitAsync(Bound)).IsSuccess);
        Assert.True((await controller.SeekAsync(TimeSpan.FromSeconds(0.25)).WaitAsync(Bound)).IsSuccess);
        await session.SeekEntered.WaitAsync(Bound);
        var pause = controller.PauseAsync();
        session.SeekCallbackThrows = true;
        // Through the same queue as the pause, so once it completes the pause is queued.
        Assert.True((await controller.SeekAsync(TimeSpan.FromSeconds(0.5)).WaitAsync(Bound)).IsSuccess);
        await session.SecondSeekEntered.WaitAsync(Bound);

        var disposing = controller.DisposeAsync().AsTask();
        session.ReleaseSeek();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => disposing.WaitAsync(Bound));

        Assert.Contains(
            HeldSession.SeekCallbackFailure,
            (thrown is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [thrown]).Select(e => e.Message)
        );
        var paused = await pause.WaitAsync(Bound);
        Assert.False(paused.IsSuccess);
        Assert.Equal("PlaybackController is disposing.", paused.Error!.Message);
    }

    private static PlaybackControllerCore NewController(HeldSession session, IMediaSource? reservable = null) =>
        NewController(new OneSessionFactory(session, reservable));

    private static PlaybackControllerCore NewController(OneSessionFactory factory)
    {
        var time = new FakeTimeProvider();
        return new(NullLogger<PlaybackControllerCore>.Instance, factory, new PlaybackClock(time), timeProvider: time);
    }

    /// <summary>Hands out one session, reserves <paramref name="reservable"/> to start, and counts what is given back.</summary>
    private sealed class OneSessionFactory(IPlaybackSession session, IMediaSource? reservable) : IPlaybackSessionFactory
    {
        private int _releases;

        public int Releases => Volatile.Read(ref _releases);

        public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks) => session;

        public IMediaSource? ReserveStart() => reservable;

        public void ReleaseStart() => Interlocked.Increment(ref _releases);
    }

    /// <summary>
    /// A session whose open or play, when held, ends only when its token is cancelled, and whose
    /// warm-up, seek or pause, when held, ignores its token and ends when released. It logs what it
    /// did, and whether the tokens its open and play were given can be cancelled at all.
    /// </summary>
    private sealed class HeldSession : IPlaybackSession
    {
        private readonly ConcurrentQueue<string> _log = new();
        private readonly TaskCompletionSource _openEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _playEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _warmUpEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _warmUpReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _seekEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _seekReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondSeekEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seeksEntered;
        private readonly TaskCompletionSource _pauseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _pauseReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new();

        public bool HoldOpen { get; init; }

        public bool HoldPlay { get; init; }

        public bool HoldWarmUp { get; init; }

        public bool HoldSeek { get; init; }

        public bool HoldPause { get; init; }

        /// <summary>When set, a held seek registers a callback on its token that throws <see cref="SeekCallbackFailure"/>.</summary>
        public bool SeekCallbackThrows { get; set; }

        public const string SeekCallbackFailure = "The seek's cancellation callback failed.";

        public bool OpenTokenCanBeCanceled { get; private set; }

        public bool PlayTokenCanBeCanceled { get; private set; }

        public Task SeekEntered => _seekEntered.Task;

        public Task SecondSeekEntered => _secondSeekEntered.Task;

        public Task DisposeEntered => _disposeEntered.Task;

        public void ReleaseSeek() => _seekReleased.TrySetResult();

        public Task PauseEntered => _pauseEntered.Task;

        public void ReleasePause() => _pauseReleased.TrySetResult();

        public string[] Log => [.. _log];

        public Task OpenEntered => _openEntered.Task;

        public Task PlayEntered => _playEntered.Task;

        public Task WarmUpEntered => _warmUpEntered.Task;

        public void ReleaseWarmUp() => _warmUpReleased.TrySetResult();

        public MediaInfo? MediaInfo { get; } = new("fake", TimeSpan.FromSeconds(1), [], []);

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public async ValueTask InitializeAsync(IMediaSource source, CancellationToken cancellationToken = default)
        {
            if (!HoldOpen)
                return;
            OpenTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            _openEntered.TrySetResult();
            await HoldUntilCancelledAsync("open cancelled", cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            if (!HoldWarmUp)
                return;
            _warmUpEntered.TrySetResult();
            await _warmUpReleased.Task.ConfigureAwait(false);
        }

        public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
        {
            if (!HoldPlay)
            {
                _log.Enqueue("play");
                return;
            }
            PlayTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            _playEntered.TrySetResult();
            await HoldUntilCancelledAsync("play cancelled", cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
        {
            if (!HoldPause)
                return;
            _pauseEntered.TrySetResult();
            await _pauseReleased.Task.ConfigureAwait(false);
            _log.Enqueue("pause ended");
        }

        public async ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            if (!HoldSeek)
                return;
            if (SeekCallbackThrows)
                cancellationToken.Register(static () => throw new InvalidOperationException(SeekCallbackFailure));
            _seekEntered.TrySetResult();
            if (Interlocked.Increment(ref _seeksEntered) == 2)
                _secondSeekEntered.TrySetResult();
            await _seekReleased.Task.ConfigureAwait(false);
            _log.Enqueue("seek ended");
        }

        public ValueTask DisposeAsync()
        {
            _log.Enqueue("session disposed");
            _disposeEntered.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private async Task HoldUntilCancelledAsync(string cancelled, CancellationToken cancellationToken)
        {
            try
            {
                await _never.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Enqueue(cancelled);
                throw;
            }
        }
    }

    private sealed class FakeSource : IMediaSource
    {
        public string DisplayName => "fake";
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private sealed class Relay<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => onNext(value);
    }
}
