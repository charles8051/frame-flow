using FrameFlow.Decoding;
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

    // Required-with-empty-capabilities is covered at the decoder layer, where the
    // decision actually lives, by
    // FrameFlow.Decoding.Tests.HardwareDecodeRequiredTests. It does not belong
    // here: the substrate composes the decoder factory internally with no seam to
    // inject empty capabilities, and VideoDecoder.Open takes them directly.
}
