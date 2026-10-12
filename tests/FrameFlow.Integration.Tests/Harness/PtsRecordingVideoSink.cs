using System.Collections.Concurrent;
using FrameFlow.Media;

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
    private readonly Lock _gate = new();
    private readonly List<(Func<TimeSpan, bool> Match, TaskCompletionSource<TimeSpan> Signal)> _waiters = [];

    /// <summary>Every frame's PTS, in delivery order.</summary>
    public IReadOnlyList<TimeSpan> PresentedPts => _presented.ToArray();

    /// <summary>How many frames have been delivered.</summary>
    public int Count => _presented.Count;

    /// <summary>
    /// Completes with the PTS of the first frame <paramref name="match"/> accepts, whether it
    /// was delivered before this call or arrives after it, ignoring the first
    /// <paramref name="skip"/> frames delivered.
    /// </summary>
    /// <remarks>
    /// Pass a <see cref="Count"/> read earlier as <paramref name="skip"/> to consider only the
    /// frames delivered since, for instance those of a later playlist item.
    /// </remarks>
    public Task<TimeSpan> WhenPresented(Func<TimeSpan, bool> match, int skip = 0)
    {
        var signal = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_gate)
        {
            foreach (var pts in _presented.Skip(skip))
            {
                if (match(pts))
                {
                    signal.TrySetResult(pts);
                    return signal.Task;
                }
            }
            _waiters.Add((match, signal));
        }
        return signal.Task;
    }

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        var pts = frame.Pts;
        _presented.Enqueue(pts);
        frame.Dispose();

        // Enqueued first, so a waiter registered after this point finds the frame in the queue
        // and one registered before it is found here.
        lock (_gate)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Match(pts))
                {
                    _waiters[i].Signal.TrySetResult(pts);
                    _waiters.RemoveAt(i);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
