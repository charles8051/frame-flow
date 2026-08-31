// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Encoding.Internal;

/// <summary>
/// The classified outcome of a single <c>avcodec_send_frame</c> /
/// <c>avcodec_receive_packet</c> call, lifted out of FFmpeg's raw integer return
/// codes. This is the <b>input vocabulary</b> of the encode Mealy machine
/// (<see cref="EncodeProtocol"/>).
/// </summary>
/// <remarks>
/// The raw <c>int</c> → <see cref="CodecReturn"/> mapping is the one FFmpeg-aware
/// seam and lives in <see cref="EncodeDriver.Classify"/> — never in this file.
/// Keeping the codec ABI out of the core is what lets the whole protocol be
/// exercised, and the transition table asserted, with nothing plugged in: it is
/// the encode-direction sibling of <c>FrameFlow.Decoding.Internal.CodecReturn</c>
/// (ADR-0055). The vocabulary is identical because the send/receive contract is
/// symmetric — only the payloads (frame in, packet out) differ.
/// </remarks>
internal enum CodecReturn
{
    /// <summary><c>0</c> — the call succeeded: a frame was accepted, or a packet was produced.</summary>
    Ok,

    /// <summary>
    /// <c>AVERROR(EAGAIN)</c> — on send: the encoder will not accept input until its
    /// buffered output is drained; on receive: no packet is available yet, feed more input.
    /// </summary>
    Again,

    /// <summary><c>AVERROR_EOF</c> — the encoder is fully drained; no more packets will come.</summary>
    EndOfStream,

    /// <summary>Any other negative code — an unrecoverable encode fault.</summary>
    Fault,
}

/// <summary>
/// Where the encode machine sits in the FFmpeg send/receive cycle. Held apart from
/// the codec's own (native, opaque) buffer state — this enum is the entire portion
/// of "encode state" that is ours to thread as an immutable value.
/// </summary>
internal enum EncodePhase
{
    /// <summary>Between inputs. <see cref="EncodeProtocol.Begin"/> moves to <see cref="Feeding"/>.</summary>
    Idle,

    /// <summary>An input (frame or flush) is presented; awaiting the result of the send.</summary>
    Feeding,

    /// <summary>Send was accepted; pulling packets until the encoder reports Again or EOF.</summary>
    Draining,

    /// <summary>
    /// Send returned Again (encoder full); drain the buffered output, then re-send the
    /// <b>same</b> input. This is the branch the hand-inlined encode loop relies on to
    /// avoid dropping a stalled frame — see ADR-0055 (decode mirror).
    /// </summary>
    DrainingThenRetry,

    /// <summary>Terminal. The packet sequence has ended (EOF or fault).</summary>
    Done,
}

/// <summary>
/// What the imperative shell should do next. Exactly one action is produced per machine
/// step — the Mealy output.
/// </summary>
internal enum EncodeAction
{
    /// <summary>Call <c>avcodec_send_frame</c> with the current input; feed the result back to <see cref="EncodeProtocol.Advance"/>.</summary>
    SendInput,

    /// <summary>Call <c>avcodec_receive_packet</c>; feed the result back to <see cref="EncodeProtocol.Advance"/>.</summary>
    ReceivePacket,

    /// <summary>A packet was produced: hand it to the consumer, then call <c>avcodec_receive_packet</c> again.</summary>
    EmitThenReceive,

    /// <summary>The current input is fully processed; the shell may present the next frame (or flush) and <see cref="EncodeProtocol.Begin"/> again.</summary>
    NeedNextInput,

    /// <summary>The encoder is fully drained (EOF); end the sequence.</summary>
    Complete,

    /// <summary>Send returned a fault code; the shell throws.</summary>
    FaultOnSend,

    /// <summary>Receive returned a fault code; the shell throws.</summary>
    FaultOnReceive,
}

/// <summary>The threaded, immutable encode state. A value, not a mutable cell.</summary>
internal readonly record struct EncodeState(EncodePhase Phase)
{
    /// <summary>The resting state before any input is presented.</summary>
    public static EncodeState Initial => new(EncodePhase.Idle);
}

/// <summary>One step of the machine: the next state paired with the action the shell must perform.</summary>
internal readonly record struct EncodeTransition(EncodeState State, EncodeAction Action);

