// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// A hot, latest-value-cached signal of <see cref="TimeSpan"/> ticks that
/// represent a monotonic timeline. Consumers either read the cached
/// <see cref="Latest"/> value synchronously, or asynchronously
/// <see cref="WaitUntilAsync"/> a target value is reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>The producer side</b> is typically a concrete <see cref="ClockSubject"/>
/// that the clock-owning subsystem publishes into (an audio sink reading a
/// sample counter, a wallclock timer, an RTSP NPT-derived source, a test
/// fixture). The producer-consumer split keeps clock-driven consumers
/// (video pacing, position UI, drift monitors, subtitle windows) coupled to
/// the narrow <c>IClockSource</c> read surface rather than to whatever
/// subsystem happens to author the clock.
/// </para>
/// <para>
/// <b>Semantics.</b> The clock is a single scalar; the publisher overwrites
/// it (no buffering of intermediate values is required). <see cref="Latest"/>
/// is always safe to read from any thread.
/// <see cref="WaitUntilAsync"/> completes when the published value is at
/// or past the target; cancellation via the supplied
/// <see cref="CancellationToken"/> aborts the wait.
/// </para>
/// <para>
/// <b>Monotonicity.</b> Producers are expected to publish a monotonically
/// non-decreasing value during steady-state operation. Discontinuities
/// (seek, pause-then-jump) are permitted — consumers either observe a
/// backwards jump (a previously-pending <c>WaitUntilAsync</c> may no longer
/// be satisfied and continues to wait) or a forward jump (any pending
/// waits whose targets are crossed fire immediately).
/// </para>
/// </remarks>
public interface IClockSource
{
    /// <summary>
    /// The most recently published value. Cached; never blocks. Safe to
    /// read from any thread.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="TimeSpan.Zero"/> before the first publication.
    /// </remarks>
    TimeSpan Latest { get; }

    /// <summary>
    /// Completes when the published value reaches or passes
    /// <paramref name="target"/>. If <see cref="Latest"/> is already at or
    /// past the target, completes synchronously without yielding the
    /// thread.
    /// </summary>
    /// <param name="target">The target timeline position to await.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait. When triggered before the target is reached, the
    /// returned task transitions to the cancelled state.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Backwards jumps.</b> If the published value moves backwards (e.g.
    /// a seek), any in-flight wait whose target was previously satisfied
    /// stays completed (it already returned). A wait registered after the
    /// backwards jump observes the new value; if the target is past the
    /// new latest, the wait suspends until publication catches up again.
    /// </para>
    /// <para>
    /// <b>Producer pauses.</b> If the producer simply stops publishing
    /// (paused source), waits whose target exceeds the last published
    /// value remain suspended indefinitely — exactly what a video pacer
    /// wants for a paused audio clock. Cancel the supplied token to
    /// unstick if you need to tear down.
    /// </para>
    /// </remarks>
    ValueTask WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken = default);
}
