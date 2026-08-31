using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// <see cref="IVideoSink"/> that copies every presented frame's pixel
/// data into a heap buffer and retains it as a <see cref="VideoCapture"/>.
/// Owns a real <see cref="CpuFramePool"/> so frame-pool backpressure
/// stays faithful to production.
/// </summary>
/// <remarks>
/// <para>
/// Differs from <see cref="HarnessVideoSink"/> in one axis: this sink
/// copies the pixel bytes off the pool frame before it's returned, so
/// captures survive the pool recycle. Tests that don't need pixel data
/// stay on <see cref="HarnessVideoSink"/>.
/// </para>
/// <para>
/// Unlike <see cref="HarnessVideoSink"/>, this sink does NOT run a
/// background 16 ms pump — it consumes the frame synchronously inside
/// <see cref="PresentAsync"/>, copies the pixels, and disposes the
/// frame to return the pool slot. The 16 ms pump in the lifecycle
/// harness models the SDL/Avalonia split-thread render cadence; the
/// content-capture sink doesn't model a renderer at all, it just
/// snapshots.
/// </para>
/// </remarks>
internal sealed class CapturingVideoSink : IVideoSink
{
    private readonly List<VideoCapture> _captures = new();
    private readonly Lock _capturesLock = new();
    private int _formatChangedCount;

    public CapturingVideoSink()
    {
    }

    public IFramePool FramePool { get; } =
        new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);


    /// <summary>Snapshot of all captured frames in arrival order.</summary>
    public IReadOnlyList<VideoCapture> Captures
    {
        get
        {
            lock (_capturesLock)
            {
                return _captures.ToArray();
            }
        }
    }

    /// <summary>Total <see cref="OnFormatChangedAsync"/> calls received.</summary>
    public int FormatChangedCount => Volatile.Read(ref _formatChangedCount);

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            var capture = FramePacker.Pack(frame);
            lock (_capturesLock)
            {
                _captures.Add(capture);
            }
        }
        finally
        {
            // Sink owns the frame per IVideoSink contract; return the pool
            // slot immediately so the worker's RentAsync doesn't backpressure.
            frame.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        Interlocked.Increment(ref _formatChangedCount);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (FramePool is IAsyncDisposable asyncPool)
            return asyncPool.DisposeAsync();
        if (FramePool is IDisposable syncPool)
            syncPool.Dispose();
        return ValueTask.CompletedTask;
    }
}
