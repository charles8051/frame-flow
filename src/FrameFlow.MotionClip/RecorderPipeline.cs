// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Video;

namespace FrameFlow.MotionClip;

/// <summary>
/// Builds the recorder graph. The base topology is
/// <c>source → ResizeAndConvert(display BGRA32) → RecordingGate (operator)
/// → ClipEncoderSink (sink)</c>. The gate-to-sink edge is
/// <see cref="EdgeOptions.Buffered"/> so the gate can queue the next clip
/// while the encoder is still writing the previous one (was the
/// "Motion detected while saving — event dropped" case in the monolithic
/// recorder). When a live <see cref="IVideoSink"/> preview is supplied,
/// it is wired as a sibling consumer of the display-resolution stream, and
/// the two share each frame by reference (ADR-0080).
/// Used identically by the headless (<c>Program</c>) and windowed
/// (<c>MainWindow</c>) hosts and by the camera tracker
/// (<see cref="CameraTracking"/>) so every path runs the same topology.
/// </summary>
internal static class RecorderPipeline
{
    /// <summary>Synthetic capture resolution (a camera uses its native size, capped).</summary>
    public const int CaptureWidth = 800;
    public const int CaptureHeight = 600;

    /// <summary>Display/recording resolution the graph normalises every frame to.</summary>
    public const int DisplayWidth = 640;
    public const int DisplayHeight = 480;

    /// <summary>
    /// Builds the graph. When <paramref name="preview"/> is non-<see langword="null"/>,
    /// the display-resolution stream fans out to two sibling consumers:
    /// the gate (which drives motion detection and clip assembly) and the
    /// preview sink (which renders frames to the UI). Both hold the same
    /// frame by reference (ADR-0080). The
    /// preview edge is <see cref="EdgeOptions.LatestWins(int)"/> so a slow
    /// UI drops frames rather than back-pressuring motion detection. The
    /// gate-to-encoder edge stays <c>Buffered(cap=1)</c> so "save in
    /// progress, drop the next motion event" becomes "queue it."
    /// </summary>
    public static FrameFlow.Graph.Graph BuildGraph(
        SourceNode<IVideoFrame> source,
        RecordingGate gate,
        ClipEncoderSink encoderSink,
        IVideoSink? preview = null
    )
    {
        OperatorNode<IVideoFrame, IVideoFrame> resizeConvert =
            VideoOperators.ResizeAndConvert(
                "resize-convert",
                DisplayWidth,
                DisplayHeight,
                PixelFormat.Bgra32
            );

        OperatorNode<IVideoFrame, ClipSegment> gateNode = gate.Build();
        SinkNode<ClipSegment> encoderNode = encoderSink.Build();

        var graph = new FrameFlow.Graph.Graph();
        GraphChain<IVideoFrame> display = graph.Pipeline(source).Then(resizeConvert);

        // The preview branches off the display stream and shares its frames.
        if (preview is not null)
            display.Branch(EdgeOptions.LatestWins()).To(preview.AsSinkNode("preview-sink"));

        display.Then(gateNode).To(encoderNode, EdgeOptions.Buffered(capacity: 1));
        return graph;
    }

    /// <summary>
    /// The <c>BufferCount</c> a camera session needs to hand this pipeline its frames without
    /// copying: the camera budget of the graph <see cref="BuildGraph"/> wires, and the frame the
    /// push source's bridge holds ahead of it (ADR-0081, decision 4). <see langword="null"/> when
    /// the graph holds camera frames without bound.
    /// </summary>
    public static int? CameraBufferCount(
        RecordingGate gate,
        ClipEncoderSink encoderSink,
        IVideoSink? preview = null
    )
    {
        var standIn = new SourceNode<IVideoFrame>("camera-source", static _ => default);
        var budget = BuildGraph(standIn, gate, encoderSink, preview).FrameBudgetFor(standIn.Output);

        // AsPushVideoFrameSource's bridge holds one frame at its default capacity.
        return budget.Frames + 1;
    }
}
