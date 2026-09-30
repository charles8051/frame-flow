using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Hardware-decode coverage of the
/// <see cref="FrameFlow.Playback.PlaybackController"/>.
/// Mirrors <see cref="HardwareDecodeIntegrationTests"/> against the
/// substrate. Asserts the same contract: playback completes,
/// frame content is sane, and the controller surfaces the same
/// Auto/Disabled/Required selection behavior regardless of which
/// backend bound at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Required-with-empty-caps test is intentionally NOT ported.</b>
/// The old test wires an empty <see cref="HardwareDecodeCapabilities"/>
/// directly into DI to force Required-mode load failure. The new
/// substrate's <see cref="FrameFlow.Playback.PlaybackController.Create"/>
/// owns the decoder factory composition internally — there's no DI seam
/// to inject empty capabilities. The Required-failure path stays
/// exercised by the old-controller test until the substrate exposes a
/// capability injection hook.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(ContentCaptureCollection.Name)]
public sealed class HardwareDecodeIntegrationTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public HardwareDecodeIntegrationTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Auto_PlayCorpusFile_CompletesEnded()
    {
        var capture = await PlaybackHarness.PlayCorpusFileAsync(
            "test-av-h264-aac.mp4",
            hardwareDecodeMode: HardwareDecodeMode.Auto
        );

        Assert.True(
            capture.LoadResult.IsSuccess,
            $"LoadAsync failed: {capture.LoadResult.Error?.Message}"
        );
        Assert.True(
            capture.PlayResult.IsSuccess,
            $"PlayAsync failed: {capture.PlayResult.Error?.Message}"
        );
        Assert.Equal(PlaybackState.Ended, capture.FinalState);
        Assert.NotEmpty(capture.Video);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Disabled_PlayCorpusFile_MatchesSoftwarePath()
    {
        var capture = await PlaybackHarness.PlayCorpusFileAsync(
            "test-av-h264-aac.mp4",
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        Assert.True(capture.LoadResult.IsSuccess);
        Assert.True(capture.PlayResult.IsSuccess);
        Assert.Equal(PlaybackState.Ended, capture.FinalState);
        Assert.NotEmpty(capture.Video);
    }

    /// <summary>
    /// Auto must actually select a hardware backend on a machine that has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Auto_PlayCorpusFile_CompletesEnded"/> asserts the run reaches
    /// <see cref="PlaybackState.Ended"/> with frames, and passes identically whether those
    /// frames came off the GPU or the CPU. It has to: demanding hardware would fail on a
    /// runner without a GPU. So a change that silently dropped every codec to software
    /// decode — a native version bump being the likely cause — leaves the suite green.
    /// </para>
    /// <para>
    /// This closes that by gating instead of asserting unconditionally.
    /// <see cref="RequiresHardwareDecodeFactAttribute"/> skips where H.264 cannot use
    /// hardware here, so the assertion only runs where it is answerable.
    /// </para>
    /// <para>
    /// The gate asks about the codec, not just the device, and that matters: hardware
    /// support is per codec. On the machine this was written for, H.264, HEVC and VP9
    /// decode on D3D11VA while AV1 falls back to software against the same initialised
    /// device. A device-level gate would let an assertion run where the codec cannot
    /// answer it, and a test demanding hardware for every fixture would encode one
    /// machine's capability matrix.
    /// </para>
    /// <para>
    /// <c>HardwareBackend</c> names the backend that produced the frames rather than the
    /// one <c>Open</c> bound — <c>VideoDecoder.TrackHardwareEngagement</c> derives it from
    /// each frame's pixel format. So a non-null value here means frames genuinely came off
    /// hardware, which is what #74 corrected and what makes this worth asserting.
    /// </para>
    /// </remarks>
    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27)]
    public async Task Auto_WithABackendAvailable_DecodesH264OnHardware()
    {
        var capture = await PlaybackHarness.PlayCorpusFileAsync(
            "test-av-h264-aac.mp4",
            hardwareDecodeMode: HardwareDecodeMode.Auto
        );

        Assert.True(
            capture.LoadResult.IsSuccess,
            $"LoadAsync failed: {capture.LoadResult.Error?.Message}"
        );
        Assert.True(
            capture.PlayResult.IsSuccess,
            $"PlayAsync failed: {capture.PlayResult.Error?.Message}"
        );
        Assert.Equal(PlaybackState.Ended, capture.FinalState);
        Assert.NotEmpty(capture.Video);

        var backend = capture.Diagnostics.Pipeline.Stream.VideoDecoder.HardwareBackend;
        var available = string.Join(
            ", ",
            _fixture.Capabilities.Available.Where(b => b.Initialized).Select(b => b.Kind)
        );

        Assert.True(
            backend is not null,
            "Auto decoded H.264 in software, on a machine where the H.264 decoder "
                + $"advertises a hardware config for an initialised backend ({available}). "
                + "The gate and the decoder were asked the same question and gave different "
                + "answers, so either hardware decode stopped being selected, or binding it "
                + "failed for this stream in particular."
        );
    }

    /// <summary>
    /// A player that yields hardware frames opens its decoder for what its video path can hold
    /// (ADR-0081, #387), so the decoder's budget is that path's frame budget rather than the
    /// pool's default spare surfaces.
    /// </summary>
    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27, fixedPool: true)]
    public async Task YieldingHardwareFrames_BudgetsTheDecoderForWhatThePlayerHolds()
    {
        var videoSink = new HarnessVideoSink();
        var audioSink = new HarnessAudioSink();
        var controller = PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: HardwareDecodeMode.Auto,
            yieldHardwareFrames: true
        );

        try
        {
            var (load, play) = await IntegrationTestHelper.RunToCompletionAsync(
                controller,
                MediaSource.FromFile(PlaybackHarness.ResolveCorpusPath("test-av-h264-aac.mp4"))
            );
            Assert.True(load.IsSuccess, $"LoadAsync failed: {load.Error?.Message}");
            Assert.True(play.IsSuccess, $"PlayAsync failed: {play.Error?.Message}");

            var decoder = controller.GetDiagnostics().Pipeline.Stream.VideoDecoder;
            Assert.True(decoder.HardwareBackend is not null, "the player decoded in software");
            Assert.Equal(
                SubstrateSession.VideoFrameBudget(configurator: null, videoSink).Frames,
                decoder.HardwareFrameBudget
            );
        }
        finally
        {
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
            await controller.DisposeAsync();
            await videoSink.DisposeAsync();
            await audioSink.DisposeAsync();
        }
    }

    /// <summary>
    /// A sink that does not say how many frames it keeps leaves the video path unbounded, and a
    /// player yielding frames from a fixed hardware pool refuses to load rather than guess
    /// (ADR-0081, decision 4).
    /// </summary>
    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27, fixedPool: true)]
    public async Task YieldingHardwareFrames_ToASinkThatDoesNotSay_IsRefusedAtLoad()
    {
        var videoSink = new UndeclaredVideoSink();
        var controller = PlaybackController.Create(
            videoSink: videoSink,
            hardwareDecodeMode: HardwareDecodeMode.Required,
            yieldHardwareFrames: true
        );

        try
        {
            var load = await controller.LoadAsync(
                MediaSource.FromFile(PlaybackHarness.ResolveCorpusPath("test-av-h264-aac.mp4"))
            );

            Assert.False(load.IsSuccess);
            Assert.Contains("video-sink", load.Error?.ToString() ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await controller.DisposeAsync();
        }
    }

    /// <summary>
    /// A sink that takes CPU frames only, behind a player yielding hardware frames, is refused at
    /// load and named, where it used to be handed GPU frames it could not read (#435).
    /// </summary>
    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27)]
    public async Task YieldingHardwareFrames_ToACpuOnlySink_IsRefusedAtLoad()
    {
        var videoSink = new CpuOnlyVideoSink();
        var controller = PlaybackController.Create(
            videoSink: videoSink,
            hardwareDecodeMode: HardwareDecodeMode.Required,
            yieldHardwareFrames: true
        );

        try
        {
            var load = await controller.LoadAsync(
                MediaSource.FromFile(PlaybackHarness.ResolveCorpusPath("test-video-h264-yuv420p.mp4"))
            );

            Assert.False(load.IsSuccess);
            Assert.Contains("'video-sink' takes CPU frames", load.Error?.ToString() ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await controller.DisposeAsync();
        }
    }

    /// <summary>
    /// The #370 reproduction's HEVC clip, whose default pool faults at 6 held frames, plays
    /// through a player yielding hardware frames with its pool sized from the budget.
    /// </summary>
    // 172 is AV_CODEC_ID_HEVC.
    [RequiresHardwareDecodeFact(codecId: 172, fixedPool: true)]
    public async Task TheHevcPressureClip_PlaysWithItsPoolSizedFromTheBudget()
    {
        var videoSink = new HarnessVideoSink();
        var controller = PlaybackController.Create(
            videoSink: videoSink,
            hardwareDecodeMode: HardwareDecodeMode.Required,
            yieldHardwareFrames: true
        );

        try
        {
            var (load, play) = await IntegrationTestHelper.RunToCompletionAsync(
                controller,
                MediaSource.FromFile(PlaybackHarness.ResolveCorpusPath("test-portrait-hevc-pressure.mp4"))
            );
            Assert.True(load.IsSuccess, $"LoadAsync failed: {load.Error?.Message}");
            Assert.True(play.IsSuccess, $"PlayAsync failed: {play.Error?.Message}");
            Assert.Equal(PlaybackState.Ended, controller.State);

            var decoder = controller.GetDiagnostics().Pipeline.Stream.VideoDecoder;
            Assert.Equal(
                SubstrateSession.VideoFrameBudget(configurator: null, videoSink).Frames,
                decoder.HardwareFrameBudget
            );
            Assert.Equal(0, decoder.DecodeErrors);
        }
        finally
        {
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, videoSink: videoSink);
            await controller.DisposeAsync();
            await videoSink.DisposeAsync();
        }
    }

    /// <summary>
    /// A player given a <see cref="HardwareDevice"/> decodes every source it loads on that device,
    /// each through a decoder of its own (#428). Without one, each load's decoder makes its own
    /// device, and anything built on the last one outlives it.
    /// </summary>
    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27)]
    public async Task AnOwnedDevice_IsSharedByEverySourceThePlayerLoads()
    {
        using var device = CreateAnyDevice();
        var videoSink = new DeviceRecordingSink();
        var controller = PlaybackController.Create(
            videoSink: videoSink,
            hardwareDecodeMode: HardwareDecodeMode.Required,
            yieldHardwareFrames: true,
            hardwareDevice: device
        );

        var framesAfterEachLoad = new List<int>();
        try
        {
            var source = PlaybackHarness.ResolveCorpusPath("test-video-h264-yuv420p.mp4");
            for (int load = 0; load < 2; load++)
            {
                var (loaded, played) = await IntegrationTestHelper.RunToCompletionAsync(
                    controller, MediaSource.FromFile(source));
                Assert.True(loaded.IsSuccess, $"LoadAsync {load} failed: {loaded.Error?.Message}");
                Assert.True(played.IsSuccess, $"PlayAsync {load} failed: {played.Error?.Message}");
                var unloaded = await controller.UnloadAsync();
                Assert.True(unloaded.IsSuccess, $"UnloadAsync {load} failed: {unloaded.Error?.Message}");
                framesAfterEachLoad.Add(videoSink.Seen.Count);
            }
        }
        finally
        {
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller);
            await controller.DisposeAsync();
        }

        // Both loads decoded on the device. Each load's decoder was closed before the next opened,
        // so their pools' addresses may repeat and are not compared.
        Assert.True(framesAfterEachLoad[0] > 0, "the first load presented no hardware frames");
        Assert.True(framesAfterEachLoad[1] > framesAfterEachLoad[0], "the second load presented no hardware frames");
        Assert.All(videoSink.Seen, s => Assert.Equal(device.ContextPointer, s.Device));
    }

    /// <summary>
    /// A sink that prefers a backend gets its frames from it when they stay on the GPU (#532).
    /// Vulkan is last in Linux's default order and absent from Windows', so without the sink's
    /// preference this player decodes on another backend wherever one engages.
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, PreferenceClip)]
    public async Task ASinkThatPrefersABackend_GetsFramesFromIt()
    {
        var sink = new BackendRecordingSink(HardwareDecodeBackendKind.Vulkan);

        var backend = await PlayAsync(sink, yieldHardwareFrames: null);

        Assert.Equal(HardwareDecodeBackendKind.Vulkan, backend);
        Assert.NotEmpty(sink.Seen);
        Assert.All(sink.Seen, b => Assert.Equal(HardwareDecodeBackendKind.Vulkan, b));
    }

    /// <summary>
    /// A sink's preference is for the frames it receives on the GPU. A player that downloads them
    /// decodes on the backend it would have chosen with no preference at all (#532).
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, PreferenceClip)]
    public async Task ASinksPreference_IsSetAside_WhenFramesAreDownloaded()
    {
        var unasked = await PlayAsync(new BackendRecordingSink(), yieldHardwareFrames: false);

        var preferring = await PlayAsync(
            new BackendRecordingSink(HardwareDecodeBackendKind.Vulkan), yieldHardwareFrames: false);

        Assert.Equal(unasked, preferring);
    }

    private const string PreferenceClip = "test-video-h264-yuv420p.mp4";

    /// <summary>Plays <see cref="PreferenceClip"/> into <paramref name="sink"/> and returns the backend that decoded it.</summary>
    private static async Task<HardwareDecodeBackendKind?> PlayAsync(BackendRecordingSink sink, bool? yieldHardwareFrames)
    {
        var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Required,
            yieldHardwareFrames: yieldHardwareFrames
        );

        try
        {
            var (load, play) = await IntegrationTestHelper.RunToCompletionAsync(
                controller,
                MediaSource.FromFile(PlaybackHarness.ResolveCorpusPath(PreferenceClip))
            );
            Assert.True(load.IsSuccess, $"LoadAsync failed: {load.Error?.Message}");
            Assert.True(play.IsSuccess, $"PlayAsync failed: {play.Error?.Message}");
            return controller.GetDiagnostics().Pipeline.Stream.VideoDecoder.HardwareBackend;
        }
        finally
        {
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller);
            await controller.DisposeAsync();
        }
    }

    /// <summary>The first backend the probe initialised that FFmpeg can make a device for here.</summary>
    private HardwareDevice CreateAnyDevice()
    {
        foreach (var backend in _fixture.Capabilities.Available.Where(b => b.Initialized).Select(b => b.Kind))
        {
            try
            {
                return HardwareDevice.Create(backend);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException("No initialised backend would create a device.");
    }

    /// <summary>Records each hardware frame's device and pool, and keeps none.</summary>
    private sealed class DeviceRecordingSink : IVideoSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<(nint Device, nint Pool)> Seen { get; } = new();

        public int? MaxHeldFrames => 0;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Any;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            using (frame)
            {
                if (frame is GpuVideoFrame gpu)
                    Seen.Enqueue((gpu.HwDeviceContext, gpu.HwFramesContext));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CpuOnlyVideoSink : IVideoSink
    {
        public int? MaxHeldFrames => 0;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Cpu;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UndeclaredVideoSink : IVideoSink
    {
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Required-with-empty-capabilities is covered at the decoder layer, where the
    // decision actually lives, by
    // FrameFlow.Decoding.Tests.HardwareDecodeRequiredTests. It does not belong
    // here: the substrate composes the decoder factory internally with no seam to
    // inject empty capabilities, and VideoDecoder.Open takes them directly.
}
