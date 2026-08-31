// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A thread-safe, single-element "latest-wins" buffer for one decoded
/// <see cref="IVideoFrame"/>. Producers call <see cref="TrySet"/> from any thread to
/// install the newest frame; a consumer calls <see cref="Take"/> on its render tick to
/// acquire it. When a new frame supersedes an unconsumed one, the older frame is
/// disposed exactly once and the supersede is counted as a drop.
/// </summary>
/// <remarks>
/// <para>
/// This collapses the latest-wins intake that each of FrameFlow's render-tick video
/// sinks (<c>AvaloniaVideoSink</c>, <c>SdlVideoSink</c>,
/// <c>CompositionInteropVideoSink</c>) previously reimplemented — two via
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> on a pending field, one via a
/// <see langword="lock"/>-guarded field. Each carried its own copy of the
/// dispose-the-superseded + drop-accounting logic; this is the one tested home for it.
/// </para>
/// <para>
/// <b>Ownership contract (ADR-0025, ADR-0044).</b> Producers hand the slot a frame they
/// own; the slot owns whatever frame is currently pending. A superseded pending frame is
/// disposed by the slot (a drop). A frame returned by <see cref="Take"/> transfers
/// ownership <i>back</i> to the caller, which must dispose it after presenting. This
/// matches the per-frame disposal discipline frames require: every install is balanced by
/// exactly one disposal — either the slot drops it (superseded) or the consumer takes and
/// disposes it.
/// </para>
/// <para>
/// <b>Threading.</b> The slot swap is performed under an internal monitor so install and
/// take never race on the field. Per-sink side effects (drop meters/logs, the
/// diagnostics stamp) run <i>outside</i> the lock: <see cref="TrySet"/> returns whether a
/// frame was dropped, and <see cref="Take"/> accepts an optional callback invoked with the
/// taken frame. Keeping those hooks outside the critical section avoids holding the lock
/// across <c>Dispose</c> or consumer code, and keeps sink-specific concerns (e.g.
/// Avalonia's PTS/wallclock stamp, which SDL and the compositor presenter do not share)
/// out of the slot.
/// </para>
/// </remarks>
public sealed class LatestWinsFrameSlot
{
    private readonly object _gate = new();
    private IVideoFrame? _pending;
    private long _dropped;

    /// <summary>
    /// Total frames dropped because a newer frame superseded an unconsumed one
    /// (the supersede-before-<see cref="Take"/> count). Safe to read from any thread.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Gets a value indicating whether a frame is currently pending (installed but not yet
    /// taken). Intended for diagnostics/tests; the result is a point-in-time observation
    /// and may be stale by the time the caller acts on it.
    /// </summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _pending is not null;
        }
    }

    /// <summary>
    /// Installs <paramref name="frame"/> as the newest pending frame (latest-wins). If an
    /// unconsumed frame was already pending, it is superseded: disposed exactly once and
    /// counted toward <see cref="Dropped"/>.
    /// </summary>
    /// <param name="frame">
    /// The frame to install. Ownership transfers to the slot. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a previously pending frame was superseded (dropped);
    /// otherwise <see langword="false"/>. Callers use this to run sink-specific drop
    /// side-effects (incrementing a drop meter, emitting a log) outside the slot's lock.
    /// </returns>
    public bool TrySet(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IVideoFrame? superseded;
        lock (_gate)
        {
            superseded = _pending;
            _pending = frame;
        }

        if (superseded is null)
            return false;

        // Drop accounting and disposal happen outside the lock: the dispose may return the
        // frame to its pool (arbitrary work) and must not run under the slot's gate.
        Interlocked.Increment(ref _dropped);
        superseded.Dispose();
        return true;
    }

    /// <summary>
    /// Atomically removes and returns the newest pending frame, transferring ownership to
    /// the caller, or <see langword="null"/> if no frame is pending. The caller must
    /// dispose a non-<see langword="null"/> result after presenting it.
    /// </summary>
    /// <param name="onTaken">
    /// Optional callback invoked with the taken frame (never <see langword="null"/>) after
    /// it leaves the slot but before it is returned. Runs outside the slot's lock. Sinks
    /// use this to stamp per-frame diagnostics (e.g. presentation timestamp + wallclock)
    /// at the moment of acquisition; sinks that do not stamp pass <see langword="null"/>.
    /// </param>
    /// <returns>The newest pending frame, or <see langword="null"/> if the slot was empty.</returns>
    public IVideoFrame? Take(Action<IVideoFrame>? onTaken = null)
    {
        IVideoFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
        }

        if (frame is null)
            return null;

        onTaken?.Invoke(frame);
        return frame;
    }
}
