// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly bool _ownsPool;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a counting headless sink.
    /// </summary>
    /// <param name="framePool">
    /// The pool the decoder rents from. Defaults to a <see cref="CpuFramePool"/> at its own
    /// default capacity — a real bounded pool rather than an unbounded one, so the decoder
    /// blocks when frames are in flight exactly as it would behind a real sink. A pool passed
    /// in is not disposed by this sink; one created here is.
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
        IFramePool? framePool = null,
        TimeSpan presentCost = default,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(presentCost, TimeSpan.Zero);

        _ownsPool = framePool is null;
        FramePool = framePool ?? new CpuFramePool(NullLogger<CpuFramePool>.Instance);
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
            frame.Dispose();
            return;
        }

        try
        {
            if (PresentCost > TimeSpan.Zero)
                await Task.Delay(PresentCost, _time, ct).ConfigureAwait(false);

            _telemetry.RecordPresented(frame.Pts);
        }
        finally
        {
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

        if (_ownsPool)
            FramePool.Dispose();

        return ValueTask.CompletedTask;
    }
}
