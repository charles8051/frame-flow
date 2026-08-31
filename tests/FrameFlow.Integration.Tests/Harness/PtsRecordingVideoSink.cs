using System.Collections.Concurrent;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// An <see cref="IVideoSink"/> that records the PTS of every frame it is handed, in order,
/// and releases each frame immediately.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="HarnessVideoSink"/>, which models a real presenter: it keeps a
/// latest-wins pending slot and consumes it on a ~16 ms pump, so frames delivered faster
/// than that are counted as dropped rather than recorded. That is the right shape for
/// testing backpressure and the wrong shape for testing <i>which</i> frames were delivered,
/// because the ones a defect delivers wrongly are exactly the ones it discards.
/// </para>
/// <para>
/// Releasing on arrival also keeps this sink from being the bottleneck, so what it records
/// is what the pacer decided rather than what a slow consumer allowed through.
/// </para>
/// </remarks>
internal sealed class PtsRecordingVideoSink : IVideoSink
{
    private readonly ConcurrentQueue<TimeSpan> _presented = new();

    /// <summary>Every frame's PTS, in delivery order.</summary>
    public IReadOnlyList<TimeSpan> PresentedPts => _presented.ToArray();

    public IFramePool FramePool { get; } =
        new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 8);

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        _presented.Enqueue(frame.Pts);
        frame.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
