// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia;

/// <summary>
/// An <see cref="IVideoSink"/> that delivers video frames to an Avalonia
/// <see cref="FrameFlowVideoView"/> for rendering via <c>WriteableBitmap</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PresentAsync"/> installs a frame from the presenting (graph) thread and
/// then raises <see cref="FrameArrived"/> on that same thread. The view's handler drains
/// the slot with <see cref="RenderPendingFrame"/> and copies the pixels into its back
/// buffer right there, so the ~8 MB memcpy a 1080p frame costs never lands on the
/// Avalonia UI thread (ADR-0016 Decision 1). The UI thread only swaps buffers and draws.
/// </para>
/// <para>
/// <see cref="RenderPendingFrame"/> stays safe to call from any thread — a host driving
/// this sink without <see cref="FrameFlowVideoView"/> can still pull frames on its own
/// render tick, which is the pre-ADR-0016 shape and what <c>CreateHeadless</c> uses.
/// </para>
/// <para>
/// Frames are accessed via <see cref="IVideoFrame.AsCpu()"/> for pixel data.
/// After rendering, frames are disposed (returning them to the pool).
/// When a new frame overwrites a pending frame that hasn't been rendered yet,
/// the old frame is disposed (dropped).
/// </para>
/// </remarks>
public sealed partial class AvaloniaVideoSink : IVideoSink
{
    private static readonly VideoSinkMeters Meters = new(
        "FrameFlow.Avalonia.Sink",
        "frameflow.avalonia.sink",
        nameof(AvaloniaVideoSink)
    );

    private readonly ILogger<AvaloniaVideoSink> _logger;
    private readonly LatestWinsFrameSlot _slot = new();
    private readonly VideoSinkTelemetry _telemetry;
    private volatile bool _disposed;

    // Stall-detection state: timestamp of the most recent PresentAsync
    // call. Used to flag gaps > 500 ms as Warning logs so post-mortem
    // log inspection can see when the sink stopped receiving frames.
    private long _lastPresentTimestamp;

    /// <summary>Gets the total number of frames that reached the screen via this sink.</summary>
    public int RenderedFrameCount => (int)_telemetry.PresentedCount;

    /// <summary>
    /// Gets the total number of frames that never reached the screen: superseded in the
    /// slot before anything took them, plus copied into the back buffer but overwritten
    /// before the UI thread swapped them in.
    /// </summary>
    public int DroppedFrameCount => (int)_telemetry.DroppedCount;

    /// <inheritdoc />
    public IFramePool FramePool { get; }

    /// <summary>
    /// Raised on the presenting (graph) thread once <see cref="PresentAsync"/> has installed
    /// a frame. <see cref="FrameFlowVideoView"/> sets this so it can take the frame and copy
    /// it into the back buffer off the UI thread (ADR-0016 Decision 1). Null when no view is
    /// attached, in which case frames sit in the slot until something pulls them.
    /// </summary>
    /// <remarks>
    /// The handler runs inline on the presenting thread, so its cost is paid by the pacing
    /// chain rather than by the compositor. That is the point: an 8 MB memcpy is affordable
    /// there and is not on a thread that has to hit a ~16 ms present.
    /// </remarks>
    internal Action? FrameArrived { get; set; }

    /// <summary>
    /// Takes the pending frame without counting it as presented. The view uses this because
    /// it cannot know whether a frame will be copied until it has one in hand and can read
    /// its dimensions — a frame arriving mid-resize is discarded, and counting it presented
    /// would inflate the very number this sink exists to report honestly.
    /// Pair with <see cref="RecordCopied"/> or <see cref="RecordPreSwapDrop"/>.
    /// </summary>
    internal IVideoFrame? TakePendingFrame() => _slot.Take();

    /// <summary>
    /// Records that a frame was swapped to the front buffer, so it is what the next draw
    /// shows. Carries the ADR-0034 PTS/wallclock stamp that <see cref="RenderPendingFrame"/>
    /// applies on the pull path.
    /// </summary>
    /// <remarks>
    /// Counted at the swap rather than at the copy, so <see cref="RenderedFrameCount"/> means
    /// "reached the screen" and no frame is ever counted both presented and dropped. A frame
    /// copied into the back buffer and then overwritten before the UI thread swapped it is a
    /// drop only.
    /// </remarks>
    internal void RecordPresented(TimeSpan pts) => _telemetry.RecordPresented(pts);

    /// <summary>
    /// Records that a taken frame never drew — either overwritten in the back buffer before
    /// the UI thread swapped it in, or discarded because the buffers were not the right size
    /// yet. Counted into <see cref="DroppedFrameCount"/> and the diagnostics snapshot
    /// alongside the slot's own supersedes.
    /// </summary>
    internal void RecordPreSwapDrop()
    {
        _telemetry.RecordExtraDrop();
        LogFrameDropped(_logger, RenderedFrameCount);
    }

