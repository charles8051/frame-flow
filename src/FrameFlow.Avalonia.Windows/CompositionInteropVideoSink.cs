// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// The <see cref="IVideoSink"/> half of the composition-interop presenter. Buffers the
/// latest decoded frame (latest-wins) and hands it to the owning
/// <see cref="CompositionInteropVideoView"/> on the view's render tick; the view routes
/// by memory domain (GPU frame → zero-copy, CPU frame → upload fallback).
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
/// produced. Hardware <c>GpuVideoFrame</c>s take the zero-copy path; CPU frames
/// (software decode / no D3D11VA) take the view's BGRA upload fallback.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoSink : IVideoSink
{
    private readonly ILogger _logger;
    private readonly IFramePool _framePool;
    private readonly LatestWinsFrameSlot _slot = new();
    private bool _disposed;
    private long _framesAccepted;

    /// <summary>
    /// Creates a sink bound to <paramref name="framePool"/> (the pool the decoder rents
    /// from for any CPU frames; GPU frames are independently owned clones and do not use it).
    /// </summary>
    public CompositionInteropVideoSink(IFramePool framePool, ILogger<CompositionInteropVideoSink>? logger = null)
    {
        _framePool = framePool ?? throw new ArgumentNullException(nameof(framePool));
        _logger = logger ?? NullLogger<CompositionInteropVideoSink>.Instance;
    }

    /// <inheritdoc/>
    public IFramePool FramePool => _framePool;

    /// <summary>
    /// Total frames the sink has accepted from the decoder. The stall watchdog reads this as the
    /// "is the decoder still feeding us?" signal: a frozen presenter with this still climbing is the
    /// present-stall signature (investigation 2026-06-12 §9), distinct from a benign no-frames window
    /// (clip advance / pause), where it goes flat too.
    /// </summary>
    public long FramesAccepted => Volatile.Read(ref _framesAccepted);

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
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        // Delivery-cadence telemetry (perf survey §A1 A/B): one record per frame
        // this real sink accepts. Uniform across pacing variants — both PaceUntil
        // and ClockSelectVideoSink ultimately deliver here at clock cadence.
        PresentCadenceMetrics.RecordPresent();
        Interlocked.Increment(ref _framesAccepted);

        // Latest-wins: keep only the newest frame; the slot disposes any prior unconsumed
        // one. The view routes by memory domain (GPU → zero-copy, CPU → upload fallback).
        // The supersede is accounted by the slot itself (FramesSuperseded) and folded into
        // the view's DiagnosticsSource, so the bool TrySet returns here would be redundant;
        // the sink has no per-drop log or meter of its own to drive with it.
        _slot.TrySet(frame);

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

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return default;
        _disposed = true;
        _slot.Take()?.Dispose();
        return default;
    }
}
