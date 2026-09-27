// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.Core;
using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// Holds inference results until the frame each belongs to reaches the screen, and says which one
/// that is. An inference branch runs ahead of the picture; this lines its results up with what the
/// viewer sees.
/// </summary>
/// <typeparam name="TResult">What the model produces.</typeparam>
/// <remarks>
/// <para>
/// When a frame is presented, the result for it is the latest one at or before its timestamp: a
/// branch drops frames while a run is in progress, so most frames show the result before theirs.
/// </para>
/// <para>
/// A result earlier than the one before it means the timeline went back, by a seek or a loop, and
/// the results still waiting are dropped. A presented frame earlier than the result showing, with
/// no result of its own, means the same on the presenting side: the result is cleared and
/// <see cref="Cleared"/> is raised until one arrives for the new position.
/// </para>
/// <para>
/// <see cref="Presented"/> is raised on whichever thread presented the frame, inside the present
/// path. Keep its handlers to handing the result on.
/// </para>
/// </remarks>
public sealed class PresentedResults<TResult> : IDisposable
{
    private readonly IFramePresentedSource _source;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<InferenceResult<TResult>> _pending = [];
    private readonly List<TimeSpan> _timestamps = [];
    private InferenceResult<TResult>? _current;

    // Which timeline the waiting results and the current one came from. A result earlier than the
    // last starts a new one; the waiting results are always from the newest.
    private int _timeline;
    private int _currentTimeline;
    private bool _disposed;

    /// <summary>Follows the frames <paramref name="source"/> presents.</summary>
    /// <param name="source">The video sink or view that presents the frames.</param>
    /// <param name="capacity">The most results held while waiting; the oldest go first.</param>
    public PresentedResults(IFramePresentedSource source, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _source = source;
        _capacity = capacity;
        _source.FramePresented += OnFramePresented;
    }

    /// <summary>Raised when the frame on screen brings a different result.</summary>
    public event EventHandler<InferenceResult<TResult>>? Presented;

    /// <summary>
    /// Raised when the frame on screen went back before the result showing and has none of its
    /// own, so nothing should be shown until the next <see cref="Presented"/>.
    /// </summary>
    public event EventHandler? Cleared;

    /// <summary>The result for the frame on screen, or null before one arrives.</summary>
    public InferenceResult<TResult>? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>Holds <paramref name="result"/> until its frame is presented.</summary>
    public void Post(InferenceResult<TResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (_timestamps.Count > 0 && result.Timestamp < _timestamps[^1])
            {
                _pending.Clear();
                _timestamps.Clear();
                _timeline++;
            }

            _pending.Add(result);
            _timestamps.Add(result.Timestamp);
            if (_pending.Count > _capacity)
            {
                _pending.RemoveAt(0);
                _timestamps.RemoveAt(0);
            }
        }
    }

    /// <summary>Stops following the source.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _source.FramePresented -= OnFramePresented;
    }

    private void OnFramePresented(object? sender, FramePresentedInfo presented)
    {
        InferenceResult<TResult>? changed = null;
        bool cleared = false;
        lock (_gate)
        {
            int index = PresentedMatch.LatestAtOrBefore(_timestamps, presented.PresentationTime);
            if (index < 0)
            {
                // Nothing at or before this frame. If what is showing is from later in the
                // stream, the picture went back past it.
                if (_current is not null && presented.PresentationTime < _current.Timestamp)
                {
                    // What waits from the cleared result's timeline would show it again when the
                    // picture gets back there; what the branch has posted since is for the new one.
                    if (_currentTimeline == _timeline)
                    {
                        _pending.Clear();
                        _timestamps.Clear();
                    }

                    _current = null;
                    cleared = true;
                }
            }
            else
            {
                changed = Match(index);
            }
        }

        if (cleared)
            Cleared?.Invoke(this, EventArgs.Empty);
        else if (changed is not null)
            Presented?.Invoke(this, changed);
    }

    /// <summary>Makes the result at <paramref name="index"/> current; returns it when it changed.</summary>
    private InferenceResult<TResult>? Match(int index)
    {
        var result = _pending[index];
        // Keep the matched result; everything before it has been shown or passed over.
        _pending.RemoveRange(0, index);
        _timestamps.RemoveRange(0, index);
        if (ReferenceEquals(result, _current))
            return null;
        _current = result;
        _currentTimeline = _timeline;
        return result;
    }
}
