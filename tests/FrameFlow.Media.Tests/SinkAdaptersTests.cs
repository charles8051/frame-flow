using FrameFlow.Graph;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Tests for the generic sink adapters that bridge
/// FrameFlow's <see cref="IVideoSink"/> / <see cref="IAudioSink"/>
/// implementations to the substrate's
/// <see cref="SinkNode{TIn}"/> shape.
/// </summary>
public sealed class SinkAdaptersTests
{
    [Fact]
    public async Task IVideoSink_AsSinkNode_ForwardsFramesToConsumer()
    {
        var receivedPts = new List<TimeSpan>();
        var fake = new FakeVideoSink(frame =>
        {
            lock (receivedPts)
                receivedPts.Add(frame.Pts);
        });

        var emitted = 0;
        var source = new SourceNode<VideoFrameRef>(
            "src",
            (ct) =>
            {
                if (emitted >= 3)
                    return ValueTask.FromResult<VideoFrameRef?>(null);
                var frame = new RefCountedTestFrame(ptsSeconds: emitted);
                emitted++;
                return ValueTask.FromResult<VideoFrameRef?>(new VideoFrameRef(frame));
            }
        );

        var sinkNode = fake.AsSinkNode("video-sink");

        var graph = new Graph.Graph();
        graph.Pipeline(source).To(sinkNode);
        await graph.RunAsync();

        Assert.Equal(3, receivedPts.Count);
        Assert.Equal(
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            receivedPts
        );
    }

    [Fact]
    public async Task IVideoSink_AsSinkNode_RefcountBalanced_NoLeaks()
    {
        // Pin the AddRef-and-let-sink-dispose contract: after the
        // graph runs, every emitted frame has refcount 0.
        var live = 0;
        var fake = new FakeVideoSink(frame =>
        {
            // Per the FrameConsumer<T> contract, the sink owns the
            // frame and must dispose it. We do that explicitly here
            // to model what a real sink does after rendering.
            frame.Dispose();
        });

        var emitted = 0;
        var source = new SourceNode<VideoFrameRef>(
            "src",
            (ct) =>
            {
                if (emitted >= 5)
                    return ValueTask.FromResult<VideoFrameRef?>(null);
                emitted++;
                Interlocked.Increment(ref live);
                var frame = new RefCountedTestFrame(
                    ptsSeconds: 0,
                    onLastDispose: () => Interlocked.Decrement(ref live)
                );
                return ValueTask.FromResult<VideoFrameRef?>(new VideoFrameRef(frame));
            }
        );

        var graph = new Graph.Graph();
        graph.Pipeline(source).To(fake.AsSinkNode("v"));
        await graph.RunAsync();

        Assert.Equal(0, Volatile.Read(ref live));
    }

    [Fact]
    public void IVideoSink_AsSinkNode_NullSink_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IVideoSink)null!).AsSinkNode()
        );
    }

    [Fact]
    public void IAudioSink_AsSinkNode_NullSink_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IAudioSink)null!).AsSinkNode()
        );
    }

    // ─── Fakes ──────────────────────────────────────────────────────

    private sealed class FakeVideoSink : IVideoSink
    {
        private readonly Action<IVideoFrame> _onPresent;

        public FakeVideoSink(Action<IVideoFrame> onPresent)
        {
            _onPresent = onPresent;
        }

        public IFramePool FramePool => null!; // not exercised
        public IReadOnlyList<FrameMemoryDomain> SupportedMemoryDomains =>
            [FrameMemoryDomain.Cpu];

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            _onPresent(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Minimum refcounted IVideoFrame for testing. Same shape as in Yolo.Next tests.</summary>
    private sealed class RefCountedTestFrame : IVideoFrame
    {
        private readonly Action? _onLastDispose;
        private int _refCount = 1;

        public RefCountedTestFrame(int ptsSeconds, Action? onLastDispose = null)
        {
            Pts = TimeSpan.FromSeconds(ptsSeconds);
            _onLastDispose = onLastDispose;
        }

        public int Width => 4;
        public int Height => 4;
        public TimeSpan Pts { get; }
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef()
        {
            Interlocked.Increment(ref _refCount);
            return this;
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
                _onLastDispose?.Invoke();
        }

        public CpuFrameData? AsCpu() => null;
        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
