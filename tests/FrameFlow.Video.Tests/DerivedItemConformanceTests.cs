using FrameFlow.Media;
using FrameFlow.Tests.Shared;

namespace FrameFlow.Video.Tests;

/// <summary>
/// The converters build a new frame from each input, so each carries the input's timeline, pixel
/// shape and rotation over (ADR-0084, decision 3). The readback and the upload are covered with
/// the decoders, which own them.
/// </summary>
public sealed class DerivedItemConformanceTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public DerivedItemConformanceTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegTheory]
    [InlineData(PixelFormat.Bgra32, PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Yuv420P, PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Nv12, PixelFormat.Rgba32)]
    public void AConversion_CarriesItsInputsLineage(PixelFormat from, PixelFormat to)
    {
        _ = _fixture;
        using var input = DerivedItemConformance.VideoInput(from);
        using var converter = VideoConverter.Create(targetFormat: to);

        using var converted = converter.Process(input);

        DerivedItemConformance.AssertCarriesLineage(input, converted);
    }

    [RequiresFfmpegFact]
    public async Task TheConvertNode_CarriesItsInputsLineage()
    {
        using var input = DerivedItemConformance.VideoInput(PixelFormat.Yuv420P);
        var node = VideoOperators.ConvertPixelFormat("convert", PixelFormat.Bgra32);

        using var converted = await node.Body(input, CancellationToken.None);

        DerivedItemConformance.AssertCarriesLineage(input, converted!);
    }

    [RequiresFfmpegFact]
    public async Task AResize_CarriesItsInputsTimelineAndRotation()
    {
        // A resize shows the same picture on another grid, so its pixel shape is rescaled to keep
        // the display shape (#542). The timeline and the rotation are the input's.
        using var input = DerivedItemConformance.VideoInput(PixelFormat.Bgra32, width: 16, height: 8);
        var node = VideoOperators.Resize("resize", 32, 8);

        using var resized = await node.Body(input, CancellationToken.None);

        Assert.Equal(input.Timeline, resized!.Timeline);
        Assert.Equal(input.Rotation, resized.Rotation);
    }
}
