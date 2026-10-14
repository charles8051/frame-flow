// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A thread-safe, single-element "latest-wins" buffer for one decoded
/// <see cref="IVideoFrame"/>. Producers call <see cref="Install"/> from any thread to
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
/// matches the per-frame disposal discipline frames require: every frame handed to the slot
/// is balanced by exactly one disposal — the slot drops it (superseded), the consumer takes
/// and disposes it, or the slot disposes it at <see cref="Close"/> or, once closed, on
/// arrival.
/// </para>
/// <para>
/// <b>Open or closed (ADR-0085, decision 4).</b> A slot starts open. A sink calls
/// <see cref="Close"/> from its dispose, which takes the pending frame and closes the slot in
/// one step under the slot's gate. From then on the slot disposes each frame it is given
/// instead of installing it. A present that checked the sink was live and then lost the race
/// to its dispose therefore cannot leave a frame in the slot that nothing will take.
/// </para>
/// <para>
/// <b>Threading.</b> The slot swap is performed under an internal monitor so install,
/// take and close never race on the field. Per-sink side effects (drop meters/logs, the
/// diagnostics stamp) run <i>outside</i> the lock: <see cref="Install"/> says what it did
/// with the frame, and <see cref="Take"/> accepts an optional callback invoked with the
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
    private bool _closed;
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
    /// Installs <paramref name="frame"/> as the newest pending frame (latest-wins), unless the
    /// slot is closed, and says which happened. If an unconsumed frame was already pending, it
    /// is superseded: disposed exactly once and counted toward <see cref="Dropped"/>. A closed
    /// slot disposes <paramref name="frame"/> instead and counts nothing.
    /// </summary>
    /// <param name="frame">
    /// The frame to install. Ownership transfers to the slot, installed or not. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// What the slot did with the frame. Callers use it to run sink-specific side effects
    /// (counting an accepted frame, a drop meter, a log) outside the slot's lock, and to skip
    /// them for a frame the closed slot refused.
    /// </returns>
    public FrameSlotInstall Install(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IVideoFrame? superseded = null;
        bool closed;
        lock (_gate)
        {
            closed = _closed;
            if (!closed)
            {
                superseded = _pending;
                _pending = frame;
            }
        }

        // Disposal and drop accounting happen outside the lock: the dispose may return the
        // frame to its pool (arbitrary work) and must not run under the slot's gate.
        if (closed)
        {
            // Not a drop: the sink is gone, so nothing was going to draw it.
            frame.Dispose();
            return FrameSlotInstall.Refused;
        }

        if (superseded is null)
            return FrameSlotInstall.Installed;

        Interlocked.Increment(ref _dropped);
        superseded.Dispose();
        return FrameSlotInstall.Superseded;
    }

    /// <summary>
    /// Installs <paramref name="frame"/> as <see cref="Install"/> does, and says only whether
    /// it superseded a pending frame.
    /// </summary>
    /// <param name="frame">
    /// The frame to install. Ownership transfers to the slot, installed or not. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a previously pending frame was superseded (dropped);
    /// otherwise <see langword="false"/>, including when the slot is closed and disposed
    /// <paramref name="frame"/>. <see cref="Install"/> tells those two apart.
    /// </returns>
    public bool TrySet(IVideoFrame frame) => Install(frame) == FrameSlotInstall.Superseded;

    /// <summary>
    /// Takes the pending frame and closes the slot, in one step under the slot's gate, and
    /// disposes that frame. From then on <see cref="Install"/> and <see cref="TrySet"/> dispose
    /// the frame they are given, and <see cref="Take"/> returns <see langword="null"/>. A sink
    /// calls this from its dispose (ADR-0085, decision 4).
    /// </summary>
    /// <remarks>
    /// The pending frame is not counted toward <see cref="Dropped"/>: it is lost to the sink's
    /// disposal, not to a newer frame.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> if this call closed the slot; <see langword="false"/> if it was
    /// already closed, in which case the call did nothing.
    /// </returns>
    public bool Close()
    {
        IVideoFrame? pending;
        lock (_gate)
        {
            if (_closed)
                return false;

            _closed = true;
            pending = _pending;
            _pending = null;
        }

        pending?.Dispose();
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

/// <summary>What <see cref="LatestWinsFrameSlot.Install"/> did with a frame.</summary>
public enum FrameSlotInstall
{
    /// <summary>The frame is pending, and the slot was empty.</summary>
    Installed,

    /// <summary>
    /// The frame is pending, and it superseded an unconsumed frame, which the slot disposed and
    /// counted toward <see cref="LatestWinsFrameSlot.Dropped"/>.
    /// </summary>
    Superseded,

    /// <summary>
    /// The slot is closed: it disposed the frame instead of installing it, and counted nothing.
    /// </summary>
    Refused,
}
