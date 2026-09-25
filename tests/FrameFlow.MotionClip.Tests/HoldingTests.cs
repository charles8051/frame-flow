using FrameFlow.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrameFlow.MotionClip.Tests;

/// <summary>
/// What the recorder's gate and encoder declare they hold (ADR-0081). A segment carries the
/// clip's frames, so the gate says how many each carries.
/// </summary>
public sealed class HoldingTests
{
    [Fact]
    public void TheGate_HoldsItsLongerBuffer_AndSaysHowManyFramesASegmentCarries()
    {
        var gate = new RecordingGate(
            new RecordingGateOptions
            {
                PreRollFrames = 60,
                PostRollFrames = 90,
                MaxFramesPerClip = 900,
            },
            NullLogger.Instance
        );

        var holding = gate.Build().Holding;

        Assert.Equal(901, holding.MaxHeld);
        Assert.True(holding.ForwardsStorage);
        Assert.Equal(900, holding.FramesPerOutputItem);
    }

    [Fact]
    public async Task TheEncoder_HoldsItsQueueTheSegmentItEncodesAndOneWaiting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "frameflow-holding-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var encoder = new ClipEncoderSink(
                new ClipEncoderOptions { OutputDirectory = directory, FrameRate = 30, QueueCapacity = 4 },
                NullLogger.Instance
            );

            Assert.Equal(6, encoder.Build().Holding.MaxHeld);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
