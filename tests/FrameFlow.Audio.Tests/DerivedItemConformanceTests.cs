using FrameFlow.Audio;
using FrameFlow.Media;
using FrameFlow.Tests.Shared;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// The resampler builds a new buffer from each input, so each is on the input's timeline
/// (ADR-0084, decision 3): a converted buffer, an empty one, and what a flush drains.
/// </summary>
public sealed class DerivedItemConformanceTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public DerivedItemConformanceTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegFact]
    public void AResampledBuffer_IsOnItsInputsTimeline()
    {
        _ = _fixture;
        using var resampler = AudioResampler.Create(16_000, 1);
        using var input = DerivedItemConformance.AudioInput(48_000, 2);

        using var output = resampler.Process(input);

        DerivedItemConformance.AssertCarriesTimeline(input, output);
    }

    [RequiresFfmpegFact]
    public void AnEmptyInput_GivesAnEmptyBufferOnItsTimeline()
    {
        using var resampler = AudioResampler.Create(16_000, 1);
        using var primer = DerivedItemConformance.AudioInput(48_000, 2);
        using var _ = resampler.Process(primer);
        using var empty = DerivedItemConformance.AudioInput(48_000, 2, frames: 0);

        using var output = resampler.Process(empty);

        Assert.Equal(0, output.SampleCount);
        DerivedItemConformance.AssertCarriesTimeline(empty, output);
    }

    [RequiresFfmpegFact]
    public void AFlush_IsOnTheLastInputsTimeline()
    {
        // The samples a flush drains are the tail of what was given last.
        using var resampler = AudioResampler.Create(44_100, 2);
        using var first = DerivedItemConformance.AudioInput(48_000, 2);
        using var last = DerivedItemConformance.AudioInput(48_000, 2);
        using var a = resampler.Process(first);
        using var b = resampler.Process(last);

        using var tail = resampler.Flush(last.PresentationTime + last.Duration);

        Assert.NotNull(tail);
        DerivedItemConformance.AssertCarriesTimeline(last, tail);
    }

    [RequiresFfmpegFact]
    public async Task TheResampleNode_IsOnItsInputsTimeline()
    {
        var node = AudioOperators.Resample("resample", 16_000, 1);
        using var input = DerivedItemConformance.AudioInput(48_000, 2);

        try
        {
            using var output = await node.Body(input, CancellationToken.None);

            DerivedItemConformance.AssertCarriesTimeline(input, output!);
        }
        finally
        {
            await node.Cleanup!();
        }
    }
}
