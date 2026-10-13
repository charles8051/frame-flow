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
/// <b>Timelines.</b> A result carries its frame's timeline (<see cref="InferenceResult{TResult}.Timeline"/>)
/// and a presented frame names the timeline on screen (<see cref="FramePresentedInfo.Timeline"/>),
/// so a load, a seek, a loop or the next item is known exactly (ADR-0084). A frame shows only a
/// result of its own timeline. When a new timeline reaches the screen, the results of the one it
/// left are dropped, the one showing included, and a result of a timeline already left is dropped
/// when it is posted. A result of a timeline not yet shown waits until it is.
/// </para>
/// <para>
/// <b>Without timelines.</b> A result on <see cref="Graph.TimelineId.None"/>, or a presenter that
/// names none, keeps the rules that use timestamps alone. A result earlier than the one before it
/// means the timeline went back, by a seek or a loop, and the results still waiting are dropped. A
/// presented frame earlier than the result showing, with no result of its own, means the same on the
/// presenting side: the result is cleared until one arrives for the new position. These rules miss
/// a new item whose timestamps do not go back. A result on <see cref="Graph.TimelineId.None"/>
/// keeps them even when the presenter names its timelines: nothing says which timeline the result
/// belongs to, so a new timeline on screen does not drop it, and it matches whichever is on screen.
/// </para>
/// <para>
/// <see cref="Presented"/> and <see cref="Cleared"/> are raised on whichever thread presented the
/// frame, inside the present path. Keep their handlers to handing the result on.
/// </para>
/// </remarks>
public sealed class PresentedResults<TResult> : IDisposable
{
    private readonly IFramePresentedSource _source;
    private readonly object _gate = new();
    private PresentedResultsState<TResult> _state;
    private bool _disposed;

    /// <summary>Follows the frames <paramref name="source"/> presents.</summary>
    /// <param name="source">The video sink or view that presents the frames.</param>
    /// <param name="capacity">The most results held while waiting; the oldest go first.</param>
    public PresentedResults(IFramePresentedSource source, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _source = source;
        _state = PresentedResultsState<TResult>.Empty(capacity);
        _source.FramePresented += OnFramePresented;
    }

    /// <summary>Raised when the frame on screen brings a different result.</summary>
    public event EventHandler<InferenceResult<TResult>>? Presented;

    /// <summary>
    /// Raised when the result showing no longer applies and the frame on screen has none of its
    /// own: the frame is on a new timeline, or went back before the result. Nothing should be shown
    /// until the next <see cref="Presented"/>.
    /// </summary>
    public event EventHandler? Cleared;

    /// <summary>The result for the frame on screen, or null before one arrives.</summary>
    public InferenceResult<TResult>? Current
    {
        get
        {
            lock (_gate)
                return _state.Current;
        }
    }

    /// <summary>
    /// Holds <paramref name="result"/> until its frame is presented, or drops it when its timeline
    /// has already left the screen.
    /// </summary>
    public void Post(InferenceResult<TResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
            _state = _state.Post(result);
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
        PresentedChange change;
        InferenceResult<TResult>? current;
        lock (_gate)
        {
            (_state, change) = _state.Present(presented.PresentationTime, presented.Timeline);
            current = _state.Current;
        }

        if (change == PresentedChange.Cleared)
            Cleared?.Invoke(this, EventArgs.Empty);
        else if (change == PresentedChange.Presented && current is not null)
            Presented?.Invoke(this, current);
    }
}
