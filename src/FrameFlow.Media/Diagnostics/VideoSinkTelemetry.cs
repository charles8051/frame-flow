// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// The named <see cref="Meter"/> and the two frame counters one video sink type publishes.
/// Construct once per sink type as a <see langword="static"/> <see langword="readonly"/> field
/// and hand it to every <see cref="VideoSinkTelemetry"/> that sink creates.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="VideoSinkTelemetry"/> because the meter is per <i>type</i> while the
/// counts are per <i>instance</i>. Each sink previously declared its own static meter plus two
/// counters that differed only in the name strings; this keeps that one-meter-per-sink-type
/// shape without repeating the wiring.
/// </para>
/// <para>
/// <b>Metric names are the caller's.</b> The instrument names are
/// <c>{metricPrefix}.frames_presented</c> and <c>{metricPrefix}.frames_dropped</c>, so an
/// existing sink keeps the exact names its dashboards already scrape.
/// </para>
/// </remarks>
public sealed class VideoSinkMeters
{
    private readonly Meter _meter;

    /// <summary>Counts frames the sink rendered to its surface.</summary>
    internal Counter<long> FramesPresented { get; }

    /// <summary>Counts frames the sink discarded without rendering.</summary>
    internal Counter<long> FramesDropped { get; }

    /// <param name="meterName">
    /// Meter name to scrape with, e.g. <c>FrameFlow.SDL.Sink</c>.
    /// </param>
    /// <param name="metricPrefix">
    /// Instrument-name prefix, e.g. <c>frameflow.sdl.sink</c>. The two counters append
    /// <c>.frames_presented</c> and <c>.frames_dropped</c>.
    /// </param>
    /// <param name="sinkName">
    /// The sink type's name, used only in the instrument descriptions.
    /// </param>
    public VideoSinkMeters(string meterName, string metricPrefix, string sinkName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(sinkName);

        _meter = new Meter(meterName, "1.0.0");

        FramesPresented = _meter.CreateCounter<long>(
            $"{metricPrefix}.frames_presented",
            description: $"Total video frames rendered to the surface via {sinkName}."
        );

        FramesDropped = _meter.CreateCounter<long>(
            $"{metricPrefix}.frames_dropped",
            description: $"Total video frames {sinkName} discarded because the render thread did not consume the previous frame in time."
        );
    }
}

/// <summary>
/// Per-instance frame accounting for a render-tick video sink: the presented and dropped
/// counts, the ADR-0034 presentation-time stamp, and the
/// <see cref="VideoSinkDiagnosticsSnapshot"/> built from them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LatestWinsFrameSlot"/> already collapsed the latest-wins <i>intake</i> the sinks
/// shared. This collapses the telemetry shell that sat around it, which
/// <c>AvaloniaVideoSink</c> and <c>SdlVideoSink</c> had each written out separately — and
/// which <c>SdlVideoSink</c> had only half of, so it reported
/// <see cref="VideoSinkDiagnosticsSnapshot.Empty"/> through the default
/// <c>IVideoSink.GetDiagnostics</c>.
/// </para>
/// <para>
/// <b>Two kinds of drop.</b> The slot counts frames superseded before anything took them, so
/// <see cref="RecordSupersededDrop"/> only bumps the meter — counting it again here would
/// double it in <see cref="DroppedCount"/>. <see cref="RecordExtraDrop"/> is for losses the
/// slot cannot see, such as a frame copied into a back buffer and then overwritten before the
/// UI thread swapped it in.
/// </para>
/// <para>
/// <b>Threading.</b> Every member is safe from any thread. The counts are interlocked; the two
/// stamp fields are written and read with <see cref="Volatile"/>. The stamp pair is not
/// captured atomically — the window in which the PTS and the wallclock disagree is well under
/// one frame, which is below the resolution anything reading the snapshot cares about.
/// </para>
/// <para>
/// <b>Logging stays with the sink.</b> This type owns counters and counts only. Drop and
/// present log messages name the sink's own render thread, so they remain per-sink
/// <c>[LoggerMessage]</c> members rather than moving here behind a format parameter.
/// </para>
/// </remarks>
public sealed class VideoSinkTelemetry
{
    private readonly VideoSinkMeters _meters;
    private readonly LatestWinsFrameSlot? _slot;

