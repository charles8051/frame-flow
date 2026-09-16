namespace FrameFlow.Playback.Tests;

/// <summary>
/// Smoke tests for the <see cref="PlaybackController.Create"/>
/// factory — verifies the facade returns a working
/// <see cref="IPlaybackController"/> with the expected initial state.
/// No FFmpeg dependency.
/// </summary>
public sealed class PlaybackControllerFactoryTests
{
    [Fact]
    public async Task Create_NoSinks_ReturnsIdleController()
    {
        await using var controller = PlaybackController.Create();

        Assert.Equal(PlaybackState.Idle, controller.State);
        Assert.Equal(SeekState.NotSeeking, controller.SeekingState);
        Assert.Equal(RepeatMode.Off, controller.RepeatMode);
        Assert.Equal(TimeSpan.Zero, controller.Position);
        Assert.Equal(TimeSpan.Zero, controller.Duration);
        Assert.Null(controller.MediaInfo);
    }

    [Fact]
    public async Task Create_InitialRepeatModeOne_StartsInOne()
    {
        await using var controller = PlaybackController.Create(
            initialRepeatMode: RepeatMode.One
        );

        Assert.Equal(RepeatMode.One, controller.RepeatMode);
    }

    [Fact]
    public async Task Create_ObservablesAreLive()
    {
        await using var controller = PlaybackController.Create();

        // Subscribing to each observable should not throw and should
        // return a real subscription.
        var sub1 = controller.PlaybackStateChanged.Subscribe(
            new RelayObserver<StateTransition<PlaybackState>>()
        );
        var sub2 = controller.SeekStateChanged.Subscribe(
            new RelayObserver<StateTransition<SeekState>>()
        );
        var sub3 = controller.RepeatModeChanged.Subscribe(
            new RelayObserver<StateTransition<RepeatMode>>()
        );
        var sub4 = controller.LoopRestarted.Subscribe(new RelayObserver<LoopRestarted>());
        var sub5 = controller.ErrorOccurred.Subscribe(new RelayObserver<PlaybackError>());
        var sub6 = controller.PositionTick.Subscribe(new RelayObserver<TimeSpan>());

        Assert.NotNull(sub1);
        Assert.NotNull(sub2);
        Assert.NotNull(sub3);
        Assert.NotNull(sub4);
        Assert.NotNull(sub5);
        Assert.NotNull(sub6);

        sub1.Dispose();
        sub2.Dispose();
        sub3.Dispose();
        sub4.Dispose();
        sub5.Dispose();
        sub6.Dispose();
    }

    [Fact]
    public async Task PlayBeforeLoad_FailsWithInvalidOperation()
    {
        await using var controller = PlaybackController.Create();

        var result = await controller.PlayAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, result.Error?.Category);
    }

    [Fact]
    public async Task SetRepeatMode_TogglesBetweenOffAndOne()
    {
        await using var controller = PlaybackController.Create();

        var to1 = await controller.SetRepeatModeAsync(RepeatMode.One);
        Assert.True(to1.IsSuccess);
        Assert.Equal(RepeatMode.One, controller.RepeatMode);

        var to0 = await controller.SetRepeatModeAsync(RepeatMode.Off);
        Assert.True(to0.IsSuccess);
        Assert.Equal(RepeatMode.Off, controller.RepeatMode);
    }

    [Fact]
    public async Task SetRepeatMode_ReachesTheQueueTheControllerPlays()
    {
        // The controller plays a queue of one, and the queue runs the repeat mode. It is told at
        // construction and on every change (the one-player-type record, decision 1).
        var controller = PlaybackController.Create(initialRepeatMode: RepeatMode.One);
        await using var _ = controller;
        var coordinator = Coordinator(controller);

        Assert.Equal(RepeatMode.One, coordinator.RepeatMode);

        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.All)).IsSuccess);
        Assert.Equal(RepeatMode.All, coordinator.RepeatMode);
    }

    [Fact]
    public async Task Dispose_DisposesTheCoordinatorTheControllerBuilt()
    {
        // Nothing else owns it, so nothing else can dispose it (decision 7). A playlist player's
        // coordinator outlives its controller and is disposed by the player instead.
        var controller = PlaybackController.Create();
        var coordinator = Coordinator(controller);

        await controller.DisposeAsync();

        // A disposed transition subject accepts no more subscribers.
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SourceTransitioned.Subscribe(new RelayObserver<PlaylistTransition>())
        );
    }

    private static PlaylistCoordinator Coordinator(IPlaybackController controller) =>
        ((PlaylistSessionFactory)((PlaybackControllerCore)controller).SessionFactory).Coordinator;

    private sealed class RelayObserver<T> : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) { }
    }
}
