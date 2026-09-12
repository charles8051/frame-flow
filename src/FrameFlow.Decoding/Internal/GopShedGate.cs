// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// Whether the packet stream is currently intact or waiting to resynchronise at a
/// keyframe. Threaded through <see cref="GopShedGate"/>; never mutated in place.
/// </summary>
/// <param name="AwaitingKeyframe">
/// True once a packet has been shed and no keyframe has arrived since.
/// </param>
internal readonly record struct GopShedState(bool AwaitingKeyframe)
{
    /// <summary>The reference chain is intact: every packet is admitted.</summary>
    internal static GopShedState Intact => new(AwaitingKeyframe: false);
}

/// <summary>What to do with the packet the gate was offered.</summary>
internal enum PacketAdmission
{
    /// <summary>Hand it to the decoder's queue.</summary>
    Admit,

    /// <summary>Free it. Its references are gone and it cannot be reconstructed.</summary>
    Shed,
}

/// <summary>
/// Holds the packet stream shut from the first shed packet to the next keyframe, so a
/// pipeline under backpressure loses whole GOPs instead of producing corrupt ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists to prevent (#134).</b> <c>VideoDecoder.SendPacketAsync</c> sheds
/// the newest packet when its bounded queue is full and blocking would wedge the shared
/// demux pump (ADR-0060). Packets are not frames. Dropping one that later packets predict
/// from leaves the decoder reconstructing motion vectors against references it never
/// received: macroblock-aligned garbage, green and magenta chroma blocks, regions holding
/// content from several frames earlier, partially healing at each keyframe and degrading
/// again until the next one.
/// </para>
/// <para>
/// Nothing reports it. FFmpeg does not error — it decoded exactly what it was handed — so
/// <c>DecodeErrors</c> stays at zero while the picture is wrong. The only signal that
/// tracked the corruption was the shed count itself.
/// </para>
/// <para>
/// <b>Why a gate and not a smarter drop.</b> The send path sees packets in decode order and
/// knows only one thing about each: whether it is a keyframe. That is not enough to pick a
/// safe packet to drop, because it cannot know what predicts from what. It is exactly enough
/// to know when the chain is safe to rejoin. So the gate does not try to choose better
/// victims; once a packet is lost it stops feeding the decoder altogether and waits for the
/// next point where decoding can start from nothing.
/// </para>
/// <para>
/// <b>What this costs.</b> A shed episode now discards the rest of its GOP rather than
/// scattered packets across several. Fewer frames reach the screen, and the visible artifact
/// becomes "video holds the last good frame, then resumes at the keyframe" instead of
/// "video shows wrong pictures". That trade is the whole point: a late picture is recoverable
/// and a wrong one is not. Shedding load without losing frames is
/// <see cref="DecodeDiscardLevel"/>'s job, which discards with knowledge of frame types and
/// so never breaks a chain.
/// </para>
/// <para>
/// <b>Not conditioned on the full-queue policy.</b> The gate arms on a shed, and only
/// drop-newest sheds. But if <c>DropNewestWhenQueueFull</c> is flipped off while the gate is
/// armed, the chain is still broken, so the gate still holds. Correctness here follows from
/// what was dropped, not from which policy dropped it.
/// </para>
/// </remarks>
internal static class GopShedGate
{
    /// <summary>
    /// Decides whether one packet may be admitted, given the state left by the packets
    /// before it.
    /// </summary>
    /// <param name="state">The gate state carried from the previous packet.</param>
    /// <param name="isKeyframe">
    /// Whether this packet carries <c>AV_PKT_FLAG_KEY</c>. A keyframe decodes from nothing,
    /// so it is the point at which a broken chain becomes whole again.
    /// </param>
    internal static (GopShedState Next, PacketAdmission Admission) Offer(
        GopShedState state,
        bool isKeyframe
    )
    {
        if (!state.AwaitingKeyframe)
            return (state, PacketAdmission.Admit);

        // The chain restarts here. Admitting the keyframe is what reopens the gate, not
        // merely observing one — a keyframe that is itself shed downstream of this call
        // re-arms the gate through AfterShed.
        if (isKeyframe)
            return (GopShedState.Intact, PacketAdmission.Admit);

        return (state, PacketAdmission.Shed);
    }

    /// <summary>
    /// Records that the packet just offered was dropped after all — the queue was full and
    /// the send could not block. Every packet from here to the next keyframe is unusable.
    /// </summary>
    internal static GopShedState AfterShed(GopShedState state) =>
        state with
        {
            AwaitingKeyframe = true,
        };
}
