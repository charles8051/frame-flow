// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Video;

/// <summary>
/// Port of <see cref="VideoPipelineExtensions"/> to the new
/// primitive-set substrate. Each operator is now a factory that
/// builds an <see cref="OperatorNode{TIn, TOut}"/> wrapping the
/// underlying <see cref="IVideoConverter"/> primitive; consumers
/// connect the node's input/output ports via
/// <see cref="Graph.Connect"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Port-vs-pipeline shape difference.</b> The old surface returned
/// a <c>FramePipeline&lt;IVideoFrame&gt;</c> that callers chained:
/// <code>
/// var pipeline = source.AsPipeline()
///     .ConvertPixelFormat(PixelFormat.Bgra32)
///     .Resize(640, 480);
/// </code>
/// The new surface returns nodes; callers wire them via the graph:
/// <code>
/// var graph = new Graph.Graph();
/// var convert = VideoOperators.ConvertPixelFormat("convert", PixelFormat.Bgra32);
/// var resize = VideoOperators.Resize("resize", 640, 480);
/// graph.Connect(source.Output, convert.Input);
/// graph.Connect(convert.Output, resize.Input);
/// </code>
/// More explicit (each edge is visible) at the cost of more lines.
/// Fluent sugar over this shape is straightforward to add as a
/// follow-up (extension methods on <see cref="OutputPort{T}"/> that
/// take a <see cref="Graph"/> and chain a node) but isn't this
/// port's scope.
/// </para>
/// <para>
/// <b>Frame ownership.</b> Each operator wraps its output frame in
/// a fresh <see cref="VideoFrameRef"/>. The substrate disposes the
/// input wrapper after the operator returns (releasing the input's
/// ref on the underlying frame); the output wrapper flows downstream
/// and is eventually disposed by the sink (releasing the output
/// frame's ref). Refcount discipline is identical to the old
/// substrate, just enforced uniformly by the wrapper + the
/// always-refcount substrate protocol instead of operator-author
/// discipline.
/// </para>
/// </remarks>
public static class VideoOperators
{
    /// <summary>
    /// Builds an operator node that converts each upstream frame to
    /// <paramref name="target"/> pixel format, keeping source
    /// dimensions.
    /// </summary>
    public static OperatorNode<VideoFrameRef, VideoFrameRef> ConvertPixelFormat(
        string id,
        PixelFormat target
    )
    {
        // Same lifetime story as original FrameFlow.Video: the converter
        // is captured by the node body closure and outlives the graph run
        // until GC reclaims the closure. SwsContextHandle is a SafeHandle
        // so native cleanup is guaranteed by the finalizer.
#pragma warning disable CA2000
        var converter = VideoConverter.Create(targetFormat: target);
#pragma warning restore CA2000
        return BuildConverterNode(id, converter);
    }

    /// <summary>
    /// Builds an operator node that resizes each upstream frame to
    /// <paramref name="width"/> × <paramref name="height"/>, keeping
    /// source pixel format.
    /// </summary>
    public static OperatorNode<VideoFrameRef, VideoFrameRef> Resize(
        string id,
        int width,
        int height
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
#pragma warning disable CA2000
        var converter = VideoConverter.Create(targetWidth: width, targetHeight: height);
#pragma warning restore CA2000
        return BuildConverterNode(id, converter);
    }

    /// <summary>
    /// Builds an operator node that resizes AND converts in a single
    /// swscale pass.
    /// </summary>
    public static OperatorNode<VideoFrameRef, VideoFrameRef> ResizeAndConvert(
        string id,
        int width,
        int height,
        PixelFormat targetFormat
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
#pragma warning disable CA2000
        var converter = VideoConverter.Create(
            targetWidth: width,
            targetHeight: height,
            targetFormat: targetFormat
        );
#pragma warning restore CA2000
        return BuildConverterNode(id, converter);
    }

    /// <summary>
    /// Constructs the node body around an <see cref="IVideoConverter"/>.
    /// The converter is captured by the operator closure and lives
    /// for the lifetime of the graph run; the <c>SafeHandle</c>-wrapped
    /// native context inside is finalized by GC after the run.
    /// </summary>
    private static OperatorNode<VideoFrameRef, VideoFrameRef> BuildConverterNode(
        string id,
        IVideoConverter converter
    )
    {
        return new OperatorNode<VideoFrameRef, VideoFrameRef>(
            id,
            (input, ct) =>
            {
                var output = converter.Process(input.Frame);
                // VideoConverter.Process returns a fresh frame the
                // caller owns one ref on. Wrap and forward.
                return ValueTask.FromResult<VideoFrameRef?>(new VideoFrameRef(output));
            }
        );
    }
}
