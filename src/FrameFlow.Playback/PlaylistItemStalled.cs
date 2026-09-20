// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// The current playlist item was expected to repeat and appears to have stalled instead: the
/// position overran its duration with no restart, so frame delivery stopped while the clock kept
/// advancing. This says which item wedged.
/// </summary>
/// <param name="Item">The item that stalled.</param>
/// <param name="Stall">
/// The same <see cref="LoopStalled"/> instance the player raises on its <c>LoopStalled</c> for this
/// stall, by reference, carrying the loop count, position, duration and overrun. A consumer
/// subscribed to both discards the second sighting by reference equality.
/// </param>
/// <remarks>
/// <para>
/// Raised on <c>IMediaPlayer.ItemStalled</c>, which is the channel to prefer over
/// <c>LoopStalled</c> when the item matters. Both report the same stall, <c>ItemStalled</c> first.
/// </para>
/// <para>
/// <b>Nothing recovers.</b> This is an observation. The player does not rebuild the item or bound
/// the wait, so a host that wants the surface to come back has to act — and rebuilding the whole
/// player throws away the warm presenter. What recovery should look like is not decided; see the
/// tracking issue on the repository.
/// </para>
/// <para>
/// It fires on the rising edge only. The stall verdict returning to false re-arms it, so a stall
/// that persists reports once rather than on every tick.
/// </para>
/// </remarks>
public sealed record PlaylistItemStalled(PlaylistItem Item, LoopStalled Stall);
