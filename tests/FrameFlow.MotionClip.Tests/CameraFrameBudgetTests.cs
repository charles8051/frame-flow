using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrameFlow.MotionClip.Tests;

/// <summary>
/// The camera budgets of the two camera graphs in the tree (ADR-0081, decision 3). Both convert
/// the camera's frame first, which is a storage boundary, so each holds three camera buffers:
/// the source pump's frame, the edge's and the converter's.
/// </summary>
public sealed class CameraFrameBudgetTests
{
    [Fact]
    public Task TheMotionClipRecorder_WithAPreview() =>
        WithEncoder(encoder =>
        {
            var source = StandIn();
            var graph = RecorderPipeline.BuildGraph(source, Gate(), encoder, new NullVideoSink());

            Assert.Equal(3, graph.FrameBudgetFor(source.Output).Frames);
        });

    [Fact]
    public Task TheMotionClipRecorder_Headless() =>
        WithEncoder(encoder =>
        {
            var source = StandIn();
            var graph = RecorderPipeline.BuildGraph(source, Gate(), encoder, preview: null);

            Assert.Equal(3, graph.FrameBudgetFor(source.Output).Frames);
        });

    [Fact]
    public Task TheRecordersSession_NeedsItsGraphsBudgetAndTheBridgesFrame() =>
        WithEncoder(encoder =>
        {
            Assert.Equal(4, RecorderPipeline.CameraBufferCount(Gate(), encoder, new NullVideoSink()));
            Assert.Equal(4, RecorderPipeline.CameraBufferCount(Gate(), encoder, preview: null));
        });

    /// <summary>
    /// <c>examples/FrameFlow.Examples.Camera.Multicast</c>: the camera source, a pixel format
    /// conversion, and a sink that presents to three panes. The sink declares nothing, and it
    /// sits past the conversion, so it does not count.
    /// </summary>
    [Fact]
    public void CameraMulticast()
    {
        var source = StandIn();
        var graph = new FrameFlow.Graph.Graph();
        graph
            .Pipeline(source)
            .Then(VideoOperators.ConvertPixelFormat("camera-convert", PixelFormat.Bgra32))
            .To(new SinkNode<IVideoFrame>("broadcast-fanout", (_, _) => ValueTask.CompletedTask));

        Assert.Equal(3, graph.FrameBudgetFor(source.Output).Frames);
    }

    /// <summary>The camera source's place in the graph; the budget only looks downstream of it.</summary>
    private static SourceNode<IVideoFrame> StandIn() =>
        new("camera-source", static _ => default);

    private static RecordingGate Gate() =>
        new(
            new RecordingGateOptions { PreRollFrames = 60, PostRollFrames = 90, MaxFramesPerClip = 900 },
            NullLogger.Instance
        );

    /// <summary>Runs <paramref name="test"/> against an encoder, then removes its directory.</summary>
    private static async Task WithEncoder(Action<ClipEncoderSink> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "frameflow-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var encoder = new ClipEncoderSink(
                new ClipEncoderOptions { OutputDirectory = directory, FrameRate = 30 },
                NullLogger.Instance
            );
            test(encoder);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
