// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows.Core;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// A converter whose ring the view sizes to the part of the frame it shows (#767, #774): the D3D11
/// converter's shader mode and the D3D12 converter. The view drives both with one policy,
/// <see cref="ViewSizedRingPolicy"/> and <see cref="RingSettle"/>. UI thread only.
/// </summary>
internal interface IViewSizedRing
{
    /// <summary>The frame's width.</summary>
    int Width { get; }

    /// <summary>The frame's height.</summary>
    int Height { get; }

    /// <summary>The ring's size.</summary>
    RingSize Ring { get; }

    /// <summary>
    /// Whether the view sizes the ring. False in the D3D11 converter's video processor mode, whose
    /// size the super-resolution decision picks (#560); it still reads <see cref="Source"/>.
    /// </summary>
    bool RingFollowsView { get; }

    /// <summary>The part of the frame the ring holds, in coded pixels.</summary>
    SourceRect Source { get; }

    /// <summary>Sets the part of the frame the ring holds, from the next conversion.</summary>
    void SetSource(SourceRect source);

    /// <summary>
    /// Replaces the ring, and whatever else its size fixes, with a <paramref name="ring"/>-sized one.
    /// Returns the old parts for the caller to dispose off the UI thread once it has detached their
    /// compositor imports, or <see langword="null"/> when the converter is unchanged.
    /// </summary>
    IDisposable? TryResizeRing(RingSize ring);
}
