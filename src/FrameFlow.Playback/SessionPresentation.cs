// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// What a session is presenting, read as one value. The controller's loop-stall fold needs the
/// repeat expectation and the item together, and reading them as two properties lets the session
/// advance in between — producing a verdict about one item and an attribution naming another.
/// </summary>
/// <param name="ExpectsRepeat">
/// Whether the session expects its current item to repeat at its end. The stall watchdog is
/// eligible only while this is <see langword="true"/>.
/// </param>
/// <param name="CurrentItem">
/// The item that has <b>started</b>, or <see langword="null"/> when the session presents none. It
/// is the started item rather than the one an advance has taken, because that is what is on
/// screen, and a stall is about what is on screen.
/// </param>
/// <remarks>
/// <para>
/// It is a reference type so that one write publishes the whole pair. A struct field would be
/// written in parts, and a reader could take the repeat expectation from one instant and the item
/// from another — which is the mismatch this type exists to prevent.
/// </para>
/// <para>
/// A session builds it once per state change, after its queue has been committed, from that
/// committed queue and its own state. A session that presents no queue of its own uses
/// <see cref="Empty"/>.
/// </para>
/// </remarks>
internal sealed record SessionPresentation(bool ExpectsRepeat, PlaylistItem? CurrentItem)
{
    /// <summary>Nothing is presenting, and no repeat is expected.</summary>
    public static SessionPresentation Empty { get; } = new(false, null);
}
