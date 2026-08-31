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
/// it is wired as a sibling consumer of the display-resolution stream via
/// a fan-out edge with an explicit <c>CloneCpu</c> cloner — see ADR-0054.
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
    /// preview sink (which renders frames to the UI). The preview branch
    /// carries an explicit cloner so the substrate hands the sink an
    /// independent <see cref="VideoFrameExtensions.CloneCpu"/> per frame —
    /// required because the converter output is a one-shot frame type whose
    /// <see cref="IRefCounted.AddRef"/> throws by design (ADR-0054). The
    /// preview edge is <see cref="EdgeOptions.LatestWins(int)"/> so a slow
    /// UI drops frames rather than back-pressuring motion detection. The
    /// gate-to-encoder edge stays <c>Buffered(cap=1)</c> so "save in
    /// progress, drop the next motion event" becomes "queue it."
    /// </summary>
    public static FrameFlow.Graph.Graph BuildGraph(
        SourceNode<VideoFrameRef> source,
        RecordingGate gate,
        ClipEncoderSink encoderSink,
        IVideoSink? preview = null
    )
    {
        OperatorNode<VideoFrameRef, VideoFrameRef> resizeConvert =
            VideoOperators.ResizeAndConvert(
                "resize-convert",
                DisplayWidth,
                DisplayHeight,
                PixelFormat.Bgra32
            );

        OperatorNode<VideoFrameRef, ClipSegment> gateNode = gate.Build();
        SinkNode<ClipSegment> encoderNode = encoderSink.Build();

        var graph = new FrameFlow.Graph.Graph();
        graph.Connect(source.Output, resizeConvert.Input);
        // Gate branch first (cloner-less) so the substrate's ForwardAsync
        // hands it the inherited ref directly; only the preview branch
        // pays the CloneCpu cost.
        graph.Connect(resizeConvert.Output, gateNode.Input);

        if (preview is not null)
        {
            SinkNode<VideoFrameRef> previewNode = preview.AsSinkNode("preview-sink");
            graph.Connect(
                resizeConvert.Output,
                previewNode.Input,
                EdgeOptions
                    .LatestWins()
                    .WithCloner<VideoFrameRef>(
                        input => new VideoFrameRef(input.Frame.CloneCpu())
                    )
            );
        }

        graph.Connect(gateNode.Output, encoderNode.Input, EdgeOptions.Buffered(capacity: 1));
        return graph;
    }
}
