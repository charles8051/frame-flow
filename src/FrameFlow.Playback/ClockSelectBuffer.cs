// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// The <b>pure core</b> of presenter-side select-by-clock pacing (ADR-0057
/// Stage 2): a small PTS-ordered ring of decoded video frames plus a total
/// selection function that, given "now" on the master clock, decides which one
/// frame is due to present and which buffered frames are late and must be
/// dropped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pure core.</b> Pacing used to gate frame <i>flow</i> inside the
/// graph operator (<see cref="PaceUntil"/>), which held a decode-pool lease
/// across the clock wait and starved the (small, FFmpeg-default) hwframe pool
/// on a long wait. This core instead lets frames arrive at decode rate into a
/// bounded ring and <i>selects</i> the right one at present time — late frames
/// are dropped (their lease returns immediately) rather than one frame being
/// pinned across a multi-second wait. The selection is a value transform: same
/// (buffer, now) → same decision, no IO, no clock, no locks. The owning
/// <see cref="ClockSelectVideoSink"/> shell provides the clock reads, the
/// lock, the delivery loop, and frame disposal.
/// </para>
/// <para>
/// <b>Selection policy.</b> On a tick at time <c>now</c>:
/// </para>
/// <list type="bullet">
/// <item>The frame to present is the <b>latest</b> buffered frame whose
/// <see cref="IVideoFrame.Pts"/> is at or before <c>now</c> (the freshest due
/// frame).</item>
/// <item>Every <b>earlier</b> due frame (Pts ≤ now but not the chosen one) is
/// <b>late</b> and dropped — the presenter only ever shows the freshest due
/// frame, matching the latest-wins presenters downstream.</item>
/// <item>Frames with Pts &gt; now stay buffered for a future tick.</item>
/// <item>If no frame is due yet (all Pts &gt; now) nothing is presented this
/// tick; the presenter holds whatever it last showed.</item>
/// </list>
/// <para>
/// <b>Monotonicity / discontinuities.</b> The core assumes arrival order is
/// non-decreasing in PTS during steady state (the decoder emits in
/// presentation order on this path). A backwards jump (seek / loop) is handled
/// by the shell flushing the buffer at the discontinuity, not by this core.
/// </para>
/// <para>
/// <b>The post-seek floor.</b> Seeking repositions the demuxer to the keyframe
/// <i>at or before</i> the target, because the frames in between are needed as
/// references, while the master clock is seated at the target exactly. Those
/// reference frames therefore arrive carrying a PTS already behind the clock,
/// which makes every one of them due on arrival — and since they arrive one at
/// a time, each is the freshest due frame at its own moment, so the late-drop
/// rule above never sees two at once and never fires. The result was the whole
/// GOP presenting at decode rate: 7.15x realtime for 421 frames on a
/// single-keyframe 1080p60 file (#157).
/// </para>
/// <para>
/// <see cref="SetFloor"/> makes those frames inadmissible instead. They are
/// still decoded — the decoder needs them — they just never reach the ring.
/// The floor is <b>one-shot</b>: the first frame that clears it spends it, so
/// it can only ever discard the frames between the keyframe and the target.
/// That bound is what keeps a stream whose PTS do not behave as expected from
/// being silently swallowed indefinitely.
/// </para>
/// </remarks>
internal sealed class ClockSelectBuffer
{
    private readonly List<IVideoFrame> _frames;
    private readonly int _capacity;

    // Frames strictly below this are pre-target and must not present. Null when no
    // discontinuity is pending, which is every moment except between a seek and its
    // first on-target frame.
    private TimeSpan? _floor;