/// <summary>
/// The FFmpeg encode protocol expressed as a pure Mealy machine:
/// <c>δ : (state, input) → (state', output)</c>, where the input is a
/// <see cref="CodecReturn"/> and the output is an <see cref="EncodeAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (ADR-0055, encode mirror).</b> The <c>send_frame</c> /
/// <c>receive_packet</c> contract is a small request/response protocol with a fixed
/// return-code vocabulary (<c>0</c>, <c>EAGAIN</c>, <c>EOF</c>, <c>&lt;0</c>) — the exact
/// reflection of the decode direction ADR-0055 purified. <c>H264VideoEncoder</c>
/// hand-inlined that protocol in <c>SendFrameAndDrain</c> / <c>DrainPackets</c>, fused
/// with the native calls. Lifting the sequencing into this pure function gives one
/// transition table the encoder cranks, mirrors the decode core exactly so the two
/// directions cannot drift, and makes the whole protocol unit-testable from a scripted
/// transcript with no FFmpeg.
/// </para>
/// <para>
/// This is a self-contained sibling to <c>FrameFlow.Decoding.Internal.DecodeProtocol</c>,
/// not a shared abstraction: a future unification of the two cores is deliberately out of
/// scope. The vocabulary and transition table are identical because the send/receive
/// negotiation is symmetric; only the named payloads (frame in vs. packet in, packet out
/// vs. frame out) differ.
/// </para>
/// <para>
/// This core deliberately does <b>not</b> own the codec's buffers (those stay inside the
/// native <c>AVCodecContext</c>), nor any IO, clock, cancellation, or threading — all of
/// that is the shell's job (<see cref="EncodeDriver"/> / <c>H264VideoEncoder</c>). It is a
/// total function of <c>(EncodePhase, CodecReturn)</c>.
/// </para>
/// </remarks>
internal static class EncodeProtocol
{
    /// <summary>
    /// Present a fresh input — a frame, or the end-of-stream flush — to the machine.
    /// The machine always asks the shell to send first. Call once per input, before
    /// the first <see cref="Advance"/> for that input.
    /// </summary>
    public static EncodeTransition Begin() =>
        new(new EncodeState(EncodePhase.Feeding), EncodeAction.SendInput);

    /// <summary>
    /// Advance the machine one step, given the classified result of the action it last
    /// asked the shell to perform.
    /// </summary>
    /// <param name="state">The current threaded state.</param>
    /// <param name="result">The <see cref="CodecReturn"/> from the last send or receive.</param>
    /// <returns>The next state and the action the shell must perform.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if called in <see cref="EncodePhase.Idle"/> (use <see cref="Begin"/>) or
    /// <see cref="EncodePhase.Done"/> (terminal).
    /// </exception>
    public static EncodeTransition Advance(EncodeState state, CodecReturn result) =>
        state.Phase switch
        {
            // Awaiting the result of a send.
            EncodePhase.Feeding => result switch
            {
                // Accepted (or already at EOF in flush mode): start draining packets.
                CodecReturn.Ok or CodecReturn.EndOfStream => new(
                    state with { Phase = EncodePhase.Draining },
                    EncodeAction.ReceivePacket
                ),
                // Encoder full: drain first, then re-send the SAME input.
                CodecReturn.Again => new(
                    state with { Phase = EncodePhase.DrainingThenRetry },
                    EncodeAction.ReceivePacket
                ),
                CodecReturn.Fault => new(
                    state with { Phase = EncodePhase.Done },
                    EncodeAction.FaultOnSend
                ),
                _ => throw Unreachable(result),
            },

            // Send was accepted; pulling packets until the encoder wants more input.
            EncodePhase.Draining => result switch
            {
                CodecReturn.Ok => new(state, EncodeAction.EmitThenReceive),
                CodecReturn.Again => new(
                    state with { Phase = EncodePhase.Idle },
                    EncodeAction.NeedNextInput
                ),
                CodecReturn.EndOfStream => new(
                    state with { Phase = EncodePhase.Done },
                    EncodeAction.Complete
                ),
                CodecReturn.Fault => new(
                    state with { Phase = EncodePhase.Done },
                    EncodeAction.FaultOnReceive
                ),
                _ => throw Unreachable(result),
            },

            // Send said Again; drain the buffered output, then re-send the same input.
            EncodePhase.DrainingThenRetry => result switch
            {
                CodecReturn.Ok => new(state, EncodeAction.EmitThenReceive),
                // Output drained — now the encoder will accept the held input. Re-send it.
                CodecReturn.Again => new(
                    state with { Phase = EncodePhase.Feeding },
                    EncodeAction.SendInput
                ),
                CodecReturn.EndOfStream => new(
                    state with { Phase = EncodePhase.Done },
                    EncodeAction.Complete
                ),
                CodecReturn.Fault => new(
                    state with { Phase = EncodePhase.Done },
                    EncodeAction.FaultOnReceive
                ),
                _ => throw Unreachable(result),
            },

            _ => throw new InvalidOperationException(
                $"EncodeProtocol.Advance is not defined for phase {state.Phase}. "
                    + "Idle requires Begin(); Done is terminal."
            ),
        };

    private static InvalidOperationException Unreachable(CodecReturn result) =>
        new($"Unhandled CodecReturn value {result}.");
}
