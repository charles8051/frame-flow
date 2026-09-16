// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// A monotonic timeline of <see cref="TimeSpan"/> positions, read on demand.
/// Consumers either read <see cref="Latest"/> synchronously, or await
/// <see cref="WaitUntilAsync"/> until a target position is reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>The producer side</b> is the clock-owning subsystem itself: an audio
/// sink reading a sample counter (<c>OpenAlAudioSink</c>), a wallclock timer
/// (<c>WallClockSource</c>), an RTSP NPT-derived source, a test fixture.
/// The producer-consumer split keeps clock-driven consumers
/// (video pacing, position UI, drift monitors, subtitle windows) coupled to
/// the narrow <c>IClockSource</c> read surface rather than to whatever
/// subsystem happens to author the clock.
/// </para>
/// <para>
/// <b>Semantics.</b> The clock is a single scalar computed when it is read.
/// There is no publication step and no cached tick that can go stale.
/// <see cref="Latest"/> is safe to read from any thread and does not block.
/// <see cref="WaitUntilAsync"/> completes once the clock is at or past the
/// target; cancellation via the supplied <see cref="CancellationToken"/>
/// aborts the wait.
/// </para>
/// <para>
/// <b>Waiting.</b> A wait that is not already due is served by re-reading the
/// clock: compute the time remaining, sleep at most that long, re-check. Both
/// in-tree implementations cap one sleep, so a frozen clock is still re-checked
/// periodically. Two consequences. A wait cannot be stranded by a thread that
/// failed to run, because there is no publisher to deschedule (ADR-0057). And a
/// target that becomes due through a discontinuity resolves on the next
/// re-check rather than at the instant it was crossed.
/// </para>
/// <para>
/// <b>Monotonicity.</b> The clock is expected to advance monotonically during
/// steady-state operation. Discontinuities (seek, pause-then-jump) are
/// permitted. A backwards jump leaves an in-flight wait waiting, now against
/// the new origin; a forward jump past a pending target resolves that wait on
/// its next re-check.
/// </para>
/// </remarks>
public interface IClockSource
{
    /// <summary>
    /// The clock's current position, computed on read. Never blocks. Safe to
    /// read from any thread.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="TimeSpan.Zero"/> before the clock starts.
    /// </remarks>
    TimeSpan Latest { get; }

    /// <summary>
    /// Completes when the clock reaches or passes <paramref name="target"/>.
    /// If <see cref="Latest"/> is already at or past the target, completes
    /// synchronously without yielding the thread.
    /// </summary>
    /// <param name="target">The target timeline position to await.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait. When triggered before the target is reached, the
    /// returned task transitions to the cancelled state.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Backwards jumps.</b> If the clock moves backwards (e.g. a seek), a
    /// wait that already returned stays completed. A wait still in flight
    /// re-reads the clock on its next check and continues against the new
    /// origin.
    /// </para>
    /// <para>
    /// <b>Stopped clocks.</b> If the clock stops advancing (paused source),
    /// waits whose target is ahead of it stay suspended indefinitely — exactly
    /// what a video pacer wants for a paused audio clock. Cancel the supplied
    /// token to unstick if you need to tear down.
    /// </para>
    /// </remarks>
    ValueTask WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken = default);
}
