using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Video.Tests;

/// <summary>Runs alone: its assertions read the process-wide copy counts exactly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FrameCopyCountsCollection
{
    public const string Name = "Frame copy counts (#435)";
}

/// <summary>
/// A converter operator counts the frame it writes (#435), including one that already had the
/// size and format asked for. That copy stays: the converter is the storage boundary that keeps a
/// fixed pool, such as a camera's buffers, from being held by whatever keeps frames after it
/// (ADR-0081).
/// </summary>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class ConverterCopyCountTests : IClassFixture<FfmpegBootstrapFixture>
{
    public static TheoryData<string, int, int, PixelFormat> Conversions => new()
    {
        { "convert", 64, 32, PixelFormat.Rgba32 },
        { "resize", 32, 16, PixelFormat.Bgra32 },
        { "resize-and-convert", 32, 16, PixelFormat.Rgba32 },
        { "convert", 64, 32, PixelFormat.Bgra32 },
        { "resize-and-convert", 64, 32, PixelFormat.Bgra32 },
    };

    [RequiresFfmpegTheory]
    [MemberData(nameof(Conversions))]
    public async Task EachFrameAConverterWrites_IsCounted(string kind, int width, int height, PixelFormat format)
    {
        var node = kind switch
        {
            "convert" => VideoOperators.ConvertPixelFormat(kind, format),
            "resize" => VideoOperators.Resize(kind, width, height),
            _ => VideoOperators.ResizeAndConvert(kind, width, height, format),
        };
        using var frame = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 64, 32, TimeSpan.Zero, TimeSpan.FromMilliseconds(33), 0,
            static (planes, _) => planes.Y.Fill(128));
        var before = FrameCopyMetrics.Snapshot();

        using var output = await node.Body(frame, CancellationToken.None);

        Assert.NotSame(frame, output);
        Assert.Equal(1, FrameCopyMetrics.Snapshot().Since(before)[FrameCopySite.OperatorConvert]);
    }
}
