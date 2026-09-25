// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What a node declares about the items it holds (ADR-0081, decisions 1 and 2): the most it
/// holds at once, and whether its output can share its input's storage.
/// </summary>
/// <remarks>
/// <para>
/// A fixed pool (a hardware decoder's surfaces, a camera's buffers) runs out of frames, not
/// time, so the declaration is a count. A holder bounded only by a duration declares
/// <see cref="Unbounded"/>, because a variable-frame-rate stream can put any number of items
/// into a duration.
/// </para>
/// <para>
/// A node that declares nothing is <see cref="Unbounded"/>. Nothing can then size a fixed pool
/// for a path through it.
/// </para>
/// </remarks>
public sealed record FrameHolding
{
    private FrameHolding(int? maxHeld, bool forwardsStorage, int? framesPerOutputItem)
    {
        MaxHeld = maxHeld;
        ForwardsStorage = forwardsStorage;
        FramesPerOutputItem = framesPerOutputItem;
    }

    /// <summary>
    /// The most input items the node holds at once, counting the one its call is working on, or
    /// <see langword="null"/> when nothing bounds it.
    /// </summary>
    public int? MaxHeld { get; }

    /// <summary>
    /// Whether an output can share an input's storage. <see langword="false"/> for a storage
    /// boundary: a node that emits a new frame and releases its input.
    /// </summary>
    public bool ForwardsStorage { get; }

    /// <summary>
    /// The most frames one output item carries, for a node whose output gathers several input
    /// frames into one item, such as a clip. <see langword="null"/> when each output item carries
    /// what one input item does.
    /// </summary>
    public int? FramesPerOutputItem { get; }

    /// <summary>Nothing bounds what the node holds. The default for a node that declares nothing.</summary>
    public static FrameHolding Unbounded { get; } = new(null, forwardsStorage: true, framesPerOutputItem: null);

    /// <summary>Holds only the item its call is working on, and can forward it.</summary>
    public static FrameHolding InFlight { get; } = new(1, forwardsStorage: true, framesPerOutputItem: null);

    /// <summary>
    /// Holds only the item its call is working on, and emits a new frame: a storage boundary.
    /// </summary>
    public static FrameHolding Boundary { get; } = new(1, forwardsStorage: false, framesPerOutputItem: null);

    /// <summary>Holds at most <paramref name="maxHeld"/> input items at once.</summary>
    /// <param name="maxHeld">The most input items held at once, counting the one in the call.</param>
    /// <param name="forwardsStorage">Whether an output can share an input's storage.</param>
    /// <param name="framesPerOutputItem">
    /// The most frames one output item carries, when the node gathers several frames into one
    /// item.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxHeld"/> or <paramref name="framesPerOutputItem"/> is below 1.
    /// </exception>
    public static FrameHolding AtMost(
        int maxHeld,
        bool forwardsStorage = true,
        int? framesPerOutputItem = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeld, 1);
        if (framesPerOutputItem is { } frames)
            ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1, nameof(framesPerOutputItem));
        return new FrameHolding(maxHeld, forwardsStorage, framesPerOutputItem);
    }
}
