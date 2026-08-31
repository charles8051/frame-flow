// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// The classified outcome of a single <c>av_read_frame</c> call <b>after stream
/// routing has been resolved</b>: not just "a packet arrived" but "a packet for
/// <i>which</i> sink arrived". This is the <b>input vocabulary</b> of the demux-pump
/// Mealy machine (<see cref="DemuxPump"/>), the sibling of <c>CodecReturn</c> on the
/// decode side (ADR-0055).
/// </summary>
/// <remarks>
/// The raw <c>int</c> → <see cref="ReadOutcome"/> mapping is split across the two
/// FFmpeg-aware seams that already exist on the demux side and must <b>never</b> live in
/// this file: <see cref="DecodingPipeline.ClassifyDemuxReadResult"/> turns the raw
/// <c>av_read_frame</c> return code into the four <c>DemuxReadResultKind</c> cases, and
/// <see cref="DemuxPump.Route"/> folds that kind together with the packet's stream index
/// (relative to the selected, decoder-backed video/audio streams) into one of these
/// outcomes. Keeping FFmpeg out of the core is what lets the whole routing table be
/// exercised, and asserted, with nothing plugged in — exactly as
/// <see cref="DecodeProtocol"/> does for the codec send/receive protocol.
/// </remarks>
internal enum ReadOutcome
{
    /// <summary>
    /// A packet was read whose stream is the selected video stream and a video decoder is
    /// present. The shell must clone it onto the video sink.
    /// </summary>
    SelectedVideo,

    /// <summary>
    /// A packet was read whose stream is the selected audio stream and an audio decoder is
    /// present. The shell must clone it onto the audio sink.
    /// </summary>
    SelectedAudio,

    /// <summary>
    /// A packet was read that belongs to neither selected, decoder-backed stream (a
    /// different stream, or a selected stream whose decoder is absent). It is discarded —
    /// the source packet is unreferenced and no clone is made. This is the demux-side
    /// analogue of the no-consumer discard the pump has always performed by falling through
    /// its routing <c>if</c>/<c>else</c> (ADR-0059).
    /// </summary>
    Unselected,

    /// <summary>
    /// <c>AVERROR_EOF</c> — the format context is exhausted; no more packets will come.
    /// Maps from <c>DemuxReadResultKind.EndOfStream</c>.
    /// </summary>
    EndOfStream,

    /// <summary>
    /// A non-EOF demux failure or a malformed (positive, non-zero) return code — both are
    /// unrecoverable and fault the pump. Maps from <c>DemuxReadResultKind.Fault</c> and
    /// <c>DemuxReadResultKind.Malformed</c>; the shell distinguishes the two only for its
    /// exception message, never for control flow.
    /// </summary>
    Fault,
}

/// <summary>
/// Where the demux pump sits in its read/route cycle. This is the entire portion of
/// "pump state" that is ours to thread as an immutable value — the format context's own
/// (native, opaque) read cursor stays behind the FFmpeg wall, exactly as the codec's
/// buffers do on the decode side.
/// </summary>
internal enum DemuxPumpPhase
{
    /// <summary>
    /// Resting between packets, with nothing held. The next step reads the next packet.
    /// </summary>
    NeedRead,

    /// <summary>
    /// A previously-cloned packet is retained for (re)delivery and has not yet been handed
    /// to its sink. The pump entered this phase when a queue write was interrupted (e.g. by
    /// cancellation during pause) after the clone was made but before the sink accepted it,
    /// so the work the pump already did is not lost. The next step delivers it before any
    /// further reads.
    /// </summary>
    /// <remarks>
    /// This retention is correct for pause/resume but <b>wrong across a seek</b>: a packet
    /// held from the pre-seek timeline must not be delivered as the head of the post-seek
    /// stream. That invalidation is the explicit <see cref="DemuxPump.Seek"/> transition,
    /// not a side comment — <c>HavePending + Seek → NeedRead, DropPending</c>.
    /// </remarks>
    HavePending,

    /// <summary>Terminal. End-of-stream was reached; the pump stops.</summary>
    Done,
}

