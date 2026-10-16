using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A player whose GPU device is lost fails its item with <see cref="ErrorCategory.DeviceLost"/>,
/// carrying the device's reason, and a load plays again once the device is replaced (#759). With
/// its only item failed, the player ends (#758).
/// </summary>
/// <remarks>
/// No device is removed. A sink that throws <see cref="DeviceLostException"/> stands in for one
/// on a lost device, and stops throwing when the test replaces the device. These tests wait on
/// real playback, which is why they are in this suite (ADR-0072 rule 6). Each wait completes on a
/// signal; the bound only stops a failing run.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DeviceLossRecoveryTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private const int DeviceReset = unchecked((int)0x887A0007); // DXGI_ERROR_DEVICE_RESET
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public DeviceLossRecoveryTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerOnALostDevice_FailsWithDeviceLost_AndALoadPlaysAgain()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var sink = new LostDeviceSink();
        var (controller, _) = IntegrationTestHelper.CreateController(sink);
        await using (controller)
        {
            var fatal = new TaskCompletionSource<PlaybackError>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var errors = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(error =>
                {
                    // The sink's per-frame failures are reported too; the fault is the one that
                    // says the sink threw on every call in a row.
                    if (error.Message.Contains("in a row", StringComparison.Ordinal))
                        fatal.TrySetResult(error);
                })
            );
            using var states = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(transition =>
                {
                    if (transition.Current is PlaybackState.Ended or PlaybackState.Error)
                        ended.TrySetResult();
                })
            );

            sink.Lose(DeviceReset);
            Assert.True((await controller.LoadAsync(MediaSource.FromFile(path))).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);

            var error = await fatal.Task.WaitAsync(Bound);
            await ended.Task.WaitAsync(Bound);
            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Equal(ErrorCategory.DeviceLost, error.Category);
            Assert.Equal(DeviceReset, error.DeviceLoss?.RemovedReason);

            // The caller replaces the device and loads the same media again. A controller that
            // has ended unloads before it loads.
            sink.Replace();
            Assert.True((await controller.UnloadAsync()).IsSuccess);
            var load = await controller.LoadAsync(MediaSource.FromFile(path));
            Assert.True(load.IsSuccess, $"The load after the loss failed: {load.Error}");
            Assert.True((await controller.PlayAsync()).IsSuccess);

            await sink.Presented.WaitAsync(Bound);
            Assert.Equal(PlaybackState.Playing, controller.State);
        }
    }

    /// <summary>A sink that throws as a presenter on a lost device would, until it is replaced.</summary>
    private sealed class LostDeviceSink : IVideoSink
    {
        private readonly TaskCompletionSource _presented = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _removedReason;

        /// <summary>Completes on the first frame presented after <see cref="Replace"/>.</summary>
        public Task Presented => _presented.Task;

        public int? MaxHeldFrames => 0;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Cpu;

        public void Lose(int removedReason) => Volatile.Write(ref _removedReason, removedReason);

        public void Replace() => Volatile.Write(ref _removedReason, 0);

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            int reason = Volatile.Read(ref _removedReason);
            if (reason != 0)
                throw new DeviceLostException("The device was lost.", reason);

            _presented.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
