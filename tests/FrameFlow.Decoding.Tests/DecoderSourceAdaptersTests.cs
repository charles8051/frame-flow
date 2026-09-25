// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FrameFlow.Decoding.Diagnostics;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for <see cref="DecoderSourceAdapters"/>, the shim that
/// exposes a decoder's <see cref="IAsyncEnumerable{T}"/> as a
/// <see cref="SourceNode{T}"/>. Stub decoder, no FFmpeg, no media
/// files.
/// </summary>
/// <remarks>
/// The two enumerator-disposal assertions pin the adapter's
/// lifecycle contract: the enumerator is disposed at EOS, and
/// disposed before the cancellation exception is rethrown, so
/// native decoder resources don't leak across graph runs. They
/// moved here from the deleted <c>PlaybackGraphTests</c>, which
/// reached the adapter through a wrapper that nothing consumed.
/// </remarks>
public sealed class DecoderSourceAdaptersTests
{
    [Fact]
    public async Task AsSourceNode_EmitsEveryFrameInOrder_AndDisposesEnumeratorAtEos()
    {
        var capturedPts = new List<TimeSpan>();
        var decoder = new StubVideoDecoder(frameCount: 5);

        var graph = new Graph.Graph();
        graph
            .Pipeline(decoder.AsSourceNode("video-source"))
            .To(
                new SinkNode<IVideoFrame>(
                    "video-sink",
                    (item, _) =>
                    {
                        capturedPts.Add(item.Pts);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync();

        Assert.Equal(
            Enumerable.Range(0, 5).Select(i => TimeSpan.FromMilliseconds(i * 33)),
            capturedPts
        );
        Assert.True(decoder.EnumeratorDisposed, "Decoder enumerator should be disposed at EOS.");
    }

    [Fact]
    public async Task AsSourceNode_GraphCancelled_DisposesEnumerator()
    {
        var decoder = new StubVideoDecoder(frameCount: 1000);
        var sinkSawAny = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var graph = new Graph.Graph();
        graph
            .Pipeline(decoder.AsSourceNode("video-source"))
            .To(
                new SinkNode<IVideoFrame>(
                    "video-sink",
                    (_, _) =>
                    {
                        sinkSawAny.TrySetResult();
                        return ValueTask.CompletedTask;
                    }
                )
            );

        using var cts = new CancellationTokenSource();
        var runTask = graph.RunAsync(cts.Token);

        // Cancel only once a frame has reached the sink, so the
        // enumerator is provably mid-iteration rather than not yet
        // created.
        await sinkSawAny.Task;
        await cts.CancelAsync();

        // The substrate either surfaces the cancellation or wins the
        // race and completes. Both are in contract.
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }

        Assert.True(
            decoder.EnumeratorDisposed,
            "Decoder enumerator should be disposed on cancellation."
        );
    }

    // ─── Stubs ──────────────────────────────────────────────────────

    /// <summary>Stub video decoder that yields N synthetic frames.</summary>
    private sealed class StubVideoDecoder : IVideoDecoder
    {
        private readonly int _frameCount;

        public StubVideoDecoder(int frameCount) => _frameCount = frameCount;

        public bool EnumeratorDisposed { get; private set; }

        public async IAsyncEnumerable<IVideoFrame> DecodeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
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
}
