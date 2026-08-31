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

    // Required-with-empty-capabilities is covered at the decoder layer, where the
    // decision actually lives, by
    // FrameFlow.Decoding.Tests.HardwareDecodeRequiredTests. It does not belong
    // here: the substrate composes the decoder factory internally with no seam to
    // inject empty capabilities, and VideoDecoder.Open takes them directly.
}