    /// <summary>
    /// Creates a buffer whose intended depth is <paramref name="capacity"/>
    /// frames. The shell (<see cref="ClockSelectVideoSink"/>) enforces the bound
    /// with an async permit before each <see cref="Admit"/>, so this core never
    /// has to evict; capacity is held here only to size the backing list.
    /// Small by design (default <see cref="ClockSelectVideoSink.DefaultCapacity"/>)
    /// so at most ~this many GPU decode-texture slices are pinned at once, while
    /// still giving the decoder enough read-ahead that a single late wakeup
    /// doesn't instantly starve the hwframe pool.
    /// </summary>
    public ClockSelectBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be >= 1.");
        _capacity = capacity;
        _frames = new List<IVideoFrame>(capacity);
    }

    /// <summary>Number of frames currently buffered.</summary>
    public int Count => _frames.Count;

    /// <summary>True when no frames are buffered.</summary>
    public bool IsEmpty => _frames.Count == 0;

    /// <summary>True while a post-seek floor is still waiting to be spent.</summary>
    public bool HasFloor => _floor is not null;

    /// <summary>
    /// Refuses frames below <paramref name="floor"/> until one reaches it. Called by the
    /// shell at a seek, with the seek target.
    /// </summary>
    /// <remarks>
    /// A floor at or below zero is not applied. Two reasons it would do harm rather than
    /// good there: a <c>RepeatMode.One</c> loop rewind runs the same discontinuity recipe
    /// with a target of 0, where every frame qualifies and the floor is pure risk; and
    /// content with an edit list can carry small negative PTS at the start, which a floor
    /// of exactly zero would discard.
    /// </remarks>
    public void SetFloor(TimeSpan floor) => _floor = floor > TimeSpan.Zero ? floor : null;

    /// <summary>
    /// The PTS of the earliest buffered frame, or <see langword="null"/> when
    /// empty. The shell uses this to decide how long to wait on the clock
    /// before the next selection.
    /// </summary>
    public TimeSpan? EarliestPts => _frames.Count == 0 ? null : _frames[0].Pts;

    /// <summary>
    /// Offers <paramref name="frame"/> to the ring in arrival order, taking ownership of
    /// its ref only if it is admitted. The shell guarantees a free slot (one async permit
    /// per add), so this never evicts: future, not-yet-due frames are <b>never</b>
    /// dropped — the only dropping is of late frames in <see cref="Select"/>. The decoder
    /// is held to ~real-time by the permit backpressure rather than by racing a
    /// drop-oldest ring.
    /// </summary>
    /// <returns>
    /// False when a post-seek floor is in force and this frame is below it, meaning the
    /// frame is a pre-target reference frame the caller must dispose. True otherwise, and
    /// ownership has transferred.
    /// </returns>
    public bool Admit(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_floor is { } floor)
        {
            if (frame.Pts < floor)
                return false;

            // Spent. Arrival order is non-decreasing in PTS here, so everything after this
            // frame clears the floor too and re-checking it would only be a way to get it
            // wrong on a stream that misbehaves.
            _floor = null;
        }

        _frames.Add(frame);
        return true;
    }

    /// <summary>
    /// Pure selection: given the master-clock time <paramref name="now"/>,
    /// returns the freshest due frame to present (or <see langword="null"/> if
    /// none is due) and removes it plus every late (earlier-due) frame from the
    /// buffer. Late frames are written to <paramref name="dropped"/> for the
    /// caller to dispose; the returned present-frame's ownership transfers to
    /// the caller. Frames with Pts &gt; now remain buffered.
    /// </summary>
    /// <param name="now">The master clock's current value.</param>
    /// <param name="dropped">
    /// Receives the late frames removed this tick (never the presented one).
    /// Empty when nothing was late.
    /// </param>
    /// <returns>The frame to present, or <see langword="null"/> when none is due.</returns>
    public IVideoFrame? Select(TimeSpan now, List<IVideoFrame> dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);

        // Find the index of the latest frame with Pts <= now. Arrival order is
        // PTS-monotonic, so this is the last contiguous due frame.
        int presentIndex = -1;
        for (int i = 0; i < _frames.Count; i++)
        {
            if (_frames[i].Pts <= now)
                presentIndex = i;
            else
                break;
        }

        if (presentIndex < 0)
            return null; // nothing due yet — hold current.

        // Everything before presentIndex is late → drop. presentIndex itself is
        // the freshest due frame → present. Remove [0..presentIndex] in one pass.
        for (int i = 0; i < presentIndex; i++)
            dropped.Add(_frames[i]);

        var present = _frames[presentIndex];
        _frames.RemoveRange(0, presentIndex + 1);
        return present;
    }

    /// <summary>
    /// Removes and returns every buffered frame (ownership transfers to the
    /// caller to dispose). Used by the shell at a seek/loop discontinuity and on
    /// teardown so pre-discontinuity frames never present against the new
    /// timeline.
    /// </summary>
    public void DrainInto(List<IVideoFrame> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.AddRange(_frames);
        _frames.Clear();
    }
}