/// <summary>
/// What the imperative shell should do next. Exactly one action is produced per machine
/// step — the Mealy output, mirroring <c>DecodeAction</c> on the decode side.
/// </summary>
internal enum DemuxPumpAction
{
    /// <summary>
    /// Read the next packet (<c>av_read_frame</c>), classify it via
    /// <see cref="DemuxPump.Route"/>, and feed the resulting <see cref="ReadOutcome"/> back
    /// to <see cref="DemuxPump.Advance"/>.
    /// </summary>
    ReadNext,

    /// <summary>
    /// Clone the just-read source packet and write the clone to the <b>video</b> sink, then
    /// unreference the source packet so the read buffer can be reused.
    /// </summary>
    RouteToVideo,

    /// <summary>
    /// Clone the just-read source packet and write the clone to the <b>audio</b> sink, then
    /// unreference the source packet so the read buffer can be reused.
    /// </summary>
    RouteToAudio,

    /// <summary>
    /// Discard the just-read source packet: unreference it without cloning. No sink receives
    /// it.
    /// </summary>
    DiscardUnselected,

    /// <summary>
    /// Deliver the retained pending packet to the sink it was cloned for. On success the
    /// pump returns to <see cref="DemuxPumpPhase.NeedRead"/>; if delivery is interrupted the
    /// shell re-retains it (the same effect that produced <see cref="DemuxPumpPhase.HavePending"/>
    /// in the first place) and the machine is not advanced.
    /// </summary>
    DeliverPending,

    /// <summary>
    /// Drop the retained pending packet (free it) without delivering it — the seek
    /// invalidation. Produced only by <see cref="DemuxPump.Seek"/>.
    /// </summary>
    DropPending,

    /// <summary>
    /// End-of-stream: the shell records EOF for diagnostics and stops the pump.
    /// </summary>
    Complete,

    /// <summary>
    /// The read returned a fault (or malformed) code; the shell throws.
    /// </summary>
    FaultRead,

    /// <summary>
    /// No effect — the machine was asked to do something already satisfied (e.g. a
    /// <see cref="DemuxPump.Seek"/> with nothing pending). The shell does nothing.
    /// </summary>
    None,
}

/// <summary>The threaded, immutable pump state. A value, not a mutable cell.</summary>
internal readonly record struct DemuxPumpState(DemuxPumpPhase Phase)
{
    /// <summary>The resting state before any packet is read.</summary>
    public static DemuxPumpState Initial => new(DemuxPumpPhase.NeedRead);
}

/// <summary>One step of the machine: the next state paired with the action the shell must perform.</summary>
internal readonly record struct DemuxPumpTransition(DemuxPumpState State, DemuxPumpAction Action);

