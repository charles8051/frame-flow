using FrameFlow.Graph;
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
/// No device is removed. A graph node between the decoder and the pacer throws
/// <see cref="DeviceLostException"/> as the decoder does on a lost device, and stops throwing when
/// the test replaces the device. A sink cannot stand in: a sink's failure is reported as
/// <see cref="ErrorCategory.Sink"/>, whatever it threw (#836). These tests wait on real playback,
/// which is why they are in this suite (ADR-0072 rule 6). Each wait completes on a signal; the
/// bound only stops a failing run.
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
        var device = new LostDeviceNode();
        var sink = new PresentedSink();
        var controller = PlaybackController.Create(
            videoSink: sink,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            configureVideo: device.Configure
        );
        await using (controller)
        {
            var fatal = new TaskCompletionSource<PlaybackError>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var errors = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(error => fatal.TrySetResult(error))
            );
            using var states = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(transition =>
                {
                    if (transition.Current is PlaybackState.Ended or PlaybackState.Error)
                        ended.TrySetResult();
                })
            );

            // Lost after the load: the load's warm-up runs frames through the node, and a loss
            // there fails the load instead.
            Assert.True((await controller.LoadAsync(MediaSource.FromFile(path))).IsSuccess);
            device.Lose(DeviceReset);
            Assert.True((await controller.PlayAsync()).IsSuccess);

            var error = await fatal.Task.WaitAsync(Bound);
            await ended.Task.WaitAsync(Bound);
            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Equal(ErrorCategory.DeviceLost, error.Category);
            Assert.Equal(DeviceReset, error.DeviceLoss?.RemovedReason);

            // The caller replaces the device and loads the same media again. A controller that
            // has ended unloads before it loads.
            device.Replace();
            Assert.True((await controller.UnloadAsync()).IsSuccess);
            var load = await controller.LoadAsync(MediaSource.FromFile(path));
            Assert.True(load.IsSuccess, $"The load after the loss failed: {load.Error}");
            var presented = sink.NextPresented();
            Assert.True((await controller.PlayAsync()).IsSuccess);

            await presented.WaitAsync(Bound);
            Assert.Equal(PlaybackState.Playing, controller.State);
        }
    }

    /// <summary>
    /// A node that throws as the decoder does on a lost device (#759), until the device is replaced.
    /// </summary>
    private sealed class LostDeviceNode
    {
        private int _removedReason;

        public void Lose(int removedReason) => Volatile.Write(ref _removedReason, removedReason);

        public void Replace() => Volatile.Write(ref _removedReason, 0);

        public GraphChain<IVideoFrame> Configure(GraphChain<IVideoFrame> chain) =>
            chain.Then(
                new OperatorNode<IVideoFrame, IVideoFrame>(
                    "lost-device",
                    (frame, _) =>
                    {
                        int reason = Volatile.Read(ref _removedReason);
                        if (reason == 0)
                            return ValueTask.FromResult<IVideoFrame?>(frame);

                        frame.Dispose();
                        throw new DeviceLostException("The device was lost.", reason);
                    }
                )
            );
    }

    /// <summary>A sink that says when it is given a frame.</summary>
    private sealed class PresentedSink : IVideoSink
    {
        private TaskCompletionSource? _presented;

        /// <summary>Completes on the first frame presented after this call.</summary>
        public Task NextPresented()
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _presented, next);
            return next.Task;
        }

        public int? MaxHeldFrames => 0;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Cpu;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            Volatile.Read(ref _presented)?.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
