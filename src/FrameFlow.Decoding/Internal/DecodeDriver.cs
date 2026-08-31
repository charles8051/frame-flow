// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// The effect surface the <see cref="DecodeDriver"/> needs from a concrete codec.
/// A real implementation wraps <c>avcodec_send_packet</c> / <c>avcodec_receive_frame</c>
/// over an <c>AVCodecContext</c>; the test double scripts <see cref="CodecReturn"/>
/// values in memory. The driver speaks only the pure protocol vocabulary and never
/// touches FFmpeg directly.
/// </summary>
/// <typeparam name="TFrame">The managed frame type the codec produces (e.g. a CPU video frame or a PCM block).</typeparam>
internal interface IDecodeCodec<TFrame>
    where TFrame : class
{
    /// <summary>
    /// Advance to the next encoded input. Returns <see langword="false"/> once the
    /// packet stream is exhausted (or a flush sentinel is reached); the driver then
    /// performs exactly one flush pass (<see cref="SendCurrentInput"/> sends a null
    /// packet) before the machine drains to end-of-stream. Async because real inputs
    /// arrive over a channel filled by a separate demux pump.
    /// </summary>
    ValueTask<bool> TryBeginNextInputAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Perform <c>avcodec_send_packet</c> on the current input (or a null flush packet
    /// once inputs are exhausted), already classified into the protocol vocabulary via
    /// <see cref="DecodeDriver.Classify"/>.
    /// </summary>
    CodecReturn SendCurrentInput();

    /// <summary>
    /// Perform <c>avcodec_receive_frame</c>, already classified. On
    /// <see cref="CodecReturn.Ok"/> the decoded frame is available to
    /// <see cref="BuildFrame"/>.
    /// </summary>
    CodecReturn ReceiveFrame();

    /// <summary>
    /// Build the managed frame from the most recently received native frame. May return
    /// <see langword="null"/> when conversion yields nothing for this frame (the driver
    /// skips it).
    /// </summary>
    TFrame? BuildFrame();
}

/// <summary>
/// The single imperative shell that drives the pure <see cref="DecodeProtocol"/> Mealy
/// machine for any codec. Both <c>VideoDecoder</c> and <c>AudioDecoder</c> crank this same
/// loop (ADR-0055): it replaced the four hand-inlined send/receive loops they used to carry,
/// so the FFmpeg send/receive protocol now lives in exactly one tested place.
/// </summary>
/// <remarks>
/// This class owns the messy edges — the codec ABI mapping, the per-frame
/// <c>yield return</c>, and cancellation — while every <i>decision</i> (what to do with a
/// return code) comes from <see cref="DecodeProtocol"/>. State, decision, and IO stay
/// three separate concerns.
/// </remarks>
internal static class DecodeDriver
{
    /// <summary>
    /// The one FFmpeg-aware step of the decode protocol: map a raw
    /// <c>avcodec_send_packet</c> / <c>avcodec_receive_frame</c> return code to the pure
    /// <see cref="CodecReturn"/> vocabulary. Mirrors
    /// <c>DecodingPipeline.ClassifyDemuxReadResult</c>, which already does this for the
    /// demux-read half. Real <see cref="IDecodeCodec{TFrame}"/> adapters call this; the
    /// driver loop itself never does.
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
    /// Drain a codec's residual output-frame queue after <c>avcodec_flush_buffers</c>, the
    /// defence-in-depth half of a post-seek <c>Flush</c> that both <c>VideoDecoder</c> and
    /// <c>AudioDecoder</c> share (ADR-0055 §Context). <c>avcodec_flush_buffers</c> is not
    /// guaranteed to empty every decoder's output queue across every codec/version combo, so
    /// the residual frames are pulled out explicitly and discarded — leaving the post-flush
    /// decode loop to see only frames produced from packets the new pump sends.
    /// </summary>
    /// <param name="receive">
    /// Receives one frame, returning the raw codec code (mirrors <c>avcodec_receive_frame</c>):
    /// <c>&gt;= 0</c> means a frame was produced; any negative code (<c>EAGAIN</c>, <c>EOF</c>,
    /// fault) means the output buffer is empty and the drain stops.
    /// </param>
    /// <param name="discard">
    /// Releases the frame just received (mirrors <c>av_frame_unref</c>), called once per
    /// produced frame before the next <paramref name="receive"/>.
    /// </param>
    /// <returns>The number of residual frames drained (for diagnostics/tests).</returns>
    /// <remarks>
    /// The two callbacks are the codec ABI seam; the loop and the <c>&gt;= 0</c> "more frames"
    /// predicate are the shared, FFmpeg-free control flow — exercisable from a scripted
    /// transcript with no codec, exactly as the rest of this driver is. This is the single
    /// function the two <c>Flush</c> bodies that used to cite each other now both call.
    /// </remarks>
    public static int DrainResidualFrames(Func<int> receive, Action discard)
    {
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(discard);

        int drained = 0;
        while (true)
        {
            int rc = receive();
            if (rc < 0)
                break; // EAGAIN, EOF, or error — codec output buffer is empty
            discard();
            drained++;
        }

        return drained;
    }

    /// <summary>
    /// Crank the machine to completion, yielding each decoded frame. Pure control flow:
    /// every branch is decided by <see cref="DecodeProtocol.Advance"/>; this method only
    /// performs the effect the machine names and threads the immutable
    /// <see cref="DecodeState"/> along.
    /// </summary>
    /// <param name="codec">The codec effect surface (real FFmpeg adapter or test double).</param>
    /// <param name="cancellationToken">Observed at every step; cancels the enumeration cleanly per ADR-0013.</param>
    public static async IAsyncEnumerable<TFrame> RunAsync<TFrame>(
        IDecodeCodec<TFrame> codec,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
        where TFrame : class
    {
        ArgumentNullException.ThrowIfNull(codec);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Present the next input (a packet, or the flush once packets run out).
            bool hadPacket = await codec
                .TryBeginNextInputAsync(cancellationToken)
                .ConfigureAwait(false);

            var (state, action) = DecodeProtocol.Begin();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (action == DecodeAction.NeedNextInput)
                    break; // inner loop done; pull the next input on the outer loop

                if (action == DecodeAction.Complete)
                    yield break; // decoder fully drained — sequence ends

                if (action == DecodeAction.FaultOnSend)
                    throw new InvalidOperationException("avcodec_send_packet failed.");

                if (action == DecodeAction.FaultOnReceive)
                    throw new InvalidOperationException("avcodec_receive_frame failed.");

                CodecReturn result;
                switch (action)
                {
                    case DecodeAction.SendInput:
                        result = codec.SendCurrentInput();
                        break;

                    case DecodeAction.ReceiveFrame:
                        result = codec.ReceiveFrame();
                        break;

                    case DecodeAction.EmitThenReceive:
                        TFrame? frame = codec.BuildFrame();
                        if (frame is not null)
                            yield return frame;
                        result = codec.ReceiveFrame();
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled decode action {action}."
                        );
                }

                (state, action) = DecodeProtocol.Advance(state, result);
            }

            // A flush pass (no packet) always terminates the inner loop at Complete, not
            // NeedNextInput, so reaching here means a real packet was drained — loop for
            // the next one. The guard is defensive against a codec that never reports EOF.
            if (!hadPacket)
                yield break;
        }
    }
}
