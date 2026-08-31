// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// Builds a 1→1 substrate operator that throttles item flow to a
/// master <see cref="IClockSource"/>. For each item, the operator
/// extracts the item's PTS via the supplied <paramref name="ptsSelector"/>
/// and awaits <see cref="IClockSource.WaitUntilAsync"/> before
/// forwarding — so a 1-second media frame doesn't reach the sink
/// before the clock has advanced 1 second.
/// </summary>
/// <remarks>
/// <para>
/// <b>Equivalent to the old <c>PacedUntil</c> operator.</b> The old
/// substrate (legacy <c>FramePipelineExtensions</c> territory) had a
/// <c>PacedUntil(clockSource)</c> extension method on
/// <c>FramePipeline&lt;T&gt;</c>. This is the equivalent — same semantics,
/// ported to <see cref="OperatorNode{T, T}"/>.
/// </para>
/// <para>
/// <b>Pacing on the video stream typically; audio passes through.</b>
/// The standard pattern is: place the operator immediately upstream
/// of the video sink, with the master clock coming from the audio
/// sink (when present) or a wallclock source. The audio sink itself
/// publishes the clock and consumes audio at its own native cadence,
/// so audio doesn't need (or want) a pacing operator. Without this
/// operator on video, frames stream at decode speed — a 3-second
/// clip "plays" in &lt;100ms on a fast host.
/// </para>
/// <para>
/// <b>What this does NOT do.</b> Drop-on-late. If the clock is way
/// past the frame's PTS (e.g. consumer was paused, clock keeps
/// running), this operator forwards immediately rather than dropping
/// the stale frame. Drop policy is a separate concern; consumers who
/// need it compose a filter operator upstream. The old substrate's
/// <c>PacedUntil</c> had a reserved <c>VideoFramesDroppedForSync</c>
/// counter that was never wired — same pattern repeats here for now.
/// </para>
/// <para>
/// <b>Diagnostic logging.</b> When a logger is supplied, the
/// operator emits a Debug log per non-trivial wait (&gt;5 ms) and a
/// Warning log per pathological wait (&gt;100 ms). Without a logger
/// (default for tests / lightweight consumers) the pacing is silent.
/// </para>
/// </remarks>
public static partial class PaceUntil
{
    /// <summary>
    /// Creates a pacing operator over <typeparamref name="T"/>. The
    /// operator's body awaits the clock before forwarding each item.
    /// </summary>
    /// <typeparam name="T">Item type — must be ref-counted per substrate convention.</typeparam>
    /// <param name="id">Node id for graph diagnostics.</param>
    /// <param name="clockSource">Master clock the operator paces against.</param>
    /// <param name="ptsSelector">Extracts the item's PTS for the clock comparison.</param>
    /// <param name="logger">
    /// Optional logger. When non-null, per-frame wait timing surfaces via
    /// Debug (any wait &gt;5 ms) and Warning (wait &gt;100 ms — likely
    /// stall culprit). Pass null for silent operation (the default).
    /// </param>
    /// <param name="maxWait">
    /// Optional upper bound on a single frame's pacing wait (defense-in-depth).
    /// When set, a wait that exceeds it stops waiting and forwards the frame
    /// instead of blocking the presenter indefinitely — so a misaligned or
    /// stalled master clock degrades to choppy-but-alive rather than a permanent
    /// freeze. Null (the default) preserves the original unbounded wait. Note the
    /// bound is safe across legitimate pauses: the pacing operator sits upstream
    /// of the pause gate, so a frame forwarded on cap is held by the closed gate,
    /// not leaked to the sink. Cancellation via the item token is unaffected.
    /// </param>
    public static OperatorNode<T, T> Create<T>(
        string id,
        IClockSource clockSource,
        Func<T, TimeSpan> ptsSelector,
        ILogger? logger = null,
        TimeSpan? maxWait = null
    )
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(clockSource);
        ArgumentNullException.ThrowIfNull(ptsSelector);
        if (maxWait is { } mw && mw <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxWait), mw, "maxWait must be positive when supplied.");

        var log = logger ?? NullLogger.Instance;

        return new OperatorNode<T, T>(
            id,
            async (item, ct) =>
            {
                var pts = ptsSelector(item);
                var sw = Stopwatch.StartNew();
                // WaitUntilAsync returns synchronously if the clock is
                // already past the target — same hot-path shape as the
                // old PacedUntil. Cancellation cleanly throws so the
                // substrate disposes the item.
                if (maxWait is { } cap)
                {
                    using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    capCts.CancelAfter(cap);
                    try
                    {
                        await clockSource.WaitUntilAsync(pts, capCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // The cap fired, not the item token: the clock did not reach this
                        // frame's PTS within the bound (misaligned origin / stalled master).
                        // Forward the frame rather than hang the presenter forever.
                        sw.Stop();
                        LogPaceCapExceeded(
                            log,
                            id,
                            sw.Elapsed.TotalMilliseconds,
                            pts.TotalSeconds,
                            clockSource.Latest.TotalSeconds
                        );
                        return item;
                    }
                }
                else
                {
                    await clockSource.WaitUntilAsync(pts, ct).ConfigureAwait(false);
                }
                sw.Stop();

                var waitMs = sw.Elapsed.TotalMilliseconds;
                if (waitMs > 100)
                {
                    LogPaceLongWait(log, id, waitMs, pts.TotalSeconds, clockSource.Latest.TotalSeconds);
                }
                else if (waitMs > 5)
                {
                    LogPaceWait(log, id, waitMs, pts.TotalSeconds);
                }

                return item;
            }
        );
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PaceUntil[{Id}] LONG WAIT {WaitMs:F1}ms (pts={PtsSec:F3}s, clock={ClockSec:F3}s). Likely freeze culprit."
    )]
    private static partial void LogPaceLongWait(
        ILogger logger,
        string id,
        double waitMs,
        double ptsSec,
        double clockSec
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "PaceUntil[{Id}] wait {WaitMs:F1}ms (pts={PtsSec:F3}s)"
    )]
    private static partial void LogPaceWait(ILogger logger, string id, double waitMs, double ptsSec);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PaceUntil[{Id}] WAIT CAP exceeded after {WaitMs:F1}ms (pts={PtsSec:F3}s, clock={ClockSec:F3}s) — clock not advancing to PTS; forwarding frame to avoid a freeze. Suspect a misaligned/stalled master clock."
    )]
    private static partial void LogPaceCapExceeded(
        ILogger logger,
        string id,
        double waitMs,
        double ptsSec,
        double clockSec
    );
}
