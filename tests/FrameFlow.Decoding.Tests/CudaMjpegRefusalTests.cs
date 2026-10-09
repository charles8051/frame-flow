using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The CUDA MJPEG decoder maps a full-range JPEG as if it were limited range, so shadows and
/// highlights clip (#574). <c>Auto</c> does not bind it for MJPEG, whatever the preference.
/// <c>Required</c> and a borrowed device name the backend, so they keep it.
/// </summary>
/// <remarks>
/// The fixture is a gray ramp whose column <c>x</c> holds <c>x</c>, so a decode that preserves
/// range returns the input at every column.
/// </remarks>
[Collection(DecodePoolCollection.Name)]
public sealed class CudaMjpegRefusalTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly int[] Columns = [0, 1, 8, 16, 17, 32, 128, 220, 235, 236, 255];

    [RequiresCudaMjpegFact]
    public async Task Auto_DoesNotBindCudaForMjpegAndKeepsTheRange()
    {
        var software = await DecodeRampAsync(HardwareDecodeMode.Disabled);

        var auto = await DecodeRampAsync(HardwareDecodeMode.Auto);

        Assert.Null(auto.Bound);
        Assert.Equal(software.Samples, auto.Samples);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_APreferenceForCudaDoesNotOverrideTheRefusal()
    {
        var software = await DecodeRampAsync(HardwareDecodeMode.Disabled);

        var preferred = await DecodeRampAsync(
            HardwareDecodeMode.Auto,
            preferred: [HardwareDecodeBackendKind.Cuda]
        );

        Assert.Null(preferred.Bound);
        Assert.Equal(software.Samples, preferred.Samples);
    }

    [RequiresCudaMjpegFact]
    public async Task Required_StillBindsCuda()
    {
        var required = await DecodeRampAsync(
            HardwareDecodeMode.Required,
            preferred: [HardwareDecodeBackendKind.Cuda]
        );

        Assert.Equal(HardwareDecodeBackendKind.Cuda, required.Bound);
    }

    [RequiresCudaMjpegFact]
    public async Task Auto_ABorrowedCudaDeviceStillBindsCuda()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.Cuda);

        var borrowed = await DecodeRampAsync(HardwareDecodeMode.Auto, device: device);

        Assert.Equal(HardwareDecodeBackendKind.Cuda, borrowed.Bound);
    }

    /// <summary>
    /// Decodes the ramp as a still and returns the backend the decoder bound and the green
    /// channel at <see cref="Columns"/>.
    /// </summary>
    private async Task<(HardwareDecodeBackendKind? Bound, int[] Samples)> DecodeRampAsync(
        HardwareDecodeMode mode,
        IReadOnlyList<HardwareDecodeBackendKind>? preferred = null,
        HardwareDevice? device = null
    )
    {
        using var file = TempFile.With(JpegFixtures.Ramp);
        await using var demux = await OpenAsync(file.Path, still: true);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = mode, PreferredBackends = preferred ?? [] },
            fixture.Capabilities,
            loggerFactory: null,
            videoOptions: device is null ? null : new VideoDecoderOptions { Device = device }
        );
        var bound = decoder.BoundBackend;

        using var frames = await DecodeAllAsync(demux, decoder);

        Assert.Single(frames);
        var samples = Columns
            .Select(c => (int)BgraSample(frames[0], c, row: 32, channel: 1))
            .ToArray();
        return (bound, samples);
    }
}
