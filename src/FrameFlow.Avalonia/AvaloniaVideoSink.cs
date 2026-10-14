// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Core;
using FrameFlow.Graph;
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
/// the slot with <see cref="TakePendingFrame"/> and copies the pixels into its back
/// buffer right there, so the ~8 MB memcpy a 1080p frame costs does not land on the
/// Avalonia UI thread frame by frame (ADR-0016 Decision 1). The UI thread swaps buffers and
/// draws, and copies pixels in two places only: once per size change, into the bitmaps the
/// new size needs, and once per attach or rebind, for a frame the sink kept while the view did
/// not present it (ADR-0085).
/// </para>
/// <para>
/// <see cref="RenderPendingFrame"/> stays safe to call from any thread — a host driving
/// this sink without <see cref="FrameFlowVideoView"/> can still pull frames on its own
/// render tick, which is the pre-ADR-0016 shape and what <c>CreateHeadless</c> uses.
/// </para>
/// <para>
/// Frames are accessed via <see cref="IVideoFrame.AsCpu()"/> for pixel data.
/// After rendering, frames are disposed.
/// When a new frame overwrites a pending frame that hasn't been rendered yet,
/// the old frame is disposed (dropped).
/// </para>
/// <para>
/// It is an <see cref="IClaimableSink"/>: a second player or pass built against it while the
/// first holds it fails (ADR-0085, decision 6).
/// </para>
/// <para>
/// <b>While no view presents it</b> (ADR-0085, decisions 2 and 3): before the view's first attach,
/// after a detach, and once a view has let it go, the sink keeps only its newest frame while a
/// player holds its claim, and disposes the one that frame replaces. With no claim it keeps nothing
/// and disposes each frame it is given, and releasing the claim disposes the frame it kept. Nothing
/// is copied or presented and <see cref="FramePresented"/> is not raised. Those frames are counted
/// in <see cref="FramesDiscardedOffScreen"/>, not as drops. A sink no view has taken up, such as one
/// a host drains through <see cref="RenderPendingFrame"/>, works as it always has.
/// </para>
/// </remarks>
public sealed partial class AvaloniaVideoSink : IVideoSink, IFramePresentedSource, IClaimableSink
{
    private static readonly VideoSinkMeters Meters = new(
        "FrameFlow.Avalonia.Sink",
        "frameflow.avalonia.sink",
        nameof(AvaloniaVideoSink)
    );

    private readonly ILogger<AvaloniaVideoSink> _logger;
    private readonly LatestWinsFrameSlot _slot = new();
    private readonly SinkClaimGate _claims;
    private readonly VideoSinkTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly long _createdTimestamp;
    private volatile bool _disposed;

    // Frames the view had taken from the slot and copied, and let go of undrawn when it stopped
    // presenting this sink. The slot counts its own off-screen discards.
    private long _discardedByView;

    // Stall-detection state: when the most recent PresentAsync frame arrived, how long it is meant
    // to show, and the timeline it belongs to. Used to flag a frame arriving > 500 ms after the
    // previous one finished showing as a Warning, so post-mortem log inspection can see when the
    // sink stopped receiving frames. A frame of a new timeline starts it over (ADR-0084, #624).
    // Only PresentAsync touches it, and its calls do not overlap and arrive in the player's order:
    // a graph's sink pump awaits each one before taking the next frame, a player's pacers present
    // through one VideoFormatAnnouncer whose semaphore serializes them, an item prepared ahead keeps
    // its gates shut until the item before it has been disposed (ADR-0084), and the claim keeps a
    // second player or pass off the sink while one holds it (ADR-0085, decision 6). Two graphs
    // built by hand over one sink take no claim, and IVideoSink leaves that undefined.
    private PresentGapTracker _gapTracker;

    /// <summary>Gets the total number of frames that reached the screen via this sink.</summary>
    public int RenderedFrameCount => (int)_telemetry.PresentedCount;

