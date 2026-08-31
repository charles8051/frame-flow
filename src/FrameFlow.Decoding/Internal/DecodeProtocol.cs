// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// The classified outcome of a single <c>avcodec_send_packet</c> /
/// <c>avcodec_receive_frame</c> call, lifted out of FFmpeg's raw integer return
/// codes. This is the <b>input vocabulary</b> of the decode Mealy machine
/// (<see cref="DecodeProtocol"/>).
/// </summary>
/// <remarks>
/// The raw <c>int</c> → <see cref="CodecReturn"/> mapping is the one FFmpeg-aware
/// seam and lives in <see cref="DecodeDriver.Classify"/> — never in this file.
/// Keeping the codec ABI out of the core is what lets the whole protocol be
/// exercised, and the transition table asserted, with nothing plugged in
/// (ADR-0055). It mirrors the demux-read half, which already classifies into
/// <c>DecodingPipeline.DemuxReadResultKind</c>.
/// </remarks>
internal enum CodecReturn
{
    /// <summary><c>0</c> — the call succeeded: a packet was accepted, or a frame was produced.</summary>
    Ok,

    /// <summary>
    /// <c>AVERROR(EAGAIN)</c> — on send: the decoder will not accept input until its
    /// buffered output is drained; on receive: no frame is available yet, feed more input.
    /// </summary>
    Again,

    /// <summary><c>AVERROR_EOF</c> — the decoder is fully drained; no more frames will come.</summary>
    EndOfStream,

    /// <summary>Any other negative code — an unrecoverable decode fault.</summary>
    Fault,
}

/// <summary>
/// Where the decode machine sits in the FFmpeg send/receive cycle. Held apart from
/// the codec's own (native, opaque) buffer state — this enum is the entire portion
/// of "decode state" that is ours to thread as an immutable value.
/// </summary>
internal enum DecodePhase
{
    /// <summary>Between inputs. <see cref="DecodeProtocol.Begin"/> moves to <see cref="Feeding"/>.</summary>
    Idle,

    /// <summary>An input (packet or flush) is presented; awaiting the result of the send.</summary>
    Feeding,

    /// <summary>Send was accepted; pulling frames until the decoder reports Again or EOF.</summary>
    Draining,

    /// <summary>
    /// Send returned Again (decoder full); drain the buffered output, then re-send the
    /// <b>same</b> input. This is the branch the audio decoder omits today — see ADR-0055.
    /// </summary>
    DrainingThenRetry,

    /// <summary>Terminal. The frame sequence has ended (EOF or fault).</summary>
    Done,
}

/// <summary>
/// What the imperative shell should do next. Exactly one action is produced per machine
/// step — the Mealy output.
/// </summary>
internal enum DecodeAction
{
    /// <summary>Call <c>avcodec_send_packet</c> with the current input; feed the result back to <see cref="DecodeProtocol.Advance"/>.</summary>
    SendInput,

    /// <summary>Call <c>avcodec_receive_frame</c>; feed the result back to <see cref="DecodeProtocol.Advance"/>.</summary>
    ReceiveFrame,

    /// <summary>A frame was produced: hand it to the consumer, then call <c>avcodec_receive_frame</c> again.</summary>
    EmitThenReceive,

    /// <summary>The current input is fully processed; pull the next packet (or flush) and <see cref="DecodeProtocol.Begin"/> again.</summary>
    NeedNextInput,

    /// <summary>The decoder is fully drained (EOF); end the sequence.</summary>
    Complete,

    /// <summary>Send returned a fault code; the shell throws.</summary>
    FaultOnSend,

    /// <summary>Receive returned a fault code; the shell throws.</summary>
    FaultOnReceive,
}

/// <summary>The threaded, immutable decode state. A value, not a mutable cell.</summary>
internal readonly record struct DecodeState(DecodePhase Phase)
{
    /// <summary>The resting state before any input is presented.</summary>
    public static DecodeState Initial => new(DecodePhase.Idle);
}

/// <summary>One step of the machine: the next state paired with the action the shell must perform.</summary>
internal readonly record struct DecodeTransition(DecodeState State, DecodeAction Action);

