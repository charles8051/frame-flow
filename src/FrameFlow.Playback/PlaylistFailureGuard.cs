// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Decides when a <see cref="PlaylistSession"/> stops skipping failed items and gives up.
/// It counts items that fail in a row without making progress. More than
/// <see cref="MaxConsecutiveFailures"/> of them and the playlist gives up.
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
/// <b>What resets the count.</b> An item that ends without failing, whether it reached its
/// end or was skipped, and an item that made progress before it faulted. A successful
/// start does not reset it: an item that starts and then faults on every pass would
/// otherwise never be counted.
/// </para>
/// <para>
/// A skip resets the count so that a rotation advanced by skips survives a bad item in it,
/// even though no item there reaches its end. Progress resets it so that a source with no
/// end is not given up on because of faults hours apart.
/// </para>
/// <para>
/// Not thread-safe. The session calls it under its transition gate.
/// </para>
/// </remarks>
internal sealed class PlaylistFailureGuard
{
    /// <summary>Failures tolerated in a row. The next one gives up.</summary>
    internal const int MaxConsecutiveFailures = 8;

    /// <summary>
    /// How long an item must have played before it faulted for the fault not to count,
    /// unless half its length is shorter.
    /// </summary>
    internal static readonly TimeSpan ProgressThreshold = TimeSpan.FromSeconds(5);

    /// <summary>Failures counted since the last reset.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Records an item that ended without failing.</summary>
    public void ItemEnded() => ConsecutiveFailures = 0;

    /// <summary>Records a failed item.</summary>
    /// <param name="playedFor">
    /// How far the item had played when it failed. <see cref="TimeSpan.Zero"/> for an item
    /// that never started.
    /// </param>
    /// <param name="itemLength">
    /// The item's duration, or <see cref="TimeSpan.Zero"/> when it is not known.
    /// </param>
    /// <returns><see langword="true"/> when the playlist should give up.</returns>
    public bool ItemFailed(TimeSpan playedFor, TimeSpan itemLength)
    {
        if (playedFor >= ProgressNeeded(itemLength))
        {
            ConsecutiveFailures = 0;
            return false;
        }

        return ++ConsecutiveFailures > MaxConsecutiveFailures;
    }

    /// <summary>
    /// How long an item of <paramref name="itemLength"/> must play to make progress.
    /// </summary>
    internal static TimeSpan ProgressNeeded(TimeSpan itemLength) =>
        itemLength > TimeSpan.Zero && itemLength / 2 < ProgressThreshold
            ? itemLength / 2
            : ProgressThreshold;
}