    /// <summary>
    /// Gets the total number of frames that never reached the screen: superseded in the
    /// slot before anything took them, plus copied into the back buffer but overwritten
    /// before the UI thread swapped them in. Frames let go of while no view presented the
    /// sink are not counted here; see <see cref="FramesDiscardedOffScreen"/>.
    /// </summary>
    public int DroppedFrameCount => (int)_telemetry.DroppedCount;

    /// <summary>
    /// Total frames this sink discarded while no view presented it (ADR-0085, decision 2): replaced
    /// by a newer frame while a player held its claim, disposed on arrival while none did, let go
    /// when the claim was released or the view stopped presenting the sink, or copied by the view
    /// and not yet drawn when it stopped.
    /// </summary>
    /// <remarks>
    /// Not a drop. <see cref="DroppedFrameCount"/> and the diagnostics snapshot's
    /// <c>FramesDropped</c> leave these out, so a view in a background tab, fed at 60 fps, does not
    /// read as a presenter that cannot keep up.
    /// </remarks>
    public long FramesDiscardedOffScreen => _slot.DiscardedOffScreen + Interlocked.Read(ref _discardedByView);

    /// <summary>
    /// Raised on the presenting (graph) thread once <see cref="PresentAsync"/> has installed
    /// a frame. <see cref="FrameFlowVideoView"/> sets this so it can take the frame and copy
    /// it into the back buffer off the UI thread (ADR-0016 Decision 1). Null when no view is
    /// bound, in which case frames sit in the slot until something pulls them, or, while no view
    /// presents the sink, are kept or disposed as the type's remarks say.
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
    /// Pair with <see cref="RecordPresented"/> or <see cref="RecordPreSwapDrop"/>.
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
    internal void RecordPresented(TimeSpan pts, TimelineId timeline)
    {
        _telemetry.RecordPresented(pts);
        RaiseFramePresented(pts, timeline);
    }

    /// <inheritdoc />
    public event EventHandler<FramePresentedInfo>? FramePresented;

