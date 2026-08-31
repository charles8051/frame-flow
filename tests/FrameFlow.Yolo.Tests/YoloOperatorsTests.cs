using FrameFlow.Graph;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// Phase 3 / Yolo.Next acceptance. Uses the delegate overload of
/// <see cref="YoloOperators.DetectWith"/> with a stub detection
/// function, so the test runs without ONNX / CUDA / YOLO model
/// files.
/// </summary>
public sealed class YoloOperatorsTests
{
    [Fact]
    public async Task DetectWith_EmitsPairedFrameAndDetections()
    {
        var captured = new List<(int VideoPts, int DetectionCount)>();
        var emitted = 0;

        // 3 synthetic frames; stub detector emits N detections where
        // N = frame's PTS in seconds (deterministic, easy to assert).
        var source = new SourceNode<VideoFrameRef>(
            "src",
            (ct) =>
            {
                if (emitted >= 3)
                    return ValueTask.FromResult<VideoFrameRef?>(null);
                var frame = MakeFrame(ptsSeconds: emitted);
                emitted++;
                return ValueTask.FromResult<VideoFrameRef?>(new VideoFrameRef(frame));
            }
        );

        // Stub detection: returns a list of length = PTS.TotalSeconds.
        // Just produces predictable shape; doesn't touch pixel data.
        var detect = YoloOperators.DetectWith(
            "detect",
            (IVideoFrame frame) =>
            {
                var n = (int)frame.Pts.TotalSeconds;
                var list = new List<Detection>(n);
                for (int i = 0; i < n; i++)
                    list.Add(new Detection(
                        ClassId: i,
                        ClassName: $"stub-{i}",
                        Confidence: 0.9f,
                        X: 0, Y: 0, Width: 10, Height: 10
                    ));
                return list;
            }
        );

        var sink = new SinkNode<DetectedVideoFrameRef>(
            "sink",
            (item, ct) =>
            {
                lock (captured)
                    captured.Add(((int)item.Video.Frame.Pts.TotalSeconds, item.Detections.Count));
                return ValueTask.CompletedTask;
            }
        );

        var graph = new Graph.Graph();
        graph.Pipeline(source).Then(detect).To(sink);
        await graph.RunAsync();

        Assert.Equal(3, captured.Count);
        Assert.Equal(new[] { (0, 0), (1, 1), (2, 2) }, captured);
    }

    [Fact]
    public async Task DetectWith_FrameRefcount_DisposedAfterSinkConsumes_NoLeak()
    {
        // Pin the ownership protocol: each emitted frame ends up with
        // refcount 0 after the sink disposes. Stub detector + real
        // CpuVideoFrame so we can observe pool/dispose behaviour.
        var live = 0;

        var emitted = 0;
        var source = new SourceNode<VideoFrameRef>(
            "src",
            (ct) =>
            {
                if (emitted >= 2)
                    return ValueTask.FromResult<VideoFrameRef?>(null);
                emitted++;
                Interlocked.Increment(ref live);
                var frame = MakeFrameTracked(() => Interlocked.Decrement(ref live));
                return ValueTask.FromResult<VideoFrameRef?>(new VideoFrameRef(frame));
            }
        );

        var detect = YoloOperators.DetectWith(
            "d",
            (IVideoFrame _) => Array.Empty<Detection>()
        );

        var sink = new SinkNode<DetectedVideoFrameRef>(
            "s",
            (_, _) => ValueTask.CompletedTask
        );

        var graph = new Graph.Graph();
        graph.Pipeline(source).Then(detect).To(sink);
        await graph.RunAsync();

        Assert.Equal(0, Volatile.Read(ref live));
    }

    [Fact]
    public void DetectWith_NullDetectorDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => YoloOperators.DetectWith("d", (Func<IVideoFrame, IReadOnlyList<Detection>>)null!)
        );
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static RefCountedTestFrame MakeFrame(int ptsSeconds) =>
        new(ptsSeconds);

    private static RefCountedTestFrame MakeFrameTracked(Action onDisposed) =>
        new(0, onDisposed);

    /// <summary>
    /// Minimum refcounted <see cref="IVideoFrame"/> implementation for
    /// tests. Skips pixel-data plumbing (the stub detector doesn't read
    /// pixels) so we don't drag in a pool or memory owner. Pin the
    /// underlying refcount with <see cref="Interlocked"/> like the
    /// production <c>FrameFlow.Playback.CpuVideoFrame</c> does.
    /// </summary>
    private sealed class RefCountedTestFrame : IVideoFrame
    {
        private readonly Action? _onDisposed;
        private int _refCount = 1;

        public RefCountedTestFrame(int ptsSeconds, Action? onDisposed = null)
        {
            Pts = TimeSpan.FromSeconds(ptsSeconds);
            _onDisposed = onDisposed;
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
            var remaining = Interlocked.Decrement(ref _refCount);
            if (remaining == 0)
                _onDisposed?.Invoke();
        }

        public CpuFrameData? AsCpu() => null;
        public CpuFrameData ToCpu() =>
            throw new NotSupportedException("Test frame; pixel data not provided.");
    }
}
