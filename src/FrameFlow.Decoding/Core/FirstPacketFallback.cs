// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Core;

/// <summary>What a decoder does with the software fallback after sending a packet.</summary>
internal enum FirstPacketAction
{
    /// <summary>Nothing: the send's result stands and the fallback, if any, is kept.</summary>
    Continue,

    /// <summary>Reopen on the software decoder and send the same packet again.</summary>
    ReopenOnSoftware,

    /// <summary>The hardware decoder took a packet, so the fallback is spent and can go.</summary>
    ReleaseFallback,
}

/// <summary>
/// When a hardware decoder that refused a stream's first packet is replaced by the software
/// decoder (ADR-0033, ADR-0083, #572). Pure.
/// </summary>
/// <remarks>
/// <para>
/// Only before any packet has been accepted. A decoder opened later lacks the reference frames
/// the following packets predict from, and shows a corrupt picture until the next keyframe, so
/// a refusal after the first accepted packet is a fault. The fallback is released the moment the
/// hardware decoder accepts a packet, which is how "any packet accepted" is remembered.
/// </para>
/// <para>
/// Only to software. The player reads the bound backend once, after <c>Open</c>, and fixes the
/// frame domain and the pool budget from it. A swap to software leaves them valid, because the
/// emitted domains include CPU. A swap to another hardware backend would not.
/// </para>
/// <para>
/// Only for a send. A decoder that accepts the first packet and then fails to return its frame
/// has spent the fallback, and the fault stands. <c>Required</c> never prepares a fallback, so it
/// never reaches <see cref="FirstPacketAction.ReopenOnSoftware"/>.
/// </para>
/// </remarks>
internal static class FirstPacketFallback
{
    /// <param name="flushing">True when the send was the null flush packet, which is not an input.</param>
    /// <param name="hasPreparedFallback">True while a software context is prepared and unspent.</param>
    /// <param name="send">The classified result of <c>avcodec_send_packet</c>.</param>
    public static FirstPacketAction After(bool flushing, bool hasPreparedFallback, CodecReturn send)
    {
        if (flushing || !hasPreparedFallback)
            return FirstPacketAction.Continue;

        return send switch
        {
            CodecReturn.Fault => FirstPacketAction.ReopenOnSoftware,
            CodecReturn.Ok => FirstPacketAction.ReleaseFallback,

            // Again: the packet was not taken, and the fallback is still unspent. EndOfStream: the
            // decoder is drained, which a first packet cannot report.
            _ => FirstPacketAction.Continue,
        };
    }
}
