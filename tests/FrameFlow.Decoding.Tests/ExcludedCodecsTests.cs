using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A codec in <see cref="HardwareDecodeOptions.ExcludedCodecs"/> decodes in software under
/// <see cref="HardwareDecodeMode.Auto"/> and fails as unbound under
/// <see cref="HardwareDecodeMode.Required"/>, whatever backend could decode it. The CUDA MJPEG
/// decoder is the backend that binds here, with the known refusal switched off so the exclusion is
/// what removes it.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class ExcludedCodecsTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    [RequiresCudaMjpegFact]
    public async Task WithoutTheExclusion_AutoBindsCuda()
    {
        var bound = await OpenAndDecodeAsync(HardwareDecodeMode.Auto, excluded: []);

        Assert.Equal(HardwareDecodeBackendKind.Cuda, bound);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_ExcludingTheCodecDecodesInSoftware()
    {
        var bound = await OpenAndDecodeAsync(HardwareDecodeMode.Auto, excluded: ["mjpeg"]);

        Assert.Null(bound);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_TheExclusionIgnoresCase()
    {
        var bound = await OpenAndDecodeAsync(HardwareDecodeMode.Auto, excluded: ["MJPEG"]);

        Assert.Null(bound);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_ExcludingAnotherCodecLeavesThisOneOnHardware()
    {
        var bound = await OpenAndDecodeAsync(HardwareDecodeMode.Auto, excluded: ["av1", "h264"]);

        Assert.Equal(HardwareDecodeBackendKind.Cuda, bound);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_ABorrowedDeviceDoesNotOverrideTheExclusion()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.Cuda);

        var bound = await OpenAndDecodeAsync(
            HardwareDecodeMode.Auto,
            excluded: ["mjpeg"],
            device: device
        );

        Assert.Null(bound);
    }

    [RequiresCudaMjpegFact]
    public async Task Required_ExcludingTheCodecFailsAndSaysWhy()
    {
        using var file = TempFile.With(JpegFixtures.Baseline);
        await using var demux = await OpenAsync(file.Path);

        var ex = Assert.Throws<HardwareDecodeUnavailableException>(() => Open(
            demux,
            HardwareDecodeMode.Required,
            excluded: ["mjpeg"]
        ));

        Assert.Equal("mjpeg", ex.CodecName);
        Assert.Contains("ExcludedCodecs", ex.Message);
        Assert.Empty(ex.Attempts);
    }

    [RequiresCudaMjpegFact]
    public async Task Required_WithoutTheExclusionStillBinds()
    {
        var bound = await OpenAndDecodeAsync(HardwareDecodeMode.Required, excluded: []);

        Assert.Equal(HardwareDecodeBackendKind.Cuda, bound);
    }

    private async Task<HardwareDecodeBackendKind?> OpenAndDecodeAsync(
        HardwareDecodeMode mode,
        IReadOnlyList<string> excluded,
        HardwareDevice? device = null
    )
    {
        using var file = TempFile.With(JpegFixtures.Baseline);
        await using var demux = await OpenAsync(file.Path);
        await using var decoder = Open(demux, mode, excluded, device);
        var bound = decoder.BoundBackend;

        using var frames = await DecodeAllAsync(demux, decoder);

        Assert.Single(frames);
        return bound;
    }

    // The known refusal is switched off, so an exclusion is the only thing that can keep CUDA off.
    private VideoDecoder Open(
        DemuxSession demux,
        HardwareDecodeMode mode,
        IReadOnlyList<string> excluded,
        HardwareDevice? device = null
    ) =>
        VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions
            {
                Mode = mode,
                PreferredBackends = [HardwareDecodeBackendKind.Cuda],
                ExcludedCodecs = excluded,
            },
            CudaOnly(),
            device is null ? null : new VideoDecoderOptions { Device = device },
            logger: null,
            refusals: []
        );

    private HardwareDecodeCapabilities CudaOnly() =>
        new(
            fixture
                .Capabilities.Available.Where(b =>
                    b.Kind == HardwareDecodeBackendKind.Cuda && b.Initialized
                )
                .ToList()
        );
}
