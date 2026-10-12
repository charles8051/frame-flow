// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Avalonia.Core;

/// <summary>
/// What the transport bar's Loop toggle shows for a <see cref="RepeatMode"/>, and which mode a click
/// or a bind asks for (#631). Pure: the bar passes in the mode the transport reports and applies
/// the answer.
/// </summary>
/// <remarks>
/// A click steps Off, One, All and back to Off. Off goes to One first because that is what a
/// click on the toggle has always done. All goes to Off, never back to One, so a click does not
/// swap a looping playlist for a looping item. Both repeating modes show checked, and the label
/// says which one.
/// </remarks>
internal static class LoopToggle
{
    /// <summary>The toggle's face for <paramref name="mode"/>.</summary>
    /// <remarks>A value outside the enum shows as <see cref="RepeatMode.Off"/>.</remarks>
    public static LoopToggleFace Face(RepeatMode mode) =>
        mode switch
        {
            RepeatMode.One => new LoopToggleFace(IsChecked: true, Label: "Loop one"),
            RepeatMode.All => new LoopToggleFace(IsChecked: true, Label: "Loop all"),
            _ => new LoopToggleFace(IsChecked: false, Label: "Loop"),
        };

    /// <summary>The mode a click asks for while the transport is in <paramref name="mode"/>.</summary>
    /// <remarks>A value outside the enum asks for <see cref="RepeatMode.Off"/>.</remarks>
    public static RepeatMode Next(RepeatMode mode) =>
        mode switch
        {
            RepeatMode.Off => RepeatMode.One,
            RepeatMode.One => RepeatMode.All,
            _ => RepeatMode.Off,
        };

    /// <summary>
    /// The mode to ask a newly bound transport for, or <see langword="null"/> to leave its mode
    /// alone.
    /// </summary>
    /// <param name="mode">The mode the transport reports on bind.</param>
    /// <param name="loopByDefault">The bar's <c>LoopByDefault</c>.</param>
    /// <returns>
    /// <see cref="RepeatMode.One"/> when <paramref name="loopByDefault"/> is set and the transport
    /// is not repeating. A transport already in <see cref="RepeatMode.One"/> or
    /// <see cref="RepeatMode.All"/> keeps its mode.
    /// </returns>
    public static RepeatMode? OnBind(RepeatMode mode, bool loopByDefault) =>
        loopByDefault && mode == RepeatMode.Off ? RepeatMode.One : null;
}

/// <summary>How the Loop toggle looks.</summary>
/// <param name="IsChecked">Whether the toggle shows checked.</param>
/// <param name="Label">The toggle's text.</param>
internal readonly record struct LoopToggleFace(bool IsChecked, string Label);