/// <summary>
/// The demux read pump expressed as a pure Mealy machine:
/// <c>δ : (state, input) → (state', output)</c>, where the input is a
/// <see cref="ReadOutcome"/> (the classified <i>and routed</i> read result) and the output
/// is a <see cref="DemuxPumpAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The demux pump is the sibling left un-lifted by ADR-0055: the
/// decode <c>send_packet</c>/<c>receive_frame</c> protocol became the pure
/// <see cref="DecodeProtocol"/> + <see cref="DecodeDriver"/> split, but its mirror — the
/// read/route loop in <see cref="DecodingPipeline.RunDemuxPumpAsync"/> — stayed hand-inlined,
/// fusing the native <c>av_read_frame</c>, the EOF/fault classification, the by-stream-index
/// routing, the clone-and-queue, the discard-on-no-consumer fall-through, and the
/// retain-on-cancel / drop-on-seek packet bookkeeping into one method. Lifting the sequencing
/// into this pure function gives one routing table the shell cranks, makes the whole pump
/// unit-testable from a scripted transcript with no FFmpeg, and turns the load-bearing
/// "retained pre-seek packet must be dropped on seek" rule (whose omission once produced a
/// stale-PTS hang, per <see cref="DecodingPipeline.DiscardPendingPacket"/>) into a
/// <i>representable state transition</i> rather than a comment.
/// </para>
/// <para>
/// This core deliberately does <b>not</b> own the format context's read cursor (that stays
/// inside the native <c>AVFormatContext</c>), nor any IO, clock, cancellation, or threading —
/// all of that is the shell's job (<see cref="DecodingPipeline.RunDemuxPumpAsync"/>). It is a
/// total function of <c>(DemuxPumpPhase, ReadOutcome)</c>.
/// </para>
/// </remarks>
internal static class DemuxPump
{
    /// <summary>
    /// Fold a classified read result together with the packet's stream index into the
    /// routed <see cref="ReadOutcome"/> vocabulary. This is the second of the demux-side
    /// ABI seams (the first being <see cref="DecodingPipeline.ClassifyDemuxReadResult"/>):
    /// it decides <i>which</i> sink a packet is bound for, or that it is unselected, given
    /// the selected video/audio stream indices and whether each decoder is present. It reads
    /// no FFmpeg state itself — the caller supplies the already-extracted stream index — so
    /// it stays a pure classification step the transcript tests can drive directly.
    /// </summary>
    /// <param name="kind">The result of <see cref="DecodingPipeline.ClassifyDemuxReadResult"/>.</param>
    /// <param name="streamIndex">The packet's stream index (only meaningful when <paramref name="kind"/> is <c>PacketAvailable</c>).</param>
    /// <param name="videoStreamIndex">The selected video stream index, or a negative sentinel when none.</param>
    /// <param name="audioStreamIndex">The selected audio stream index, or a negative sentinel when none.</param>
    /// <param name="hasVideoDecoder">Whether a video decoder is present to receive video packets.</param>
    /// <param name="hasAudioDecoder">Whether an audio decoder is present to receive audio packets.</param>
    public static ReadOutcome Route(
        DecodingPipeline.DemuxReadResultKind kind,
        int streamIndex,
        int videoStreamIndex,
        int audioStreamIndex,
        bool hasVideoDecoder,
        bool hasAudioDecoder
    ) =>
        kind switch
        {
            DecodingPipeline.DemuxReadResultKind.EndOfStream => ReadOutcome.EndOfStream,
            DecodingPipeline.DemuxReadResultKind.Fault => ReadOutcome.Fault,
            DecodingPipeline.DemuxReadResultKind.Malformed => ReadOutcome.Fault,
            DecodingPipeline.DemuxReadResultKind.PacketAvailable
                when streamIndex == videoStreamIndex && hasVideoDecoder => ReadOutcome.SelectedVideo,
            DecodingPipeline.DemuxReadResultKind.PacketAvailable
                when streamIndex == audioStreamIndex && hasAudioDecoder => ReadOutcome.SelectedAudio,
            DecodingPipeline.DemuxReadResultKind.PacketAvailable => ReadOutcome.Unselected,
            _ => throw new InvalidOperationException($"Unhandled DemuxReadResultKind value {kind}."),
        };

    /// <summary>
    /// Advance the machine one step, given the classified-and-routed result of the read it
    /// last asked the shell to perform. Always called from
    /// <see cref="DemuxPumpPhase.NeedRead"/> (the pump only reads when nothing is pending);
    /// the <see cref="DemuxPumpPhase.HavePending"/> phase is driven by
    /// <see cref="DeliverPending"/> / <see cref="Retain"/> / <see cref="Seek"/>, not by a
    /// read result.
    /// </summary>
    /// <param name="state">The current threaded state.</param>
    /// <param name="outcome">The <see cref="ReadOutcome"/> from the last read.</param>
    /// <returns>The next state and the action the shell must perform.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if called in <see cref="DemuxPumpPhase.HavePending"/> (use
    /// <see cref="DeliverPending"/>) or <see cref="DemuxPumpPhase.Done"/> (terminal).
    /// </exception>
    public static DemuxPumpTransition Advance(DemuxPumpState state, ReadOutcome outcome) =>
        state.Phase switch
        {
            DemuxPumpPhase.NeedRead => outcome switch
            {
                // Routed to a sink: clone+queue, then stay ready to read the next packet.
                ReadOutcome.SelectedVideo => new(state, DemuxPumpAction.RouteToVideo),
                ReadOutcome.SelectedAudio => new(state, DemuxPumpAction.RouteToAudio),
                // No consumer for this stream: drop it and read on.
                ReadOutcome.Unselected => new(state, DemuxPumpAction.DiscardUnselected),
                // Source exhausted: terminal.
                ReadOutcome.EndOfStream => new(
                    state with { Phase = DemuxPumpPhase.Done },
                    DemuxPumpAction.Complete
                ),
                ReadOutcome.Fault => new(
                    state with { Phase = DemuxPumpPhase.Done },
                    DemuxPumpAction.FaultRead
                ),
                _ => throw Unreachable(outcome),
            },

            DemuxPumpPhase.HavePending => throw new InvalidOperationException(
                "DemuxPump.Advance is not defined in the HavePending phase: a retained packet "
                    + "must be delivered with DeliverPending (or dropped with Seek), not advanced "
                    + "with a read result."
            ),

            _ => throw new InvalidOperationException(
                $"DemuxPump.Advance is not defined for phase {state.Phase}. Done is terminal."
            ),
        };

