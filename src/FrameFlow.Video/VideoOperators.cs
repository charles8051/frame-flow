// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Video;

/// <summary>
/// Port of the old <c>VideoPipelineExtensions</c> to the new
/// primitive-set substrate. Each operator is now a factory that
/// builds an <see cref="OperatorNode{TIn, TOut}"/> wrapping the
/// underlying <see cref="IVideoConverter"/> primitive; consumers
/// chain the nodes into a graph with
/// <see cref="GraphChainExtensions.Pipeline{T}(FrameFlow.Graph.Graph, SourceNode{T})"/>.
/// <see cref="FrameFlow.Graph.Graph.Connect{T}(FrameFlow.Graph.OutputPort{T}, FrameFlow.Graph.InputPort{T}, FrameFlow.Graph.EdgeOptions)"/>
/// remains available for an input the chain cannot reach.
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
/// The new surface returns nodes, chained into a graph:
/// <code>
/// var graph = new Graph.Graph();
/// graph.Pipeline(source)
///     .Then(VideoOperators.ConvertPixelFormat("convert", PixelFormat.Bgra32))
///     .Then(VideoOperators.Resize("resize", 640, 480))
///     .To(sink);
/// </code>
/// Every edge is still a real port-to-port connection; the chain
/// names them in order instead of one call per edge. For a fan-out,
/// declare the second consumer with
/// <see cref="GraphChain{T}.Branch(EdgeOptions)"/>.
/// </para>
/// <para>
/// <b>Frame ownership.</b> Each operator returns a new frame, or
/// forwards its input by returning it. The substrate releases the
/// input after the operator returns; the output flows downstream and
/// is released by whatever holds it last (ADR-0080).
/// </para>
/// </remarks>
public static class VideoOperators
{
    /// <summary>
    /// Builds an operator node that converts each upstream frame to
    /// <paramref name="target"/> pixel format, keeping source
    /// dimensions.
    /// </summary>
    public static OperatorNode<IVideoFrame, IVideoFrame> ConvertPixelFormat(
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
    public static OperatorNode<IVideoFrame, IVideoFrame> Resize(
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
    public static OperatorNode<IVideoFrame, IVideoFrame> ResizeAndConvert(
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
    /// Builds an operator node that brings a hardware-resident frame back to CPU memory, and
    /// passes a frame that is already there straight through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the readback every other operator in this class needs and none of them can do.
    /// <see cref="ConvertPixelFormat"/>, <see cref="Resize"/> and <see cref="ResizeAndConvert"/>
    /// all route through <c>SwScaleVideoConverter.Process</c>, which calls <c>ToCpu()</c> on the
    /// incoming frame; on a GPU frame that throws. Put this ahead of them and a decoder yielding
    /// hardware frames can reach a CPU operator:
    /// <code>
    /// graph.Pipeline(source)
    ///     .Then(VideoOperators.ToCpu("readback"))
    ///     .Then(VideoOperators.Resize("resize", 640, 480))
    ///     .To(sink);
    /// </code>
    /// </para>
    /// <para>
    /// <b>The pass-through is the point of putting it in a chain.</b> A graph does not know
    /// whether the decoder bound a hwaccel backend, and the answer can differ per run and per
    /// machine. A chain with this in it is correct either way: on a software decode every frame
    /// is already <see cref="FrameMemoryDomain.Cpu"/> and is forwarded untouched, at the cost of
    /// one enum comparison.
    /// </para>
    /// <para>
    /// <b>Cost when it does read back.</b> One <c>av_hwframe_transfer_data</c> across PCIe, then
    /// one <c>sws_scale</c> to tightly-packed <see cref="PixelFormat.Bgra32"/>. The output format
    /// is Bgra32 regardless of what the frame was on the GPU, which is what
    /// <see cref="FrameFlow.Decoding.GpuVideoFrame.ReadbackToCpuBgra32"/> produces; a chain that
    /// wants something else converts after this, not instead of it.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> A readback allocates a new frame, which goes downstream, and the
    /// substrate releases the input's GPU frame when the body returns, so its decode slice is free
    /// before the next node runs. A CPU frame is forwarded by returning it: the substrate sees the
    /// same object and moves its reference downstream (ADR-0080, decision 3).
    /// </para>
    /// </remarks>
    /// <param name="id">Node id, unique within the graph.</param>
    /// <exception cref="NotSupportedException">
    /// Raised at run time for a <see cref="FrameMemoryDomain.Gpu"/> frame that is not a
    /// <see cref="FrameFlow.Decoding.GpuVideoFrame"/>. The readback is
    /// <c>av_hwframe_transfer_data</c> on an <c>AVFrame</c>, so it has nothing to say about a
    /// GPU frame from some other stack (#232).
    /// </exception>
    public static OperatorNode<IVideoFrame, IVideoFrame> ToCpu(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return new OperatorNode<IVideoFrame, IVideoFrame>(
            id,
            (input, ct) =>
            {
                // Already on the CPU: forward the input itself.
                if (input.MemoryDomain == FrameMemoryDomain.Cpu)
                    return ValueTask.FromResult<IVideoFrame?>(input);

                if (input is not GpuVideoFrame gpu)
                {
                    throw new NotSupportedException(
                        $"ToCpu('{id}') received a GPU frame of type {input.GetType().Name}, which "
                            + "is not a GpuVideoFrame. The readback is av_hwframe_transfer_data on "
                            + "an AVFrame and has no path for another GPU stack."
                    );
                }

                return ValueTask.FromResult<IVideoFrame?>(
                    gpu.ReadbackToCpuBgra32()
                );
            },
            // A CPU frame is forwarded as itself, so this is not a storage boundary for it.
            holding: Holding.InFlight
        );
    }

    /// <summary>
    /// Constructs the node body around an <see cref="IVideoConverter"/>.
    /// The converter is captured by the operator closure and lives
    /// for the lifetime of the graph run; the <c>SafeHandle</c>-wrapped
    /// native context inside is finalized by GC after the run.
    /// </summary>
    private static OperatorNode<IVideoFrame, IVideoFrame> BuildConverterNode(
        string id,
        IVideoConverter converter
    )
    {
        return new OperatorNode<IVideoFrame, IVideoFrame>(
            id,
            (input, ct) =>
            {
                var output = converter.Process(input);
                // VideoConverter.Process returns a fresh frame the
                // caller owns one ref on. Wrap and forward.
                return ValueTask.FromResult<IVideoFrame?>(output);
            },
            // Every output is a new frame, even at the input's size and format.
            holding: Holding.Boundary
        );
    }
}
