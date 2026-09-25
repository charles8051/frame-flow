using System.Buffers;
using FrameFlow.Graph;
using FrameFlow.Media;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Video.Tests;

/// <summary>
/// <see cref="VideoOperators.ToCpu"/>: the readback node that four doc comments cited before it
/// existed (#279).
/// </summary>
/// <remarks>
/// No FFmpeg and no GPU. The two branches that can be decided without hardware are the two worth
/// pinning: a CPU frame is forwarded by returning it, so its one reference moves downstream, and
/// a GPU frame from a stack this readback cannot serve is refused rather than mishandled. The
/// readback itself needs a hwaccel-bound decoder and belongs to the integration suite.
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

        var output = await node.Body(frame, CancellationToken.None);

        Assert.Same(frame, output);
        Assert.Equal(FrameMemoryDomain.Cpu, output!.MemoryDomain);
    }

    [Fact]
    public async Task CpuFrame_ThroughAGraph_ReachesTheSink_AndIsReleasedOnce()
    {
        // Returning the input is a pass-through: the substrate moves the frame's one reference
        // downstream rather than releasing it. The sink can read it, and the storage goes back
        // exactly once, after the sink.
        var pool = new ReturnCountingPool();
        var frame = MakeCpuFrame(pool: pool);
        int pulls = 0;
        var source = new SourceNode<IVideoFrame>(
            "source",
            _ => ValueTask.FromResult<IVideoFrame?>(pulls++ == 0 ? frame : null)
        );
        bool readable = false;
        var sink = new SinkNode<IVideoFrame>(
            "sink",
            (item, _) =>
            {
                readable = item.AsCpu() is not null;
                return ValueTask.CompletedTask;
            }
        );

        var graph = new GraphRunner();
        graph.Pipeline(source).Then(VideoOperators.ToCpu("readback")).To(sink);
        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(readable);
        Assert.Equal(1, pool.Returns);
        Assert.Null(frame.AsCpu());
    }

    [Fact]
    public async Task GpuFrameFromAnotherStack_IsRefusedByName()
    {
        // #232: a GPU frame that is not an AVFrame wrapper has no path through
        // av_hwframe_transfer_data. Saying so beats an InvalidCastException.
        using var foreign = new ForeignGpuFrame();
        var node = VideoOperators.ToCpu("readback");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await node.Body(foreign, CancellationToken.None)
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

    private static CpuVideoFrame MakeCpuFrame(ArrayPool<byte>? pool = null) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            8,
            8,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(33),
            0,
            static (planes, _) => planes.Y.Clear(),
            pool
        );

    /// <summary>Hands out fresh arrays and counts how many come back.</summary>
    private sealed class ReturnCountingPool : ArrayPool<byte>
    {
        private int _returns;

        public int Returns => Volatile.Read(ref _returns);

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false) =>
            Interlocked.Increment(ref _returns);
    }

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
