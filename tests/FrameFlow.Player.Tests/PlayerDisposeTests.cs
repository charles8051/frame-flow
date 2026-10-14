using FrameFlow.Playback;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins #708 on the player: a second <see cref="IAsyncDisposable.DisposeAsync"/> made while the
/// first is under way returns only once the first has released the claims on the sinks and
/// disposed the coordinator, and nothing is torn down twice (ADR-0086, decision 3). Every dispose
/// throws what the controller's dispose threw (decision 3.4). The first is held inside the
/// session's dispose, over the real controller's dispatch loop.
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
    public async Task EveryDispose_ThrowsWhatTheControllersDisposeThrew_OnceTheClaimsAreReleasedAndTheCoordinatorIsDisposed()
    {
        // The player used to log a controller dispose that threw and return (ADR-0086, decision
        // 3.4). It now throws it, after the coordinator, and a dispose that overlaps the first
        // throws the same instance without tearing anything down again.
        var claims = new CountingClaims();
        var rig = new ControllerRig(sinkClaims: claims);
        var transitions = new CompletionWatch<PlaylistTransition>();
        using var subscription = rig.Player.SourceTransitioned.Subscribe(transitions);
        Assert.True((await rig.Controller.LoadAsync(new ControllerRig.FakeSource())).IsSuccess);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("the session's dispose failed");
        rig.Session.DisposeHeldUntil = release.Task;
        rig.Session.DisposeThrows = failure;

        var first = rig.Player.DisposeAsync().AsTask();
        await rig.Session.DisposeEntered.WaitAsync(Bound);
        var second = rig.Player.DisposeAsync().AsTask();
        release.SetResult();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Bound)));
        Assert.Equal(1, claims.Released);
        Assert.True(transitions.Completed);
        Assert.Equal(1, rig.Session.Disposals);

        // A later dispose ends as the first did, and tears nothing down again.
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Player.DisposeAsync().AsTask()));
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