    /// <summary>
    /// Initializes an Avalonia video sink.
    /// </summary>
    /// <param name="framePool">The frame pool that produces frames for this sink.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public AvaloniaVideoSink(IFramePool framePool, ILogger<AvaloniaVideoSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(framePool);
        FramePool = framePool;
        _telemetry = new VideoSinkTelemetry(Meters, _slot);
        _logger = logger ?? NullLogger<AvaloniaVideoSink>.Instance;
        LogSinkCreated(_logger);
    }

    /// <summary>
    /// Creates a headless <see cref="AvaloniaVideoSink"/> for use in tests or
    /// environments without a display. <see cref="PresentAsync"/> accepts and
    /// stores frames normally; <see cref="RenderPendingFrame"/> disposes them.
    /// </summary>
    /// <param name="framePool">The frame pool for backpressure and frame lifecycle.</param>
    /// <param name="logger">Optional logger.</param>
    public static AvaloniaVideoSink CreateHeadless(
        IFramePool framePool,
        ILogger<AvaloniaVideoSink>? logger = null
    ) => new(framePool, logger);

    /// <inheritdoc />
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        if (_disposed)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        // Delivery-cadence telemetry (perf survey §A1 A/B): one record per frame
        // this real sink accepts. Uniform across pacing variants — both PaceUntil
        // and ClockSelectVideoSink ultimately deliver here at clock cadence.
        PresentCadenceMetrics.RecordPresent();

        // Stall-detection: log a Warning if the gap between consecutive
        // PresentAsync calls exceeds 500 ms. That gap means the chain
        // upstream of this sink stopped delivering frames — the
        // diagnostic that pins where a video freeze actually lives.
        var nowTicks = Stopwatch.GetTimestamp();
        var prevTicks = Interlocked.Exchange(ref _lastPresentTimestamp, nowTicks);
        if (prevTicks != 0)
        {
            var gapMs = (nowTicks - prevTicks) * 1000.0 / Stopwatch.Frequency;
            if (gapMs > 500)
            {
                LogPresentGap(_logger, gapMs, frame.Pts.TotalSeconds);
            }
            else
            {
                LogPresentArrived(_logger, gapMs, frame.Pts.TotalSeconds);
            }
        }

        // Latest-wins: the slot disposes any superseded frame and counts the drop. The
        // returned flag drives this sink's drop telemetry (meter + log) outside the slot.
        if (_slot.TrySet(frame))
        {
            _telemetry.RecordSupersededDrop();
            LogFrameDropped(_logger, RenderedFrameCount);
        }

        // Hand off on THIS thread (ADR-0016 Decision 1). The view takes the frame and does
        // its pixel copy here, not on the UI thread. With a view attached the slot is drained
        // synchronously, so the supersede above fires only if a present overtakes the copy.
        //
        // Bounded catch: this now runs presenter code inline on the pacing chain, and a throw
        // from the bitmap lock, the copy, or a dispatcher shutting down would fault the
        // graph's delivery task and stop playback. A presenter that cannot draw one frame is
        // not a reason to tear down the pipeline, so log it and keep delivering.
        try
        {
            FrameArrived?.Invoke();
        }
        catch (Exception ex)
        {
            LogFrameArrivedFailed(_logger, ex);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);
        LogFormatChanged(_logger, format.Width, format.Height, format.Format);
        // WriteableBitmap recreation is handled lazily by FrameFlowVideoView
        // when it detects a size/format mismatch during the next render pass.
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Consumes the most recently presented frame and returns it for rendering.
    /// Called by <see cref="FrameFlowVideoView"/> during its render pass.
    /// </summary>
    /// <returns>
    /// The pending frame if one is available; otherwise <see langword="null"/>.
    /// The caller takes ownership and must dispose the frame after rendering.
    /// </returns>
    public IVideoFrame? RenderPendingFrame()
    {
        // The PTS/wallclock stamp is this sink's render-tick diagnostics hook (ADR-0034);
        // SDL and the compositor presenter do not stamp here, so it stays a per-sink
        // callback rather than slot behavior. Runs only when a frame is actually taken.
        var frame = _slot.Take(taken => _telemetry.RecordPresented(taken.Pts));

        return frame;
    }

    /// <inheritdoc/>
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => _telemetry.Snapshot();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;

        _slot.Take()?.Dispose();

        LogSinkDisposed(_logger, RenderedFrameCount, DroppedFrameCount);
        return ValueTask.CompletedTask;
    }

    // ── Source-generated log methods ──────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Video surface threw while consuming a presented frame; the frame is lost but delivery continues."
    )]
    private static partial void LogFrameArrivedFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AvaloniaVideoSink created.")]
    private static partial void LogSinkCreated(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Video frame dropped: render thread has not consumed previous frame. RenderedFrameCount={RenderedFrameCount}"
    )]
    private static partial void LogFrameDropped(ILogger logger, int renderedFrameCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Video format changed: {Width}x{Height} {Format}"
    )]
    private static partial void LogFormatChanged(
        ILogger logger,
        int width,
        int height,
        PixelFormat format
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AvaloniaVideoSink disposed. Rendered={RenderedCount}, Dropped={DroppedCount}"
    )]
    private static partial void LogSinkDisposed(
        ILogger logger,
        int renderedCount,
        int droppedCount
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "AvaloniaVideoSink PRESENT GAP {GapMs:F0}ms (this frame pts={PtsSec:F3}s) — upstream stopped delivering frames"
    )]
    private static partial void LogPresentGap(ILogger logger, double gapMs, double ptsSec);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AvaloniaVideoSink present gap={GapMs:F1}ms pts={PtsSec:F3}s"
    )]
    private static partial void LogPresentArrived(ILogger logger, double gapMs, double ptsSec);
}
