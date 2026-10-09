using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A hardware decoder can bind and then refuse the stream at its first packet. Under
/// <see cref="HardwareDecodeMode.Auto"/> the decoder reopens on the software decoder and resends
/// that packet; under <see cref="HardwareDecodeMode.Required"/> the fault stands (#572).
/// </summary>
/// <remarks>
/// The CUDA MJPEG decoder accepts a baseline JPEG and refuses a progressive one, which is the
/// smallest stream that binds and then faults. <c>Auto</c> does not bind CUDA for MJPEG at all
/// (#574), so these tests open the decoder with no known refusals to reach it.
/// </remarks>
[Collection(DecodePoolCollection.Name)]
public sealed class HardwareDecodeFirstPacketFallbackTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    [RequiresCudaMjpegFact]
    public async Task Auto_AProgressiveJpegTheHardwareDecoderRefuses_DecodesInSoftware()
    {
        using var file = TempFile.With(JpegFixtures.Progressive);
        await using var demux = await OpenAsync(file.Path);
        await using var decoder = OpenOnCuda(demux, HardwareDecodeMode.Auto);
        Assert.Equal(HardwareDecodeBackendKind.Cuda, decoder.BoundBackend);

        using var frames = await DecodeAllAsync(demux, decoder);

        Assert.Single(frames);
        Assert.IsNotType<GpuVideoFrame>(frames[0]);
        Assert.Equal(256, frames[0].Width);
        Assert.Equal(144, frames[0].Height);
        Assert.Null(decoder.HardwareBackend);
        Assert.Null(decoder.BoundBackend);
    }

    [RequiresCudaMjpegFact]
    public async Task Required_AProgressiveJpegTheHardwareDecoderRefuses_Faults()
    {
        using var file = TempFile.With(JpegFixtures.Progressive);
        await using var demux = await OpenAsync(file.Path);
        await using var decoder = OpenOnCuda(demux, HardwareDecodeMode.Required);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var frames = await DecodeAllAsync(demux, decoder);
        });

        Assert.Contains("avcodec_send_packet", ex.Message);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_ABaselineJpegTheHardwareDecoderAccepts_StaysOnTheBackend()
    {
        using var file = TempFile.With(JpegFixtures.Baseline);
        await using var demux = await OpenAsync(file.Path);
        await using var decoder = OpenOnCuda(demux, HardwareDecodeMode.Auto);

        using var frames = await DecodeAllAsync(demux, decoder);

        Assert.Single(frames);
        Assert.Equal(HardwareDecodeBackendKind.Cuda, decoder.HardwareBackend);
        Assert.Equal(HardwareDecodeBackendKind.Cuda, decoder.BoundBackend);
    }

    private VideoDecoder OpenOnCuda(DemuxSession demux, HardwareDecodeMode mode) =>
        VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions
            {
                Mode = mode,
                PreferredBackends = [HardwareDecodeBackendKind.Cuda],
            },
            fixture.Capabilities,
            videoOptions: null,
            logger: null,
            refusals: []
        );
}

/// <summary>Skipped unless a CUDA device initialised here and its MJPEG decoder advertises a hardware config.</summary>
internal sealed class RequiresCudaMjpegFactAttribute : FactAttribute
{
    public RequiresCudaMjpegFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        var capabilities = FfmpegBootstrapFixture.ReadCapabilities();
        var cuda = new HardwareDecodeCapabilities(
            capabilities
                .Available.Where(b => b.Kind == HardwareDecodeBackendKind.Cuda && b.Initialized)
                .ToList()
        );
        if (cuda.Available.Count == 0)
        {
            Skip = "No CUDA device initialised on this machine.";
            return;
        }

        if (!VideoDecoder.HasHardwareCandidate(7, cuda))
            Skip = "The MJPEG decoder advertises no CUDA hardware config here.";
    }
}