/// <summary>
/// The FFmpeg decode protocol expressed as a pure Mealy machine:
/// <c>δ : (state, input) → (state', output)</c>, where the input is a
/// <see cref="CodecReturn"/> and the output is a <see cref="DecodeAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (ADR-0055).</b> The <c>send_packet</c> / <c>receive_frame</c>
/// contract is a small request/response protocol with a fixed return-code vocabulary
/// (<c>0</c>, <c>EAGAIN</c>, <c>EOF</c>, <c>&lt;0</c>). Today that protocol is
/// hand-inlined four-plus times across <c>VideoDecoder</c> and <c>AudioDecoder</c>,
/// fused with the native calls and the <c>yield return</c>, and the copies have
/// diverged (audio omits the send-<c>EAGAIN</c> re-send that
/// <see cref="DecodePhase.DrainingThenRetry"/> models). Lifting the sequencing into
/// this pure function gives one transition table both decoders crank, eliminates the
/// divergence by construction, and makes the whole protocol unit-testable from a
/// scripted transcript with no FFmpeg.
/// </para>
/// <para>
/// This core deliberately does <b>not</b> own the codec's buffers (those stay inside
/// the native <c>AVCodecContext</c>), nor any IO, clock, cancellation, or threading —
/// all of that is the shell's job (<see cref="DecodeDriver"/>). It is a total function
/// of <c>(DecodePhase, CodecReturn)</c>.
/// </para>
/// </remarks>
internal static class DecodeProtocol
{
    /// <summary>
    /// Present a fresh input — a packet, or the end-of-stream flush — to the machine.
    /// The machine always asks the shell to send first. Call once per input, before
    /// the first <see cref="Advance"/> for that input.
    /// </summary>
    public static DecodeTransition Begin() =>
        new(new DecodeState(DecodePhase.Feeding), DecodeAction.SendInput);

    /// <summary>
    /// Advance the machine one step, given the classified result of the action it last
    /// asked the shell to perform.
    /// </summary>
    /// <param name="state">The current threaded state.</param>
    /// <param name="result">The <see cref="CodecReturn"/> from the last send or receive.</param>
    /// <returns>The next state and the action the shell must perform.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if called in <see cref="DecodePhase.Idle"/> (use <see cref="Begin"/>) or
    /// <see cref="DecodePhase.Done"/> (terminal).
    /// </exception>
    public static DecodeTransition Advance(DecodeState state, CodecReturn result) =>
        state.Phase switch
        {
            // Awaiting the result of a send.
            DecodePhase.Feeding => result switch
            {
                // Accepted (or already at EOF in flush mode): start draining frames.
                CodecReturn.Ok or CodecReturn.EndOfStream => new(
                    state with { Phase = DecodePhase.Draining },
                    DecodeAction.ReceiveFrame
                ),
                // Decoder full: drain first, then re-send the SAME input.
                CodecReturn.Again => new(
                    state with { Phase = DecodePhase.DrainingThenRetry },
                    DecodeAction.ReceiveFrame
                ),
                CodecReturn.Fault => new(
                    state with { Phase = DecodePhase.Done },
                    DecodeAction.FaultOnSend
                ),
                _ => throw Unreachable(result),
            },

            // Send was accepted; pulling frames until the decoder wants more input.
            DecodePhase.Draining => result switch
            {
                CodecReturn.Ok => new(state, DecodeAction.EmitThenReceive),
                CodecReturn.Again => new(
                    state with { Phase = DecodePhase.Idle },
                    DecodeAction.NeedNextInput
                ),
                CodecReturn.EndOfStream => new(
                    state with { Phase = DecodePhase.Done },
                    DecodeAction.Complete
                ),
                CodecReturn.Fault => new(
                    state with { Phase = DecodePhase.Done },
                    DecodeAction.FaultOnReceive
                ),
                _ => throw Unreachable(result),
            },

            // Send said Again; drain the buffered output, then re-send the same input.
            DecodePhase.DrainingThenRetry => result switch
            {
                CodecReturn.Ok => new(state, DecodeAction.EmitThenReceive),
                // Output drained — now the decoder will accept the held input. Re-send it.
                CodecReturn.Again => new(
                    state with { Phase = DecodePhase.Feeding },
                    DecodeAction.SendInput
                ),
                CodecReturn.EndOfStream => new(
                    state with { Phase = DecodePhase.Done },
                    DecodeAction.Complete
                ),
                CodecReturn.Fault => new(
                    state with { Phase = DecodePhase.Done },
                    DecodeAction.FaultOnReceive
                ),
                _ => throw Unreachable(result),
            },

            _ => throw new InvalidOperationException(
                $"DecodeProtocol.Advance is not defined for phase {state.Phase}. "
                    + "Idle requires Begin(); Done is terminal."
            ),
        };

    private static InvalidOperationException Unreachable(CodecReturn result) =>
        new($"Unhandled CodecReturn value {result}.");
}
