// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.MotionClip;

/// <summary>
/// A closed bundle of captured frames representing one motion-triggered clip,
/// flowing as a graph item from <see cref="RecordingGate"/> to
/// <see cref="ClipEncoderSink"/>. Owns the frames; disposing the segment (when
/// the substrate's refcount reaches zero) disposes them in playback order.
/// </summary>
/// <remarks>
/// Implements <see cref="IRefCounted"/> so the substrate can manage its
/// lifecycle. There is normally a single consumer (the encoder sink), so
/// refcount stays at 1 and disposal disposes the frames; fan-out callers that
/// <see cref="AddRef"/> are responsible for matching <see cref="Dispose"/> calls.
/// </remarks>
public sealed class ClipSegment : IRefCounted
{
    private int _refCount = 1;
    private List<IVideoFrame>? _frames;

    /// <summary>
    /// Constructs a segment that takes ownership of the supplied frame list.
    /// The list MUST NOT be mutated by the caller after construction; the
    /// segment owns each <see cref="IVideoFrame"/> and disposes it on its own
    /// disposal.
    /// </summary>
    /// <param name="frames">Frames in playback order (pre-roll + trigger + post-roll).</param>
    /// <param name="triggeredAt">UTC time the trigger fired (start-of-motion).</param>
    /// <param name="preRollCount">Frames at the head of <paramref name="frames"/> that are pre-roll.</param>
    /// <param name="reason">Why the segment was emitted.</param>
    public ClipSegment(
        List<IVideoFrame> frames,
        DateTime triggeredAt,
        int preRollCount,
        ClipEndReason reason
    )
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
        TriggeredAt = triggeredAt;
        PreRollCount = preRollCount;
        Reason = reason;
    }

    /// <summary>Frames in playback order. Throws if the segment has been disposed.</summary>
    public IReadOnlyList<IVideoFrame> Frames =>
        _frames ?? throw new ObjectDisposedException(nameof(ClipSegment));

    /// <summary>UTC time the trigger fired.</summary>
    public DateTime TriggeredAt { get; }

    /// <summary>Number of frames in <see cref="Frames"/> that are pre-roll.</summary>
    public int PreRollCount { get; }

    /// <summary>Why the segment was emitted.</summary>
    public ClipEndReason Reason { get; }

    /// <summary>Convenience accessor; safe to call after disposal (returns 0).</summary>
    public int FrameCount => _frames?.Count ?? 0;

    /// <inheritdoc/>
    public IRefCounted AddRef()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) != 0)
            return;

        List<IVideoFrame>? toDispose = Interlocked.Exchange(ref _frames, null);
        if (toDispose is null)
            return;
        foreach (IVideoFrame f in toDispose)
            f.Dispose();
    }
}

/// <summary>Why a <see cref="ClipSegment"/> was emitted by <see cref="RecordingGate"/>.</summary>
public enum ClipEndReason
{
    /// <summary>Motion stopped and the post-roll quiet window elapsed.</summary>
    PostRollElapsed,

    /// <summary>
    /// The clip hit the per-clip frame cap. Emitting now bounds memory and lets a
    /// fresh recording start immediately if motion is still active — without this
    /// the gate would record forever under continuous motion.
    /// </summary>
    MaxFramesReached,

    /// <summary>
    /// Pipeline shutdown (camera disconnect or app close). Whatever's been
    /// captured up to this point is finalised so the on-disk clip is complete.
    /// </summary>
    Flushed,
}
