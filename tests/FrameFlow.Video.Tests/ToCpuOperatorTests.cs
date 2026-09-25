using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Video.Tests;

/// <summary>
/// <see cref="VideoOperators.ToCpu"/>: the readback node that four doc comments cited before it
/// existed (#279).
/// </summary>
/// <remarks>
/// No FFmpeg and no GPU. The two branches that can be decided without hardware are the two worth
/// pinning: a CPU frame is forwarded untouched, with ownership moving out of the input wrapper
/// rather than being shared, and a GPU frame from a stack this readback cannot serve is refused
/// rather than mishandled. The readback itself needs a
/// hwaccel-bound decoder and belongs to the integration suite.
/// </remarks>
public sealed class ToCpuOperatorTests
{
    [Fact]
    public async Task CpuFrame_IsForwardedUntouched()
    {
        // The pass-through is why the node can sit in a chain unconditionally: a graph does not
        // know whether the decoder bound a hwaccel backend.
        using var frame = MakeCpuFrame();
        var node = VideoOperators.ToCpu("readback");

        var output = await Invoke(node, frame);

        Assert.NotNull(output);
        Assert.Same(frame, output!.Frame);
        Assert.Equal(FrameMemoryDomain.Cpu, output.Frame.MemoryDomain);
    }

    [Fact]
    public async Task CpuFrame_OwnershipMovesOutOfTheInputWrapper()
    {
        // The substrate disposes the input wrapper when the operator returns, and a decoder's
        // CpuVideoFrame cannot be shared by ref counting (#41). So the frame has to leave the
        // input wrapper, or the dispose takes the pixel buffer back while the output still points
        // at it. An emptied input wrapper is what says that happened.
        using var frame = MakeCpuFrame();
        var node = VideoOperators.ToCpu("readback");
        var input = new VideoFrameRef(frame);

        var output = await node.Body(input, CancellationToken.None);

        Assert.Throws<ObjectDisposedException>(() => input.Frame);
        Assert.Same(frame, output!.Frame);

        // And the now-empty wrapper's dispose is the no-op Detach promises: the frame below is
        // still usable afterwards.
        input.Dispose();
        Assert.NotNull(output.Frame.AsCpu());
    }

    [Fact]
    public async Task GpuFrameFromAnotherStack_IsRefusedByName()
    {
        // #232: a GPU frame that is not an AVFrame wrapper has no path through
        // av_hwframe_transfer_data. Saying so beats an InvalidCastException.
        using var foreign = new ForeignGpuFrame();
        var node = VideoOperators.ToCpu("readback");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await Invoke(node, foreign)
        );

        Assert.Contains("readback", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ForeignGpuFrame), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankId_Throws(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => VideoOperators.ToCpu(id!));
    }

    private static async Task<VideoFrameRef?> Invoke(
        OperatorNode<VideoFrameRef, VideoFrameRef> node,
        IVideoFrame frame
    )
    {
        using var input = new VideoFrameRef(frame);
        return await node.Body(input, CancellationToken.None);
    }

    private static CpuVideoFrame MakeCpuFrame(int width = 8, int height = 8) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            width,
            height,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(33),
            0,
            static (planes, _) => planes.Y.Clear()
        );

    /// <summary>A GPU-domain frame that is not a <c>GpuVideoFrame</c>.</summary>
    private sealed class ForeignGpuFrame : IVideoFrame
    {
        public int Width => 8;

        public int Height => 8;

        public TimeSpan Pts => TimeSpan.Zero;

        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);

        public PixelFormat Format => PixelFormat.Bgra32;

        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Gpu;

        public IVideoFrame AddRef() => this;

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();

        public void Dispose() { }
    }
}
