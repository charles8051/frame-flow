using FrameFlow.Playback;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins #708 on the player: a second <see cref="IAsyncDisposable.DisposeAsync"/> made while the
/// first is under way returns only once the first has released the claims on the sinks and
/// disposed the coordinator, and nothing is torn down twice (ADR-0086, decision 3). The first is
/// held inside the session's dispose, over the real controller's dispatch loop.
/// </summary>
public sealed class PlayerDisposeTests
{
    [Fact]
    public async Task ASecondDispose_ReturnsOnlyOnceTheFirstHasReleasedTheClaims_AndDisposedTheCoordinator()
    {
        var claims = new CountingClaims();
        var rig = new ControllerRig(sinkClaims: claims);
        var transitions = new CompletionWatch<PlaylistTransition>();
        using var subscription = rig.Player.SourceTransitioned.Subscribe(transitions);
        Assert.True((await rig.Controller.LoadAsync(new ControllerRig.FakeSource())).IsSuccess);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Session.DisposeHeldUntil = release.Task;

        var first = rig.Player.DisposeAsync().AsTask();
        await rig.Session.DisposeEntered.WaitAsync(Bound);
        var second = rig.Player.DisposeAsync().AsTask();
        var seenAsTheSecondEnded = second.ContinueWith(
            _ => (claims.Released, transitions.Completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        // The session's dispose cannot end before it is released, so neither can the first.
        Assert.False(second.IsCompleted);
        Assert.False(transitions.Completed);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Bound);

        Assert.Equal((1, true), await seenAsTheSecondEnded);
        Assert.Equal(1, claims.Released);
        Assert.Equal(1, rig.Session.Disposals);
    }

    [Fact]
    public async Task TheTeardownRunsOnce_ForDisposesThatOverlap_WhenTheControllersDisposeThrows()
    {
        // The player logs a controller dispose that throws, and goes on to the coordinator. A second
        // dispose that ran the teardown again would log the same failure a second time.
        var logger = new WarningCounter();
        var rig = new ControllerRig(playerLogger: logger);
        Assert.True((await rig.Controller.LoadAsync(new ControllerRig.FakeSource())).IsSuccess);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Session.DisposeHeldUntil = release.Task;
        rig.Session.DisposeThrows = new InvalidOperationException("the session's dispose failed");

        var first = rig.Player.DisposeAsync().AsTask();
        await rig.Session.DisposeEntered.WaitAsync(Bound);
        var second = rig.Player.DisposeAsync().AsTask();
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Bound);

        Assert.Equal(1, logger.Warnings);
        Assert.Equal(1, rig.Session.Disposals);
    }

    // Only turns a dispose that never ends into a failure.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Claims that count their release.</summary>
    private sealed class CountingClaims : IDisposable
    {
        private int _released;

        public int Released => Volatile.Read(ref _released);

        public void Dispose() => Interlocked.Increment(ref _released);
    }

    /// <summary>Counts what is logged at Warning.</summary>
    private sealed class WarningCounter : ILogger
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }

    /// <summary>Records whether a stream has completed, which the coordinator's do when it is disposed.</summary>
    private sealed class CompletionWatch<T> : IObserver<T>
    {
        private int _completed;

        public bool Completed => Volatile.Read(ref _completed) != 0;

        public void OnCompleted() => Volatile.Write(ref _completed, 1);

        public void OnError(Exception error) { }

        public void OnNext(T value) { }
    }
}
