using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A player's items decode on hardware devices the player keeps, one per backend, rather than on
/// a device each item creates and disposes (media-clips phase 4, decision 7).
/// </summary>
/// <remarks>
/// <para>
/// The witness is the FFmpeg device context each hardware frame carries. Two items on a device of
/// their own each carry their own context; items on the shared device carry the same one.
/// </para>
/// <para>
/// The two items are separate source objects for one file, so the second is opened anew rather
/// than rewound in place, which would keep the first item's decoder and say nothing about devices.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class HardwareDeviceSharingTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private const int H264 = 27;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly FfmpegBootstrapFixture _fixture;

    public HardwareDeviceSharingTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresHardwareDecodeFact(H264)]
    public async Task ThePlayersItems_DecodeOnOneSharedDevice()
    {
        await using var sink = new DeviceRecordingSink(holdFirst: false);
        var first = Corpus(Clip);
        var second = Corpus(Clip);
        var coordinator = PlaylistCoordinator.Of([new MediaClip(first), new MediaClip(second)], RepeatMode.Off);
        await using var controller = Create(coordinator, sink, configureVideo: null);
        var ended = StateReached(controller, PlaybackState.Ended);

        Assert.True((await controller.LoadAsync(first)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await ended.WaitAsync(Bound);

        var frames = sink.Frames;
        Assert.True(BothItemsPresented(frames), "the second item presented nothing");
        var devices = frames.Select(f => f.Device).Distinct().ToList();
        Assert.True(
            devices.Count == 1 && devices[0] != nint.Zero,
            $"the items decoded on {devices.Count} device context(s): {string.Join(", ", devices)}"
        );
    }

    [RequiresHardwareDecodeFact(H264)]
    public async Task AnItemThatFaults_DropsTheSharedDevice_AndTheNextItemDecodesOnANewOne()
    {
        // The sink holds the first item's first frame until it is disposed, which keeps that
        // item's device context allocated, so the next item's cannot land at its address.
        await using var sink = new DeviceRecordingSink(holdFirst: true);

        // Every chain built before the first frame is presented is the first item's: its probe
        // and its graph. They throw on their 21st frame. The second item's are built after.
        var first = Corpus(Clip);
        var second = Corpus(Clip);
        var coordinator = PlaylistCoordinator.Of([new MediaClip(first), new MediaClip(second)], RepeatMode.Off);
        await using var controller = Create(coordinator, sink, chain => sink.Count == 0 ? Faulting(chain) : chain);
        var ended = StateReached(controller, PlaybackState.Ended);

        Assert.True((await controller.LoadAsync(first)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await ended.WaitAsync(Bound);

        var frames = sink.Frames;
        Assert.True(BothItemsPresented(frames), "the second item presented nothing");
        var devices = frames.Select(f => f.Device).Distinct().ToList();
        Assert.True(
            devices.Count == 2 && !devices.Contains(nint.Zero),
            $"the items decoded on {devices.Count} device context(s): {string.Join(", ", devices)}"
        );
    }

    /// <summary>
    /// <paramref name="chain"/>, throwing on its 21st frame. It passes hardware frames through and
    /// holds one at a time, so a path that keeps frames on the GPU still builds around it.
    /// </summary>
    private static GraphChain<IVideoFrame> Faulting(GraphChain<IVideoFrame> chain)
    {
        var frames = 0;
        return chain.Then(
            new OperatorNode<IVideoFrame, IVideoFrame>(
                "inject-fault",
                (frame, _) =>
                    ++frames == FaultInjector.FaultFrame
                        ? throw new InjectedFault(0)
                        : ValueTask.FromResult<IVideoFrame?>(frame),
                holding: FrameHolding.InFlight,
                domains: FrameDomainRule.Any
            )
        );
    }

    private static MediaSource Corpus(string name) =>
        MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(name)!);

    private static IPlaybackController Create(
        PlaylistCoordinator coordinator,
        IVideoSink sink,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configureVideo
    ) =>
        PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Required,
            hardwareDecodeCapabilities: FfmpegBootstrapFixture.ReadCapabilities(),
            yieldHardwareFrames: true,
            initialRepeatMode: RepeatMode.Off,
            configureVideo: configureVideo
        );

    /// <summary>Whether the presented PTS restart, as they do when the second item starts.</summary>
    private static bool BothItemsPresented(IReadOnlyList<(TimeSpan Pts, nint Device)> frames) =>
        frames.Zip(frames.Skip(1)).Any(pair => pair.Second.Pts < pair.First.Pts);

    private static Task StateReached(IPlaybackController controller, PlaybackState state)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = controller.PlaybackStateChanged.Subscribe(
            new ActionObserver<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current == state)
                    signal.TrySetResult();
            })
        );
        return signal.Task.ContinueWith(_ => sub.Dispose(), TaskScheduler.Default);
    }

    /// <summary>Records each frame's PTS and the device context it was decoded on.</summary>
    private sealed class DeviceRecordingSink(bool holdFirst) : IVideoSink
    {
        private readonly Lock _gate = new();
        private readonly List<(TimeSpan Pts, nint Device)> _frames = [];
        private IVideoFrame? _held;

        public int Count
        {
            get
            {
                lock (_gate)
                    return _frames.Count;
            }
        }

        public IReadOnlyList<(TimeSpan Pts, nint Device)> Frames
        {
            get
            {
                lock (_gate)
                    return [.. _frames];
            }
        }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            var device = frame is GpuVideoFrame gpu ? gpu.HwDeviceContext : nint.Zero;
            bool keep;
            lock (_gate)
            {
                _frames.Add((frame.Pts, device));
                keep = holdFirst && _held is null;
                if (keep)
                    _held = frame;
            }
            if (!keep)
                frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public int? MaxHeldFrames => holdFirst ? 1 : 0;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Any;

        public ValueTask DisposeAsync()
        {
            IVideoFrame? held;
            lock (_gate)
            {
                held = _held;
                _held = null;
            }
            held?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
