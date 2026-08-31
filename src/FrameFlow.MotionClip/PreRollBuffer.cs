// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.MotionClip;

/// <summary>
/// A bounded ring of recent frames kept alive <em>outside</em> the pipeline's
/// pool-rental lifecycle, so a triggering event can look backward in time
/// (ADR-0052 §3). Frames are stored as un-pooled <see cref="VideoFrameExtensions.CloneCpu"/>
/// copies; the oldest is evicted and disposed when the ring is full.
/// </summary>
/// <remarks>
/// <para>
/// <b>Memory budget.</b> The ring holds at most <c>capacityFrames</c> full
/// display-resolution BGRA32 copies. At 640×480 that is ~1.2&#160;MB each;
/// at 720p ~3.5&#160;MB; at 1080p ~8&#160;MB. A 2&#160;s ring at 30&#160;fps is
/// 60 frames — ≈74&#160;MB at 640×480, ≈210&#160;MB at 720p. These copies are
/// invisible to the display pool's accounting, so the ring's capacity is the
/// only governor — hence the hard cap enforced here.
/// </para>
/// <para>Thread-safety: guarded by a lock so the recorder's snapshot can race
/// the producer's add safely.</para>
/// </remarks>
internal sealed class PreRollBuffer : IDisposable
{
    private readonly Queue<IVideoFrame> _ring = new();
    private readonly int _capacityFrames;
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="capacityFrames">
    /// Maximum frames retained (typically <c>preRollSeconds × fps</c>). Must be
    /// positive; choose with the memory budget above in mind.
    /// </param>
    public PreRollBuffer(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        _capacityFrames = capacityFrames;
    }

    /// <summary>Frames currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _ring.Count;
        }
    }

    /// <summary>
    /// Clones <paramref name="frame"/> into the ring (the clone outlives the
    /// pipeline frame), evicting and disposing the oldest if at capacity. The
    /// source frame is neither retained nor disposed.
    /// </summary>
    public void Add(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IVideoFrame clone = frame.CloneCpu();
        lock (_gate)
        {
            if (_disposed)
            {
                clone.Dispose();
                return;
            }
            _ring.Enqueue(clone);
            while (_ring.Count > _capacityFrames)
                _ring.Dequeue().Dispose();
        }
    }

    /// <summary>
    /// Atomically drains the ring into an array and clears it. Ownership of the
    /// returned frames transfers to the caller, which must dispose them.
    /// </summary>
    public IReadOnlyList<IVideoFrame> SnapshotAndClear()
    {
        lock (_gate)
        {
            var snapshot = _ring.ToArray();
            _ring.Clear();
            return snapshot;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_ring.Count > 0)
                _ring.Dequeue().Dispose();
        }
    }
}
