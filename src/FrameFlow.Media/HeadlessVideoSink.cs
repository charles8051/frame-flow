// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Media;

/// <summary>
/// An <see cref="IVideoSink"/> with no window that counts what it presents and can charge a
/// synthetic cost for presenting it. For headless runs that still need to say something about
/// presentation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="NullVideoSink"/>.</b> That one disposes each frame on arrival and
/// counts nothing, so a headless run measures demux, decode, and clock and is silent about the
/// output stage. Worse, it is silent in the flattering direction: presenting costs nothing and
/// frees its pool slot instantly, so the pipeline runs faster than any real presenter would let
/// it and the numbers come back better than the machine can actually do.
/// <see cref="NullVideoSink"/> is still the right answer when output genuinely does not matter;
/// this is the right answer when the run is a measurement.
/// </para>
/// <para>
/// <b>The frame is held for the whole cost.</b> <see cref="PresentAsync"/> waits out
/// <see cref="PresentCost"/> before disposing the frame, not after. That ordering is the point:
/// the pool slot stays occupied for the duration, so the cost propagates back through
/// <see cref="FramePool"/> as real backpressure the way a slow presenter's would. Disposing
/// first and then sleeping would charge wall-clock time while letting the decoder run
/// unimpeded, which measures nothing.
/// </para>
/// <para>
/// <b>The wait is high-resolution.</b> The delay runs on
/// <see cref="HighResolutionTimeProvider.Preferred"/>. On the system timer a 5 ms synthetic cost
/// bills ~15.6 ms (ADR-0067), which would make headless numbers pessimistically wrong instead of
/// optimistically wrong — no better.
/// </para>
/// <para>
/// <b>It never drops.</b> There is no render tick to fall behind, so every frame handed to
/// <see cref="PresentAsync"/> is presented and <c>FramesDropped</c> stays at zero. That is
/// honest rather than flattering: when the synthetic cost exceeds the frame interval, the loss
/// shows up upstream as <c>VideoFramesDroppedForSync</c> on the pipeline snapshot, because the
/// pacing chain is what gives up. A script asserting on this sink's own drop count will always
/// see zero, and should watch the sync counter instead.
/// </para>
/// <para>
/// <b>It does not own the pool.</b> <see cref="FramePool"/> is supplied, never created here,
/// which is what <c>AvaloniaVideoSink</c> and <c>SdlVideoSink</c> also do. A sink that could
/// dispose a pool out from under a present still waiting on
/// <see cref="PresentCost"/> would be relying on that pool to tolerate a return after
/// disposal. <see cref="CpuFramePool"/> does tolerate it, with a warning; an arbitrary
/// <see cref="IFramePool"/> need not. Not owning it removes the question.
/// </para>
/// </remarks>
public sealed class HeadlessVideoSink : IVideoSink
{
    private static readonly VideoSinkMeters Meters = new(
        "FrameFlow.Headless.Sink",
        "frameflow.headless.sink",
        nameof(HeadlessVideoSink)
    );

    private readonly VideoSinkTelemetry _telemetry;
    private readonly TimeProvider _time;
    private long _abandoned;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a counting headless sink.
    /// </summary>
    /// <param name="framePool">
    /// The pool the decoder rents from, owned by the caller and never disposed here. Use a
    /// bounded pool such as <see cref="CpuFramePool"/> rather than an unbounded one, so the
    /// decoder blocks when frames are in flight exactly as it would behind a real sink —
    /// <see cref="NullVideoSink"/>'s unbounded pool is another way a headless run comes back
    /// faster than the machine can actually go.
    /// </param>
    /// <param name="presentCost">
    /// How long to pretend presenting a frame takes. <see cref="TimeSpan.Zero"/> (the default)
    /// charges nothing and makes this a counting sink only.
    /// </param>
    /// <param name="timeProvider">
    /// Clock the cost is waited on. Defaults to
    /// <see cref="HighResolutionTimeProvider.Preferred"/>. Inject a fake to make the cost
    /// deterministic under test.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="presentCost"/> is negative.</exception>
    public HeadlessVideoSink(
        IFramePool framePool,
        TimeSpan presentCost = default,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(framePool);
        ArgumentOutOfRangeException.ThrowIfLessThan(presentCost, TimeSpan.Zero);

        FramePool = framePool;
        PresentCost = presentCost;
        _time = timeProvider ?? HighResolutionTimeProvider.Preferred;
        _telemetry = new VideoSinkTelemetry(Meters);
    }

    /// <inheritdoc />
    public IFramePool FramePool { get; }

    /// <summary>How long each <see cref="PresentAsync"/> pretends presenting takes.</summary>
    public TimeSpan PresentCost { get; }

    /// <summary>Total frames presented. Never decreases.</summary>
    public long PresentedCount => _telemetry.PresentedCount;

    /// <summary>
    /// Frames disposed without being presented: handed over after
    /// <see cref="DisposeAsync"/>, or abandoned when the present was cancelled.
    /// </summary>
    /// <remarks>
    /// Deliberately <i>not</i> folded into the snapshot's <c>FramesDropped</c>. That field
    /// means the render path is the bottleneck, and the bench exposes it as
    /// <c>sink.dropped</c>; a handful of frames lost at shutdown is not that, and folding them
    /// in would put a non-zero floor under the metric every run. Surfaced here instead so the
    /// frames are accounted for rather than silently uncounted.
    /// </remarks>
    public long AbandonedCount => Interlocked.Read(ref _abandoned);

    /// <inheritdoc />
    /// <remarks>
    /// Charges <see cref="PresentCost"/> before disposing the frame, so the pool slot is held
    /// for the duration. Counts the frame only once the cost is paid: a frame abandoned to
    /// cancellation did not present, and saying otherwise is the one lie this sink exists to
    /// avoid.
    /// </remarks>
    public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            Interlocked.Increment(ref _abandoned);
            frame.Dispose();
            return;
        }

        var presented = false;
        try
        {
            if (PresentCost > TimeSpan.Zero)
                await Task.Delay(PresentCost, _time, ct).ConfigureAwait(false);

            // Checked on both paths, not just after a delay: otherwise an already-cancelled
            // token would count a present whenever PresentCost is zero, and the contract this
            // sink advertises would depend on how it was configured.
            ct.ThrowIfCancellationRequested();

            _telemetry.RecordPresented(frame.Pts);
            presented = true;
        }
        finally
        {
            if (!presented)
                Interlocked.Increment(ref _abandoned);
            frame.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);
        // Nothing to reconfigure: there is no surface, and the pool sizes frames per rent.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => _telemetry.Snapshot();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        // Nothing to tear down: the pool belongs to the caller and a present still waiting on
        // PresentCost holds nothing this sink owns.
        return ValueTask.CompletedTask;
    }
}
