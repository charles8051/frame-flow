// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// The rule that decides when a <see cref="PlaylistSession"/> stops skipping failed items and ends
/// the queue. <see cref="PlaylistQueue.ItemFailed"/> and <see cref="PlaylistQueue.ItemEnded"/> apply
/// it to the queue's record of items that fail without making progress. The queue ends once every
/// playlist item has failed to start, or once more than <see cref="MaxConsecutiveFailures"/> items
/// have failed in a row, whichever comes first. Both end the same way, in <c>Ended</c> (#810).
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts.</b> An item that cannot be opened or started, and an item that faults
/// before it has made progress.
/// </para>
/// <para>
/// <b>Progress.</b> An item has made progress once it has played for
/// <see cref="ProgressThreshold"/>, or for half its length when that is shorter. A clip
/// shorter than the threshold that faults near its end every pass has shown most of
/// itself each time and is not given up on. A source with no known length, such as a
/// network stream, needs the full threshold.
/// </para>
/// <para>
/// <b>What resets the record.</b> An item that ends without failing, whether it reached its
/// end or was skipped, and an item that made progress before it faulted. A successful
/// start does not reset it: an item that starts and then faults on every pass would
/// otherwise never be counted.
/// </para>
/// <para>
/// A skip resets the record so that a rotation advanced by skips survives a bad item in it,
/// even though no item there reaches its end. Progress resets it so that a source with no
/// end is not given up on because of faults hours apart.
/// </para>
/// <para>
/// <b>Why a fault does not end a pass.</b> An item that faulted did open, and a fault can pass: an
/// item looping under <see cref="RepeatMode.All"/> that faults once plays on after its rebuild. So
/// faults rotate until the count stops them. The count also bounds a long playlist of dead sources
/// and a handler that keeps adding items that fail.
/// </para>
/// </remarks>
internal static class PlaylistFailureGuard
{
    /// <summary>Failures tolerated in a row. The next one ends the queue.</summary>
    internal const int MaxConsecutiveFailures = 8;

    /// <summary>
    /// How long an item must have played before it faulted for the fault not to count,
    /// unless half its length is shorter.
    /// </summary>
    internal static readonly TimeSpan ProgressThreshold = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long an item of <paramref name="itemLength"/> must play to make progress.
    /// </summary>
    internal static TimeSpan ProgressNeeded(TimeSpan itemLength) =>
        itemLength > TimeSpan.Zero && itemLength / 2 < ProgressThreshold
            ? itemLength / 2
            : ProgressThreshold;
}
