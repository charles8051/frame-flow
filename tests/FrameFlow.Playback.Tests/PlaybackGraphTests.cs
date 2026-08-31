using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Graph;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Acceptance tests for the minimal <see cref="PlaybackGraph"/>
/// using stub decoders + a fake video sink (no FFmpeg, no real
/// media files). Pins the end-to-end "decoder → sink" path that
/// the substrate now supports.
/// </summary>
public sealed class PlaybackGraphTests
{
    [Fact]
    public async Task PlayToCompletion_VideoOnly_RunsDecoderToEOS()
    {
        var capturedPts = new List<TimeSpan>();
        var decoder = new StubVideoDecoder(frameCount: 5);
        var sink = new FakeVideoSink(frame =>
        {
            lock (capturedPts)
                capturedPts.Add(frame.Pts);
            frame.Dispose();
        });

        await using var graph = new PlaybackGraph(
            videoDecoder: decoder,
            videoSink: sink
        );

        await graph.PlayToCompletionAsync();

        Assert.Equal(5, capturedPts.Count);
        Assert.Equal(
            Enumerable.Range(0, 5).Select(i => TimeSpan.FromMilliseconds(i * 33)),
            capturedPts
        );
        Assert.True(decoder.EnumeratorDisposed, "Decoder enumerator should be disposed at EOS.");
    }

    [Fact]
    public async Task PlayToCompletion_Cancellation_DisposesDecoderCleanly()
    {
        var decoder = new StubVideoDecoder(frameCount: 1000);
        var sinkSawAny = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new FakeVideoSink(frame =>
        {
            sinkSawAny.TrySetResult();
            frame.Dispose();
        });

        await using var graph = new PlaybackGraph(
            videoDecoder: decoder,
            videoSink: sink
        );

        using var cts = new CancellationTokenSource();
        var runTask = graph.PlayToCompletionAsync(cts.Token);

        // Wait for first frame to flow, then cancel.
        await sinkSawAny.Task;
        cts.Cancel();

        // Cancellation should propagate; substrate emits an OCE or
        // completes normally depending on race. Either is fine.
        try { await runTask; }
        catch (OperationCanceledException) { }

        Assert.True(decoder.EnumeratorDisposed, "Decoder enumerator should be disposed on cancellation.");
    }

    [Fact]
    public async Task PlayToCompletion_NoSink_GracefullyDrains()
    {
        // Decoder present but no sink — graph has nothing to wire,
        // but the constructor allows decoder-only construction for
        // the audio-only or video-only case. Verify it doesn't crash.
        var decoder = new StubVideoDecoder(frameCount: 3);

        await using var graph = new PlaybackGraph(videoDecoder: decoder);

        // The graph builds nothing (no sink to terminate at).
        // RunAsync on an empty graph returns immediately.
        await graph.PlayToCompletionAsync();

        // Decoder was never invoked because no source/sink got wired.
        Assert.False(decoder.EnumeratorDisposed);
    }

    [Fact]
    public void Constructor_NoDecoders_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PlaybackGraph());
    }

    // ─── Stubs ──────────────────────────────────────────────────────

    /// <summary>Stub video decoder that yields N synthetic frames.</summary>
    private sealed class StubVideoDecoder : IVideoDecoder
    {
        private readonly int _frameCount;
        public bool EnumeratorDisposed { get; private set; }

        public StubVideoDecoder(int frameCount) => _frameCount = frameCount;

        public async IAsyncEnumerable<IVideoFrame> DecodeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                for (int i = 0; i < _frameCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return new StubVideoFrame(TimeSpan.FromMilliseconds(i * 33));
                }
            }
            finally
            {
                EnumeratorDisposed = true;
            }
        }

        public void ResetPacketQueue() { }
        public void Flush() { }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public void CompletePacketQueue() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public VideoDecoderDiagnosticsSnapshot GetDiagnostics() =>
            VideoDecoderDiagnosticsSnapshot.Empty;
    }

    private sealed class StubVideoFrame : IVideoFrame
    {
        private int _refCount = 1;
        public StubVideoFrame(TimeSpan pts) => Pts = pts;
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
        public void Dispose() => Interlocked.Decrement(ref _refCount);
        public CpuFrameData? AsCpu() => null;
        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }

    private sealed class FakeVideoSink : IVideoSink
    {
        private readonly Action<IVideoFrame> _onPresent;
        public FakeVideoSink(Action<IVideoFrame> onPresent) => _onPresent = onPresent;
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            _onPresent(frame);
            return ValueTask.CompletedTask;
        }
        public IFramePool FramePool => null!;
        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
