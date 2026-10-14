// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Avalonia;

/// <summary>
/// An in-box <see cref="IVideoSurface"/>: <see cref="FrameFlowVideoView"/>, or the Windows
/// zero-copy <c>FrameFlow.Avalonia.Windows.CompositionInteropVideoView</c>, which sees this
/// assembly's internals. <see cref="FrameFlowPlayerView"/> reads the surface's sink through it to
/// claim that sink before it swaps the surface out (ADR-0085, decision 7).
/// </summary>
/// <remarks>
/// A surface of the app's own type does not implement it, exposes no sink to the player view, and
/// is swapped out without a check.
/// </remarks>
internal interface IInBoxVideoSurface : IVideoSurface
{
    /// <summary>
    /// The sink the surface presents, or <see langword="null"/> when it has none: before it has
    /// made one or been given one, after its <c>Sink</c> was set to <see langword="null"/>, and
    /// after it was disposed. Reading it makes nothing. UI thread.
    /// </summary>
    IClaimableSink? Sink { get; }
}
