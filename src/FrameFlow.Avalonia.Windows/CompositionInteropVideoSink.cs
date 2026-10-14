// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// The <see cref="IVideoSink"/> half of the composition-interop presenter. Buffers the
/// latest decoded frame (latest-wins) and hands it to the owning
/// <see cref="CompositionInteropVideoView"/> on the view's render tick; the view routes
/// by memory domain (GPU frame → GPU conversion, CPU frame → upload fallback).
/// </summary>
/// <remarks>
/// <para>
/// This split mirrors <c>FrameFlow.Avalonia</c>'s <c>AvaloniaVideoSink</c> /
/// <c>FrameFlowVideoView</c> pairing: the sink owns frame intake + buffering, the view
/// owns the Avalonia compositor surface and the present. The view pulls via
/// <see cref="TakePendingFrame"/> rather than the sink pushing, so the sink has no
/// dependency on the view.
/// </para>
/// <para>
/// <b>Dual-domain.</b> The sink is domain-agnostic — it buffers whatever the decoder
/// produced. Hardware <c>GpuVideoFrame</c>s stay on the GPU, with one copy into the
/// converter's staging texture; CPU frames (software decode, no D3D11VA, or a frame the
/// decoder read back) take the view's BGRA upload fallback.
/// </para>
/// <para>
/// It is an <see cref="IClaimableSink"/>: a second player or pass built against it while the
/// first holds it fails (ADR-0085, decision 6).
/// </para>
/// <para>
/// <b>While no view presents it</b> (ADR-0085, decisions 2 and 3): before the view's first attach,
/// after a detach, and once a view has let it go, the sink keeps only its newest frame while a
/// player holds its claim, and disposes the one that frame replaces. With no claim it keeps nothing
/// and disposes each frame it is given, and releasing the claim disposes the frame it kept. The frame
/// the view presented last before it stopped counts as that newest frame unless a newer one has come.
/// Nothing is presented and <see cref="FramePresented"/> is not raised. Those frames are counted in
/// <see cref="FramesDiscardedOffScreen"/>, not as drops. A sink no view has taken up presents as it
/// always has.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoSink : IVideoSink, IFramePresentedSource, IClaimableSink
{
    private readonly ILogger _logger;
    private readonly LatestWinsFrameSlot _slot = new();
    private readonly SinkClaimGate _claims;

    /// <summary>Creates a sink.</summary>
    public CompositionInteropVideoSink(ILogger<CompositionInteropVideoSink>? logger = null)
    {
        _logger = logger ?? NullLogger<CompositionInteropVideoSink>.Instance;

        // A frame kept while no view presents the sink is kept for the player that holds the
        // claim, and so is the frame a view bound to it presented last. The gate runs the release
        // before it takes another claim, so it lets go of that player's frames and nothing the next
        // one sends (ADR-0085, decision 2).
        _claims = new SinkClaimGate(typeof(CompositionInteropVideoSink), released: OnClaimReleased);
    }

    /// <summary>
    /// Total frames the sink has accepted from the decoder. The stall watchdog reads this as the
    /// "is the decoder still feeding us?" signal: a frozen presenter with this still climbing is the
    /// present-stall signature (investigation 2026-06-12 §9), distinct from a benign no-frames window
    /// (clip advance / pause), where it goes flat too.
    /// </summary>
    /// <remarks>
    /// The slot counts each frame as it installs it, under the gate the view's present takes the
    /// frame through, so this never trails the view's present count. A frame given after
    /// <see cref="DisposeAsync"/> is not counted, and neither is one given while no view presents
    /// the sink and no player holds its claim, which the sink disposes on arrival.
    /// </remarks>
    public long FramesAccepted => _slot.Installed;

    /// <summary>
    /// Total frames this sink discarded at its own latest-wins intake, because a newer frame
    /// arrived before the view's render tick consumed the pending one. This is the same
    /// supersede count <c>AvaloniaVideoSink</c> reports as <c>FramesDropped</c>, and it is
    /// the dominant loss when the feed rate exceeds the presenter's tick rate.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>CompositionInteropVideoView.FramesDropped</c>, which counts only the
    /// ring-full case (every buffer still had a present in flight). The view's
    /// <c>DiagnosticsSource</c> sums the two so the snapshot's <c>FramesDropped</c> means
    /// "total frames the sink discarded", as its xmldoc states.
    /// </remarks>
    public long FramesSuperseded => _slot.Dropped;

    /// <summary>
    /// Total frames this sink discarded while no view presented it (ADR-0085, decision 2): replaced
    /// by a newer frame while a player held its claim, disposed on arrival while none did, or let go
    /// when the claim was released or the view stopped presenting it.
    /// </summary>
    /// <remarks>
    /// Not a drop. <see cref="FramesSuperseded"/> and the diagnostics snapshot's
    /// <c>FramesDropped</c> leave these out, so a view in a background tab, fed at 60 fps, does not
    /// read as a presenter that cannot keep up.
    /// </remarks>
    public long FramesDiscardedOffScreen => _slot.DiscardedOffScreen;

    /// <summary>
    /// Whether a view presents this sink. <see cref="CompositionInteropVideoView"/> sets it as it
    /// starts and stops presenting the sink, and as it takes the sink up or lets it go; see the
    /// type's remarks for what the sink keeps while it is <see langword="false"/>. A sink starts
    /// presented, so one no view has taken up works as it always has.
    /// </summary>
    internal void SetPresented(bool presented) => _slot.SetOnScreen(presented);

    /// <summary>
    /// The view stopped presenting this sink and keeps it: as <c>SetPresented(false)</c>, and the
    /// sink takes back the frame the view presented last. It keeps that frame as its newest while a
    /// player holds its claim and no newer frame is waiting, so the view presents it again when it
    /// comes back, and disposes it otherwise (ADR-0085, decision 2).
    /// </summary>
    /// <param name="lastPresented">
    /// The frame the view presented last, or <see langword="null"/>. Ownership transfers to the sink.
    /// </param>
    internal void StopPresenting(IVideoFrame? lastPresented) => _slot.SetOffScreen(lastPresented);

    /// <summary>
    /// Diagnostics source for <see cref="GetDiagnostics"/>. Set by the owning
    /// <see cref="CompositionInteropVideoView"/> when it creates the sink, so the
    /// snapshot reports the <i>presented</i>/<i>dropped</i> counts (and last-presented
    /// PTS) that live in the view's compositor present loop — the sink itself only
    /// sees intake (<see cref="FramesAccepted"/>). When unset (e.g. a sink used
    /// without a view), <see cref="GetDiagnostics"/> falls back to
    /// <see cref="VideoSinkDiagnosticsSnapshot.Empty"/>.
    /// </summary>
    /// <remarks>
    /// This is the GPU presenter's counterpart to <c>AvaloniaVideoSink.GetDiagnostics</c>:
    /// without it, the pipeline diagnostics snapshot reported all-zero video counts on the
    /// zero-copy compositor path, blinding any consumer that polls
    /// <c>PlaybackDiagnosticsSnapshot</c>.
    /// </remarks>
    internal Func<VideoSinkDiagnosticsSnapshot>? DiagnosticsSource { get; set; }

    /// <inheritdoc />
    public event EventHandler<FramePresentedInfo>? FramePresented;

    /// <summary>
    /// Raises <see cref="FramePresented"/>. The sink hands frames to the view and never sees
    /// them reach the screen, so the view calls this from its present loop, at the same point
    /// it stamps the presented PTS for the diagnostics snapshot.
    /// </summary>
    /// <remarks>
    /// A throwing handler is swallowed and logged: a consumer that cannot process one present
    /// is not a reason to stall the UI thread's present loop.
    /// </remarks>
    internal void RaiseFramePresented(TimeSpan pts, TimelineId timeline)
    {
        var handler = FramePresented;
        if (handler is null)
            return;

        var info = new FramePresentedInfo(pts, DateTime.UtcNow) { Timeline = timeline };

        // One subscriber at a time: a chain invocation abandons every handler after the one
        // that threw, so a broken consumer would silently stop an unrelated overlay updating.
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<FramePresentedInfo>)subscriber)(this, info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A FramePresented handler threw; the present stands.");
            }
        }
    }

    /// <summary>
    /// Raised on the presenting (graph) thread once <see cref="PresentAsync"/> has installed a
    /// frame. <see cref="CompositionInteropVideoView"/> sets this to schedule a present
    /// instead of waiting for a timer tick.
    /// </summary>
    /// <remarks>
    /// The handler does no rendering — the compositor work is UI-thread affine, so all it does
    /// is post to the dispatcher. What it replaces is a <c>DispatcherTimer</c>, which is a
    /// message-queue timer quantized to the ~15.625 ms platform tick: asking it for 16 ms
    /// delivered ~26 ms and capped the presenter near 38 fps regardless of how cheap the
    /// present was (issue #128).
    /// </remarks>
    internal Action? FrameArrived { get; set; }

    /// <inheritdoc/>
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() =>
        DiagnosticsSource?.Invoke() ?? VideoSinkDiagnosticsSnapshot.Empty;

    /// <inheritdoc/>
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => default;

    /// <inheritdoc/>
    /// <remarks>
    /// The frame slot, the frame the view is presenting on the UI thread, and two more: the frame the
    /// view presented last, which it holds until it has drawn the next one into its ring, so that a
    /// paused picture can be presented again after a trip out of the visual tree, and, for D3D12VA,
    /// the frames of the converter's outstanding draws, which it holds until the GPU has read them
    /// and of which the frame presented last is one. The view keeps a frame once it has been drawn,
    /// even if its import or hand-off then fails, so that frame stays one of the draws.
    /// </remarks>
    public int? MaxHeldFrames => 2 + D3D12YuvSharedConverter.InFlight;

    /// <inheritdoc />
    /// <remarks>
    /// A D3D11 or D3D12 frame is converted on the GPU, and a CPU frame is uploaded. Frames from
    /// another backend, such as CUDA, are downloaded before they get here (#566).
    /// </remarks>
    public FrameMemoryDomains AcceptedDomains =>
        FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11 | FrameMemoryDomains.D3D12;

    /// <inheritdoc/>
    /// <remarks>
    /// After <see cref="DisposeAsync"/> the sink disposes the frame, does not count it in
    /// <see cref="FramesAccepted"/> and keeps nothing (ADR-0085, decision 4). While no view
    /// presents it, it keeps or disposes the frame as the type's remarks say.
    /// </remarks>
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        // Latest-wins: keep only the newest frame; the slot disposes any prior unconsumed
        // one. The view routes by memory domain (GPU → zero-copy, CPU → upload fallback).
        // The supersede is accounted by the slot itself (FramesSuperseded) and folded into
        // the view's DiagnosticsSource; the sink has no per-drop log or meter of its own.
        // The slot also counts the install (FramesAccepted) under its gate, before a present
        // already posted can take the frame on the UI thread: the stall watchdog must never
        // see a present ahead of the intake that fed it, or it arms for a frame already on
        // screen. Once DisposeAsync has closed the slot, it disposes the frame instead, and
        // the frame is neither counted nor presented (#664). So does a slot no view presents
        // while no player holds the claim (ADR-0085, decision 2). A slot no view presents that
        // does keep the frame installs it as usual; the view leaves it there until it presents.
        if (_slot.Install(frame) == FrameSlotInstall.Refused)
            return ValueTask.CompletedTask;

        // Delivery-cadence telemetry (perf survey §A1 A/B): one record per frame
        // this real sink accepts. Uniform across pacing variants — both PaceUntil
        // and ClockSelectVideoSink ultimately deliver here at clock cadence.
        PresentCadenceMetrics.RecordPresent();

        // Schedule the present from here rather than waiting for a tick. Bounded catch: this
        // runs presenter code inline on the pacing chain, and a throw — a dispatcher in
        // shutdown, say — would fault the graph's delivery task and stop playback. A
        // presenter that cannot schedule one frame is not a reason to tear down the pipeline.
        try
        {
            FrameArrived?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video surface threw while scheduling a present; delivery continues.");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Takes the latest buffered frame (ownership transfers to the caller), or
    /// <see langword="null"/> if none is pending. Called from the view's render tick.
    /// </summary>
    internal IVideoFrame? TakePendingFrame() => _slot.Take();

    /// <summary>
    /// Whether a frame is installed and waiting for a present. Point-in-time, so a caller must
    /// tolerate it being stale; used only to decide whether a failed dispatcher post is worth
    /// retrying.
    /// </summary>
    internal bool HasPendingFrame => _slot.HasPending;

    // Runs once per released claim, before the gate takes another, on the releasing thread.
    private void OnClaimReleased()
    {
        try
        {
            _slot.SetKeepOffScreen(false);
        }
        finally
        {
            ClaimReleased?.Invoke();
        }
    }

    /// <summary>
    /// Raised, on the thread that released it, when a claim on this sink is released.
    /// <see cref="CompositionInteropVideoView"/> sets this while it is bound to the sink, to let go
    /// of the frame it presented last once the player that sent it is gone. The handler must not
    /// block or throw: it runs inside the release.
    /// </summary>
    internal Action? ClaimReleased { get; set; }

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

    /// <inheritdoc/>
    /// <remarks>
    /// A second call does nothing. Claiming the sink afterwards throws
    /// <see cref="ObjectDisposedException"/>.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        // Takes the pending frame and closes the slot in one step, so a present racing this call
        // cannot leave a frame in it.
        _slot.Close();
        _claims.Close();
        return default;
    }
}
