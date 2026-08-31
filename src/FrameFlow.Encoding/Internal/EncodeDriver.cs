// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using FrameFlow.Native.Interop;

namespace FrameFlow.Encoding.Internal;

/// <summary>
/// The effect surface the <see cref="EncodeDriver"/> needs from a concrete codec.
/// A real implementation wraps <c>avcodec_send_frame</c> / <c>avcodec_receive_packet</c>
/// over an <c>AVCodecContext</c>; the test double scripts <see cref="CodecReturn"/>
/// values in memory. The driver speaks only the pure protocol vocabulary and never
/// touches FFmpeg directly.
/// </summary>
/// <typeparam name="TPacket">The managed packet type the codec produces (e.g. an <see cref="EncodedPacket"/>).</typeparam>
/// <remarks>
/// This is the encode-direction sibling of <c>FrameFlow.Decoding.Internal.IDecodeCodec</c>.
/// It is synchronous and has no "pull the next input" hook: the encode shell processes one
/// presented input per <see cref="EncodeDriver.Run{TPacket}"/> call (one frame, or the
/// flush), so input acquisition is the caller's concern — <c>H264VideoEncoder.Encode</c>
/// fills its source frame and calls <see cref="EncodeDriver.Run{TPacket}"/> once per frame,
/// and <c>H264VideoEncoder.Flush</c> calls it once for the null flush. Decode pulls inputs
/// from an async channel inside its driver loop; encode does not.
/// </remarks>
internal interface IEncodeCodec<TPacket>
    where TPacket : class
{
    /// <summary>
    /// Perform <c>avcodec_send_frame</c> on the current input (the frame the shell just
    /// filled, or a null flush frame), already classified into the protocol vocabulary via
    /// <see cref="EncodeDriver.Classify"/>.
    /// </summary>
    CodecReturn TrySendFrame();

    /// <summary>
    /// Perform <c>avcodec_receive_packet</c>, already classified. On
    /// <see cref="CodecReturn.Ok"/> the encoded packet is available to
    /// <see cref="BuildPacket"/>.
    /// </summary>
    CodecReturn ReceivePacket();

    /// <summary>
    /// Build the managed packet from the most recently received native packet, and release
    /// the native packet's reference. May return <see langword="null"/> when this packet
    /// yields nothing for the consumer (the driver skips it).
    /// </summary>
    TPacket? BuildPacket();
}

/// <summary>
/// The imperative shell that drives the pure <see cref="EncodeProtocol"/> Mealy machine for
/// one presented input. <c>H264VideoEncoder</c> cranks this in place of the hand-inlined
/// <c>SendFrameAndDrain</c> / <c>DrainPackets</c> loop it used to carry, so the FFmpeg
/// send/receive protocol now lives in exactly one tested place (ADR-0055, encode mirror).
/// </summary>
/// <remarks>
/// This class owns the messy edges — the codec ABI mapping and the per-packet collection —
/// while every <i>decision</i> (what to do with a return code) comes from
/// <see cref="EncodeProtocol"/>. State, decision, and IO stay three separate concerns. It is
/// the encode-direction sibling of <c>FrameFlow.Decoding.Internal.DecodeDriver</c>, kept
/// self-contained in <c>FrameFlow.Encoding</c> rather than shared (a future unification is
/// out of scope).
/// </remarks>
internal static class EncodeDriver
{
    /// <summary>
    /// The one FFmpeg-aware step of the encode protocol: map a raw
    /// <c>avcodec_send_frame</c> / <c>avcodec_receive_packet</c> return code to the pure
    /// <see cref="CodecReturn"/> vocabulary. Real <see cref="IEncodeCodec{TPacket}"/> adapters
    /// call this; the driver loop itself never does. Identical to
    /// <c>DecodeDriver.Classify</c> — the codec return-code contract is direction-agnostic.
    /// </summary>
    public static CodecReturn Classify(int rawReturn)
    {
        if (rawReturn >= 0)
            return CodecReturn.Ok;
        if (rawReturn == FFAvUtil.AvErrorEagain)
            return CodecReturn.Again;
        if (rawReturn == FFAvUtil.AvErrorEof)
            return CodecReturn.EndOfStream;
        return CodecReturn.Fault;
    }

    /// <summary>
    /// Crank the machine to completion for the single input the codec is currently holding,
    /// appending each produced packet to <paramref name="output"/>. Pure control flow: every
    /// branch is decided by <see cref="EncodeProtocol.Advance"/>; this method only performs
    /// the effect the machine names and threads the immutable <see cref="EncodeState"/> along.
    /// </summary>
    /// <param name="codec">The codec effect surface (real FFmpeg adapter or test double).</param>
    /// <param name="output">The list each built packet is appended to, preserving production order.</param>
    /// <returns>
    /// <see langword="true"/> if the encoder reached end-of-stream during this call (a
    /// <c>Complete</c>); <see langword="false"/> if it merely consumed the input and wants the
    /// next one (a <c>NeedNextInput</c>). The flush call returns <see langword="true"/>; a
    /// normal per-frame encode returns <see langword="false"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the machine reports a send or receive fault; the message names which.
    /// </exception>
    public static bool Run<TPacket>(IEncodeCodec<TPacket> codec, List<TPacket> output)
        where TPacket : class
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(output);

        var (state, action) = EncodeProtocol.Begin();

        while (true)
        {
            switch (action)
            {
                case EncodeAction.NeedNextInput:
                    return false; // input consumed; the caller presents the next frame

                case EncodeAction.Complete:
                    return true; // encoder fully drained — end-of-stream reached

                case EncodeAction.FaultOnSend:
                    throw new InvalidOperationException("avcodec_send_frame failed.");

                case EncodeAction.FaultOnReceive:
                    throw new InvalidOperationException("avcodec_receive_packet failed.");
            }

            CodecReturn result;
            switch (action)
            {
                case EncodeAction.SendInput:
                    result = codec.TrySendFrame();
                    break;

                case EncodeAction.ReceivePacket:
                    result = codec.ReceivePacket();
                    break;

                case EncodeAction.EmitThenReceive:
                    TPacket? packet = codec.BuildPacket();
                    if (packet is not null)
                        output.Add(packet);
                    result = codec.ReceivePacket();
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled encode action {action}.");
            }

            (state, action) = EncodeProtocol.Advance(state, result);
        }
    }
}
