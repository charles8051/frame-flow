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
    /// Whether this surface consumes GPU-resident frames. Pass this to
    /// <c>MediaPlayer.CreateAsync(..., yieldHardwareFrames: surface.PrefersHardwareFrames)</c>
    /// so the decoder yields <c>GpuVideoFrame</c>s for a zero-copy surface, or CPU frames
    /// for a software surface.
    /// </summary>
    bool PrefersHardwareFrames { get; }

    /// <summary>
    /// Wires the logger factory and returns the <see cref="IVideoSink"/> to hand to the
    /// player's <c>MediaPlayer.CreateAsync</c>. Idempotent: repeated calls return the same sink.
    /// </summary>
    IVideoSink AttachSink(ILoggerFactory loggerFactory);
}
