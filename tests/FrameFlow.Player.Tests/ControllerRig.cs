using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A player over the real <see cref="PlaybackControllerCore"/> dispatch loop, with an in-memory
/// session in place of FFmpeg and a clock that never moves. For tests that pin what
/// <see cref="IMediaTransport"/> reports when the controller underneath does its real work.
/// </summary>
/// <remarks>
/// Every command completes on the controller's dispatch loop after the observables it raises
/// have been called, so awaiting a command is the barrier: whatever it raised has been seen.
/// </remarks>
internal sealed class ControllerRig : IAsyncDisposable
{
    /// <param name="initialRepeatMode">The mode the controller and the queue start in.</param>
    /// <param name="sinkClaims">The claims the controller holds and releases as its dispose ends.</param>
    public ControllerRig(
        RepeatMode initialRepeatMode = RepeatMode.Off,
        IDisposable? sinkClaims = null
    )
    {
        Factory = new SessionFactory(Session);
        Controller = new PlaybackControllerCore(
            NullLogger<PlaybackControllerCore>.Instance,
            Factory,
            new PlaybackClock(new FrozenTime()),
            Microsoft.Extensions.Options.Options.Create(
                new FrameFlowPlaybackOptions { InitialRepeatMode = initialRepeatMode }
            ),
            new FrozenTime(),
            sinkClaims
        );
        Player = new PlaylistMediaPlayerCore(
            Controller,
            new PlaylistCoordinator([], initialRepeatMode),
            audioSink: null
        );
    }

    public FakeSession Session { get; } = new();

    public SessionFactory Factory { get; }

    public PlaybackControllerCore Controller { get; }

    public IMediaPlayer Player { get; }

    public ValueTask DisposeAsync() => Player.DisposeAsync();

    /// <summary>
    /// Hands out <see cref="Session"/>, and answers <c>Play</c> at <c>Idle</c> with
    /// <see cref="Reservable"/> the way a player built with an empty queue answers with the item
    /// added to it since.
    /// </summary>
    internal sealed class SessionFactory(FakeSession session) : IPlaybackSessionFactory
    {
        public IMediaSource? Reservable { get; set; }

        public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks) =>
            session;

        public IMediaSource? ReserveStart() => Reservable;
    }

    /// <summary>A session that opens whatever it is given, unless told to fail.</summary>
    internal sealed class FakeSession : IPlaybackSession
    {
        private readonly TaskCompletionSource _disposeEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _disposals;

        public Exception? InitializeThrows { get; set; }

        /// <summary>Holds the dispose until this completes, when set.</summary>
        public Task? DisposeHeldUntil { get; set; }

        /// <summary>What the dispose throws as it ends, when set.</summary>
        public Exception? DisposeThrows { get; set; }

        /// <summary>Completes once the dispose has been called.</summary>
        public Task DisposeEntered => _disposeEntered.Task;

        /// <summary>How many times the dispose has been called.</summary>
        public int Disposals => Volatile.Read(ref _disposals);

        public MediaInfo? MediaInfo { get; } =
            new(ContainerName: "fake", Duration: TimeSpan.FromSeconds(10), VideoStreams: [], AudioStreams: []);

        public TimeSpan Duration => TimeSpan.FromSeconds(10);

        public ValueTask InitializeAsync(IMediaSource source, CancellationToken cancellationToken = default) =>
            InitializeThrows is { } ex ? ValueTask.FromException(ex) : ValueTask.CompletedTask;

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PlayAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            _disposeEntered.TrySetResult();
            return DisposeHeldUntil is { } held ? new ValueTask(EndAfterAsync(held)) : End();
        }

        private ValueTask End() =>
            DisposeThrows is { } ex ? ValueTask.FromException(ex) : ValueTask.CompletedTask;

        private async Task EndAfterAsync(Task held)
        {
            await held;
            await End();
        }
    }

    internal sealed class FakeSource : IMediaSource
    {
        public string DisplayName => "fake";
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    /// <summary>
    /// A clock that reads the same instant forever and whose timers never fire. Nothing here plays,
    /// so nothing reads the time; this keeps the machine's clock out of the room regardless.
    /// </summary>
    private sealed class FrozenTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        ) => new InertTimer();

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