    private long _presented;
    private long _extraDrops;

    // -1 = nothing presented yet. Volatile so cross-thread Snapshot() reads see fresh values.
    private long _lastPresentedPtsTicks = -1;
    private long _lastPresentedAtUtcTicks = -1;

    /// <param name="meters">The sink type's shared meter and counters.</param>
    /// <param name="slot">
    /// The sink's intake slot. Its <see cref="LatestWinsFrameSlot.Dropped"/> is folded into
    /// <see cref="DroppedCount"/>, so supersedes are counted once, by the slot.
    /// </param>
    public VideoSinkTelemetry(VideoSinkMeters meters, LatestWinsFrameSlot slot)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(slot);

        _meters = meters;
        _slot = slot;
    }

    /// <summary>
    /// Telemetry for a sink that has no latest-wins intake — one that presents every frame it
    /// is handed rather than keeping only the newest for a render tick.
    /// <see cref="DroppedCount"/> then counts exactly what the sink reports through
    /// <see cref="RecordExtraDrop"/>, and <see cref="RecordSupersededDrop"/> has nothing to
    /// pair with.
    /// </summary>
    /// <param name="meters">The sink type's shared meter and counters.</param>
    public VideoSinkTelemetry(VideoSinkMeters meters)
    {
        ArgumentNullException.ThrowIfNull(meters);

        _meters = meters;
        _slot = null;
    }

    /// <summary>Total frames that reached the screen through this sink.</summary>
    public long PresentedCount => Interlocked.Read(ref _presented);

    /// <summary>
    /// Total frames that never reached the screen: superseded in the slot before anything took
    /// them, plus whatever the sink reported through <see cref="RecordExtraDrop"/>. With no slot,
    /// the first term is zero.
    /// </summary>
    public long DroppedCount => (_slot?.Dropped ?? 0) + Interlocked.Read(ref _extraDrops);

    /// <summary>
    /// Records one frame as presented and stamps its PTS and the current wallclock. Call at the
    /// point the frame is actually on its way to the screen, not at intake.
    /// </summary>
    /// <param name="pts">Presentation timestamp of the frame just presented.</param>
    public void RecordPresented(TimeSpan pts)
    {
        Interlocked.Increment(ref _presented);
        _meters.FramesPresented.Add(1);

        Volatile.Write(ref _lastPresentedPtsTicks, pts.Ticks);
        Volatile.Write(ref _lastPresentedAtUtcTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Records that the slot superseded an unconsumed frame — bumps the drop meter only,
    /// because <see cref="LatestWinsFrameSlot.Dropped"/> already counts it. Call when
    /// <see cref="LatestWinsFrameSlot.TrySet"/> returns <see langword="true"/>.
    /// </summary>
    public void RecordSupersededDrop() => _meters.FramesDropped.Add(1);

    /// <summary>
    /// Records a frame lost after it left the slot, which the slot therefore cannot count.
    /// Bumps both the drop meter and <see cref="DroppedCount"/>.
    /// </summary>
    public void RecordExtraDrop()
    {
        Interlocked.Increment(ref _extraDrops);
        _meters.FramesDropped.Add(1);
    }

    /// <summary>
    /// Builds the ADR-0034 snapshot from the current counts and stamp. Safe from any thread.
    /// </summary>
    public VideoSinkDiagnosticsSnapshot Snapshot()
    {
        var ptsTicks = Volatile.Read(ref _lastPresentedPtsTicks);
        var wallTicks = Volatile.Read(ref _lastPresentedAtUtcTicks);

        return new VideoSinkDiagnosticsSnapshot(
            FramesPresented: PresentedCount,
            FramesDropped: DroppedCount,
            LastPresentedPresentationTime: ptsTicks >= 0 ? TimeSpan.FromTicks(ptsTicks) : null,
            LastPresentedAtUtc: wallTicks >= 0 ? new DateTime(wallTicks, DateTimeKind.Utc) : null
        );
    }
}