    /// <summary>
    /// Raises <see cref="FramePresented"/> for a frame that has reached the screen. Called
    /// from both present paths: the view's buffer swap, and the pull path for a host driving
    /// <see cref="RenderPendingFrame"/> itself.
    /// </summary>
    /// <remarks>
    /// A throwing handler is swallowed for the same reason the copy is: a consumer that
    /// cannot process one present is not a reason to stop the presenter. The exception is
    /// logged once per occurrence, like the copy path's.
    /// </remarks>
    private void RaiseFramePresented(TimeSpan pts, TimelineId timeline)
    {
        var handler = FramePresented;
        if (handler is null)
            return;

        var info = new FramePresentedInfo(pts, DateTime.UtcNow) { Timeline = timeline };

        // Invoked one subscriber at a time rather than as a chain: a single delegate
        // invocation abandons every handler after the one that threw, so one broken consumer
        // would silently stop an unrelated overlay updating. The invocation-list array is a
        // small per-present allocation on a path that already does an 8 MB copy.
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<FramePresentedInfo>)subscriber)(this, info);
            }
            catch (Exception ex)
            {
                LogFramePresentedHandlerFailed(_logger, ex);
            }
        }
    }

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
    /// Records that a frame the view took and copied never drew because the view stopped
    /// presenting this sink first: a detach, a dispose, or the view letting the sink go. Counted
    /// into <see cref="FramesDiscardedOffScreen"/>, not as a drop (ADR-0085, decision 2).
    /// </summary>
    internal void RecordDiscardedOffScreen() => Interlocked.Increment(ref _discardedByView);

    /// <summary>
    /// Whether a view presents this sink. <see cref="FrameFlowVideoView"/> sets it as it starts and
    /// stops presenting the sink, and as it takes the sink up or lets it go; see the type's remarks
    /// for what the sink keeps while it is <see langword="false"/>. A sink starts presented, so one
    /// no view has taken up works as it always has.
    /// </summary>
    internal void SetPresented(bool presented) => _slot.SetOnScreen(presented);

    /// <summary>
    /// Whether a frame is waiting in the slot: one the sink kept while no view presented it, or
    /// one installed in the instant before the view's callback went on. The view sweeps for it as
    /// it binds and as it starts presenting. A point-in-time observation.
    /// </summary>
    internal bool HasPendingFrame => _slot.HasPending;

    /// <summary>
    /// Initializes an Avalonia video sink.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public AvaloniaVideoSink(ILogger<AvaloniaVideoSink>? logger = null)
        : this(logger, TimeProvider.System) { }

    /// <summary>
    /// Initializes a sink that reads when frames arrive from <paramref name="timeProvider"/>, which
    /// is what its stall detection measures.
    /// </summary>
    internal AvaloniaVideoSink(ILogger<AvaloniaVideoSink>? logger, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _createdTimestamp = timeProvider.GetTimestamp();
        _telemetry = new VideoSinkTelemetry(Meters, _slot);
        _logger = logger ?? NullLogger<AvaloniaVideoSink>.Instance;

        // A frame kept while no view presents the sink is kept for the player that holds the
        // claim. The gate runs the release before it takes another claim, so it lets go of that
        // player's frame and nothing the next one sends (ADR-0085, decision 2).
        _claims = new SinkClaimGate(typeof(AvaloniaVideoSink), released: () => _slot.SetKeepOffScreen(false));
        LogSinkCreated(_logger);
    }

    /// <summary>
    /// Creates a headless <see cref="AvaloniaVideoSink"/> for use in tests or
    /// environments without a display. <see cref="PresentAsync"/> accepts and
    /// stores frames normally; <see cref="RenderPendingFrame"/> disposes them.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public static AvaloniaVideoSink CreateHeadless(ILogger<AvaloniaVideoSink>? logger = null) =>
        new(logger);

    /// <inheritdoc />
    /// <remarks>
    /// After <see cref="DisposeAsync"/> the sink disposes the frame and neither counts nor keeps
    /// it (ADR-0085, decision 4). While no view presents it, it keeps or disposes the frame as the
    /// type's remarks say.
    /// </remarks>
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        // Spares a frame given after dispose the gap tracker and its logs. The slot's closed state
        // is what disposes a frame whose present races the dispose past this check. The race test
        // disposes the sink from the frame's Duration read below, so that read stays after this
        // check (AvaloniaVideoSinkDisposalTests).
        if (_disposed)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        // Stall-detection: log a Warning if a frame arrives more than 500 ms
        // after the previous one finished showing. That gap means the chain
        // upstream of this sink stopped delivering frames — the diagnostic
        // that pins where a video freeze actually lives. The previous frame's
        // own duration is not part of the gap: a still is one frame held for
        // its whole dwell, and the next item's frame follows it by design (#576).
        // The wait before the first frame of a new timeline is not judged: it
        // is the player moving on, such as a new item after Ended (#624). It
        // is still logged at Debug, so a slow seek, loop or open stays visible.
        // Both name the timeline the tracker judged the frame on, which for a
        // frame that names none is the one it was following.
        (_gapTracker, var gap) = _gapTracker.Observe(
            _timeProvider.GetElapsedTime(_createdTimestamp),
            frame.Duration,
            frame.Timeline
        );
        if (gap is { } g)
        {
            if (g.IsStall)
            {
                LogPresentGap(
                    _logger,
                    g.Interval.TotalMilliseconds,
                    g.Held.TotalMilliseconds,
                    frame.Pts.TotalSeconds,
                    _gapTracker.Timeline
                );
            }
            else if (g.NewTimeline)
            {
                LogPresentNewTimeline(
                    _logger,
                    g.Interval.TotalMilliseconds,
                    _gapTracker.Timeline,
                    frame.Pts.TotalSeconds
                );
            }
            else
            {
                LogPresentArrived(_logger, g.Interval.TotalMilliseconds, frame.Pts.TotalSeconds);
            }
        }

        // Latest-wins: the slot disposes any superseded frame and counts the drop, and once
        // DisposeAsync has closed it, disposes this frame instead of installing it. So does a
        // slot no view presents while no player holds the claim (ADR-0085, decision 2); one that
        // keeps the frame installs it, replacing the one it kept without counting a drop, and the
        // view leaves it there until it presents. The result drives this sink's drop telemetry
        // (meter + log) outside the slot.
        switch (_slot.Install(frame))
        {
            case FrameSlotInstall.Refused:
                return ValueTask.CompletedTask;
            case FrameSlotInstall.Superseded:
                _telemetry.RecordSupersededDrop();
                LogFrameDropped(_logger, RenderedFrameCount);
                break;
        }

        // Delivery-cadence telemetry (perf survey §A1 A/B): one record per frame
        // this real sink accepts. Uniform across pacing variants — both PaceUntil
        // and ClockSelectVideoSink ultimately deliver here at clock cadence.
        PresentCadenceMetrics.RecordPresent();

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
    /// Consumes the most recently presented frame and returns it for rendering, counting it as
    /// presented. For a host that drives this sink without <see cref="FrameFlowVideoView"/>, on
    /// its own render tick; the view takes frames without counting them, and counts each at its
    /// swap.
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
        var frame = _slot.Take(taken =>
        {
            _telemetry.RecordPresented(taken.Pts);
            RaiseFramePresented(taken.Pts, taken.Timeline);
        });

        return frame;
    }

    /// <inheritdoc/>
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => _telemetry.Snapshot();

    /// <inheritdoc />
    /// <remarks>
    /// The frame slot, and a frame taken by <see cref="RenderPendingFrame"/>, which its caller
    /// holds while it renders and the slot fills again. A bound view copies the frame out and
    /// releases it in the call. The frame the sink keeps while no view presents it is the slot's.
    /// </remarks>
    public int? MaxHeldFrames => 2;

    /// <inheritdoc />
    /// <remarks>CPU only: the view reads each frame's pixels through <see cref="IVideoFrame.AsCpu()"/>.</remarks>
    public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Cpu;

    /// <inheritdoc />
    IDisposable? IClaimableSink.TryClaim(string claimant)
    {
        // Kept from here until the lease is released, which runs the gate's hook. The two cannot
        // cross: the hook for the claim before this one has run before this claim could be taken,
        // and this lease is not out yet.
        var lease = _claims.TryClaim(claimant);
        if (lease is not null)
            _slot.SetKeepOffScreen(true);
        return lease;
    }

    /// <inheritdoc />
    string? IClaimableSink.Claimant => _claims.Claimant;

    /// <inheritdoc />
    long IClaimableSink.ClaimOrder => _claims.ClaimOrder;

    /// <inheritdoc />
    /// <remarks>
    /// A second call does nothing and logs nothing. Claiming the sink afterwards throws
    /// <see cref="ObjectDisposedException"/>.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _claims.Close();

        // Takes the pending frame and closes the slot in one step, so a present racing this call
        // cannot leave a frame in it. Only the call that closed it logs.
        if (_slot.Close())
            LogSinkDisposed(_logger, RenderedFrameCount, DroppedFrameCount);

        return ValueTask.CompletedTask;
    }

    // ── Source-generated log methods ──────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Video surface threw while consuming a presented frame; the frame is lost but delivery continues."
    )]
    private static partial void LogFrameArrivedFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "A FramePresented handler threw; the present stands and delivery continues."
    )]
    private static partial void LogFramePresentedHandlerFailed(ILogger logger, Exception ex);

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
        Message = "AvaloniaVideoSink PRESENT GAP {GapMs:F0}ms after a frame meant to show for {HeldMs:F0}ms (this frame pts={PtsSec:F3}s on {Timeline}) — upstream stopped delivering frames"
    )]
    private static partial void LogPresentGap(
        ILogger logger,
        double gapMs,
        double heldMs,
        double ptsSec,
        TimelineId timeline
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AvaloniaVideoSink present gap={GapMs:F1}ms pts={PtsSec:F3}s"
    )]
    private static partial void LogPresentArrived(ILogger logger, double gapMs, double ptsSec);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AvaloniaVideoSink present gap={GapMs:F1}ms starts {Timeline} pts={PtsSec:F3}s, not judged as a stall"
    )]
    private static partial void LogPresentNewTimeline(
        ILogger logger,
        double gapMs,
        TimelineId timeline,
        double ptsSec
    );
}
