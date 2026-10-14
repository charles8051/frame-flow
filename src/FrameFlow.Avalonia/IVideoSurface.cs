// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia.Controls;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia;

/// <summary>
/// A pluggable video surface that <see cref="FrameFlowPlayerView"/> can host: a control
/// that renders decoded frames, plus the <see cref="IVideoSink"/> the player feeds it.
/// </summary>
/// <remarks>
/// Implemented by the cross-platform <see cref="FrameFlowVideoView"/> (CPU /
/// <c>WriteableBitmap</c>) and by the Windows zero-copy presenter
/// (<c>FrameFlow.Avalonia.Windows.CompositionInteropVideoView</c>). Assigning a surface
/// to <see cref="FrameFlowPlayerView.VideoSurface"/> swaps the rendering path while
/// keeping the player's transport chrome — the chrome binds to the player, not the surface.
/// </remarks>
public interface IVideoSurface
{
    /// <summary>The control to host in the visual tree.</summary>
    Control Control { get; }

    /// <summary>
    /// Wires the logger factory and returns the <see cref="IVideoSink"/> to hand to the
    /// player's <c>FrameFlowPlayer.Create()</c>. Idempotent: repeated calls return the same sink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the in-box surfaces, <see cref="FrameFlowVideoView"/> and
    /// <c>CompositionInteropVideoView</c>, the sink is the same for the surface's whole life,
    /// across trips out of the visual tree, so a player built with it keeps working through a tab
    /// switch or a docking move (ADR-0085, decision 2). Two things end that: assigning the
    /// surface's <c>Sink</c>, after which this returns the sink assigned (or a new one of the
    /// surface's own, after <see langword="null"/>), and disposing the surface, which disposes a
    /// sink of its own, after which this throws <see cref="ObjectDisposedException"/>. The sink
    /// serves one player or pass at a time: a second build against it fails while the first holds
    /// its claim (<see cref="IClaimableSink"/>).
    /// </para>
    /// <para>
    /// On the in-box surfaces, the factory reaches the surface's own sink whenever this is called,
    /// before or after the surface joins the visual tree, and a later call with another factory
    /// moves the logging to it. A sink the caller assigned to one of them keeps the logger its
    /// owner gave it. <c>FrameFlowAvaloniaBuilderExtensions.WithVideoSurface</c> makes this call
    /// inside a builder chain.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">An in-box surface has been disposed.</exception>
    IVideoSink AttachSink(ILoggerFactory loggerFactory);
}