    /// <summary>
    /// The pump's first decision each loop turn: deliver a retained pending packet if one is
    /// held, otherwise read the next packet. This is the pure form of the
    /// <c>if (_pendingPacketPtr != 0) { send; continue; }</c> guard at the top of the old
    /// read loop — a held packet is always (re)delivered before any further reads.
    /// </summary>
    /// <param name="state">The current threaded state.</param>
    /// <returns>
    /// <see cref="DemuxPumpAction.DeliverPending"/> when in
    /// <see cref="DemuxPumpPhase.HavePending"/>, otherwise <see cref="DemuxPumpAction.ReadNext"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown in the terminal <see cref="DemuxPumpPhase.Done"/> phase.</exception>
    public static DemuxPumpTransition Step(DemuxPumpState state) =>
        state.Phase switch
        {
            DemuxPumpPhase.HavePending => new(state, DemuxPumpAction.DeliverPending),
            DemuxPumpPhase.NeedRead => new(state, DemuxPumpAction.ReadNext),
            _ => throw new InvalidOperationException(
                $"DemuxPump.Step is not defined for phase {state.Phase}. Done is terminal."
            ),
        };

    /// <summary>
    /// Record that a just-cloned packet could not be delivered to its sink (its queue write
    /// was interrupted, e.g. by cancellation during pause) and is now retained for redelivery.
    /// Moves the machine to <see cref="DemuxPumpPhase.HavePending"/>. This models the effect the
    /// shell performs in its cancellation-catch — the clone is held, not freed, so no demux work
    /// is lost across a pause/resume.
    /// </summary>
    public static DemuxPumpState Retain(DemuxPumpState state) =>
        state with { Phase = DemuxPumpPhase.HavePending };

    /// <summary>
    /// Record that the retained pending packet was successfully delivered to its sink. Returns
    /// the machine to <see cref="DemuxPumpPhase.NeedRead"/>.
    /// </summary>
    public static DemuxPumpState PendingDelivered(DemuxPumpState state) =>
        state with { Phase = DemuxPumpPhase.NeedRead };

    /// <summary>
    /// Apply a seek to the pump's pending-packet bookkeeping. A retained packet belongs to the
    /// pre-seek timeline and must <b>not</b> be delivered as the head of the post-seek stream, so
    /// <see cref="DemuxPumpPhase.HavePending"/> drops it (action <see cref="DemuxPumpAction.DropPending"/>)
    /// and returns to <see cref="DemuxPumpPhase.NeedRead"/>; with nothing pending the seek is a no-op
    /// (<see cref="DemuxPumpAction.None"/>). Encoding the invalidation as a transition is the whole
    /// point — the rule that a stale-PTS hang once violated is now unrepresentable as a missed step.
    /// </summary>
    public static DemuxPumpTransition Seek(DemuxPumpState state) =>
        state.Phase switch
        {
            DemuxPumpPhase.HavePending => new(
                state with { Phase = DemuxPumpPhase.NeedRead },
                DemuxPumpAction.DropPending
            ),
            // NeedRead or Done: nothing is retained, so there is nothing to invalidate.
            _ => new(state, DemuxPumpAction.None),
        };

    private static InvalidOperationException Unreachable(ReadOutcome outcome) =>
        new($"Unhandled ReadOutcome value {outcome}.");
}
