using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for the pure demux-pump Mealy core (<see cref="DemuxPump"/>) — the sibling
/// ADR-0055 left un-lifted on the read/route side. As with
/// <see cref="DecodeProtocolTests"/>, the whole point is that the demux read/route loop
/// can be exercised, and its transition table asserted, with <b>nothing plugged in</b>:
/// no FFmpeg binaries, no media files, no format context. The transition theories below
/// are the entire decision table; the driver tests script an in-memory "demux source"
/// that speaks the <see cref="ReadOutcome"/> vocabulary directly and assert the action
/// transcript the shell would perform.
/// </summary>
public sealed class DemuxPumpTests
{
    // ── The routing seam: (read kind, stream index) → ReadOutcome ─────────────

    /// <summary>
    /// <see cref="DemuxPump.Route"/> folds a classified read result together with the
    /// packet's stream index into the routed outcome. A selected, decoder-backed stream
    /// routes to its sink; everything else (an unselected stream, or a selected stream
    /// whose decoder is absent) is the no-consumer discard.
    /// </summary>
    [Theory]
    // Video stream + video decoder present → SelectedVideo.
    [InlineData(DecodingPipeline.DemuxReadResultKind.PacketAvailable, 0, 0, 1, true, true, ReadOutcome.SelectedVideo)]
    // Audio stream + audio decoder present → SelectedAudio.
    [InlineData(DecodingPipeline.DemuxReadResultKind.PacketAvailable, 1, 0, 1, true, true, ReadOutcome.SelectedAudio)]
    // Some other stream index → Unselected.
    [InlineData(DecodingPipeline.DemuxReadResultKind.PacketAvailable, 7, 0, 1, true, true, ReadOutcome.Unselected)]
    // Selected video index but NO video decoder → Unselected (discarded, not routed).
    [InlineData(DecodingPipeline.DemuxReadResultKind.PacketAvailable, 0, 0, 1, false, true, ReadOutcome.Unselected)]
    // Selected audio index but NO audio decoder → Unselected.
    [InlineData(DecodingPipeline.DemuxReadResultKind.PacketAvailable, 1, 0, 1, true, false, ReadOutcome.Unselected)]
    // Video-only pipeline (no audio stream, sentinel -1): an audio-less packet is Unselected.
    [InlineData(DecodingPipeline.DemuxReadResultKind.PacketAvailable, 3, 0, -1, true, false, ReadOutcome.Unselected)]
    // EOF and faults ignore the stream index entirely.
    [InlineData(DecodingPipeline.DemuxReadResultKind.EndOfStream, -1, 0, 1, true, true, ReadOutcome.EndOfStream)]
    [InlineData(DecodingPipeline.DemuxReadResultKind.Fault, -1, 0, 1, true, true, ReadOutcome.Fault)]
    // Malformed (positive, non-zero av_read_frame return) collapses onto Fault.
    [InlineData(DecodingPipeline.DemuxReadResultKind.Malformed, -1, 0, 1, true, true, ReadOutcome.Fault)]
    public void Route_ClassifiesByKindAndStream(
        object kind,
        int streamIndex,
        int videoStreamIndex,
        int audioStreamIndex,
        bool hasVideoDecoder,
        bool hasAudioDecoder,
        object expected
    )
    {
        // Parameters are object for the same CS0051 reason as DecodeProtocolTests: a public
        // test signature cannot expose the internal enums, so the [InlineData] rows name them
        // for readability and they arrive boxed.
        var outcome = DemuxPump.Route(
            (DecodingPipeline.DemuxReadResultKind)kind,
            streamIndex,
            videoStreamIndex,
            audioStreamIndex,
            hasVideoDecoder,
            hasAudioDecoder
        );

        Assert.Equal((ReadOutcome)expected, outcome);
    }

    [Fact]
    public void Route_PrefersVideoWhenVideoAndAudioShareAnIndex()
    {
        // Degenerate but well-defined: if the same index were both selected video and audio,
        // the video arm wins (it is checked first), so the table stays total and deterministic.
        var outcome = DemuxPump.Route(
            DecodingPipeline.DemuxReadResultKind.PacketAvailable,
            streamIndex: 2,
            videoStreamIndex: 2,
            audioStreamIndex: 2,
            hasVideoDecoder: true,
            hasAudioDecoder: true
        );

        Assert.Equal(ReadOutcome.SelectedVideo, outcome);
    }

    // ── The pure transition table: Advance(phase, outcome) ────────────────────

    /// <summary>
    /// The complete <see cref="DemuxPump.Advance"/> function from the readable
    /// <see cref="DemuxPumpPhase.NeedRead"/> phase: δ(NeedRead, outcome) → (phase', action).
    /// Every routed read outcome has exactly one row. A future change to routing moves one
    /// row here, in review, with no FFmpeg in the loop.
    /// </summary>
    [Theory]
    [InlineData(ReadOutcome.SelectedVideo, DemuxPumpPhase.NeedRead, DemuxPumpAction.RouteToVideo)]
    [InlineData(ReadOutcome.SelectedAudio, DemuxPumpPhase.NeedRead, DemuxPumpAction.RouteToAudio)]
    [InlineData(ReadOutcome.Unselected, DemuxPumpPhase.NeedRead, DemuxPumpAction.DiscardUnselected)]
    [InlineData(ReadOutcome.EndOfStream, DemuxPumpPhase.Done, DemuxPumpAction.Complete)]
    [InlineData(ReadOutcome.Fault, DemuxPumpPhase.Done, DemuxPumpAction.FaultRead)]
    public void Advance_FromNeedRead_FollowsTransitionTable(
        object outcome,
        object expectedPhase,
        object expectedAction
    )
    {
        var t = DemuxPump.Advance(
            new DemuxPumpState(DemuxPumpPhase.NeedRead),
            (ReadOutcome)outcome
        );

        Assert.Equal((DemuxPumpPhase)expectedPhase, t.State.Phase);
        Assert.Equal((DemuxPumpAction)expectedAction, t.Action);
    }

    [Theory]
    [InlineData(DemuxPumpPhase.HavePending)] // a held packet must be DeliverPending'd / Seek'd, not advanced
    [InlineData(DemuxPumpPhase.Done)] // terminal
    public void Advance_InNonReadablePhase_Throws(object phase)
    {
        Assert.Throws<InvalidOperationException>(
            () => DemuxPump.Advance(new DemuxPumpState((DemuxPumpPhase)phase), ReadOutcome.Unselected)
        );
    }

    // ── Step: deliver-pending-first vs read ───────────────────────────────────

    [Fact]
    public void Step_WithNothingPending_ReadsNext()
    {
        var t = DemuxPump.Step(DemuxPumpState.Initial);

        Assert.Equal(DemuxPumpPhase.NeedRead, t.State.Phase);
        Assert.Equal(DemuxPumpAction.ReadNext, t.Action);
    }

    [Fact]
    public void Step_WithPending_DeliversPendingFirst()
    {
        var t = DemuxPump.Step(DemuxPump.Retain(DemuxPumpState.Initial));

        Assert.Equal(DemuxPumpPhase.HavePending, t.State.Phase);
        Assert.Equal(DemuxPumpAction.DeliverPending, t.Action);
    }

    [Fact]
    public void Step_InDonePhase_Throws() =>
        Assert.Throws<InvalidOperationException>(
            () => DemuxPump.Step(new DemuxPumpState(DemuxPumpPhase.Done))
        );

    // ── Pending lifecycle: Retain ↔ PendingDelivered ──────────────────────────

    [Fact]
    public void Retain_MovesToHavePending() =>
        Assert.Equal(DemuxPumpPhase.HavePending, DemuxPump.Retain(DemuxPumpState.Initial).Phase);

    [Fact]
    public void PendingDelivered_ReturnsToNeedRead()
    {
        var held = DemuxPump.Retain(DemuxPumpState.Initial);

        Assert.Equal(DemuxPumpPhase.NeedRead, DemuxPump.PendingDelivered(held).Phase);
    }

    // ── Seek: the pre-seek-packet invalidation, as a transition ───────────────

    /// <summary>
    /// The load-bearing rule (whose omission once produced a stale-PTS hang): a packet
    /// retained from the pre-seek timeline must be dropped on seek, not delivered as the head
    /// of the post-seek stream. Encoded here as a transition, not a comment.
    /// </summary>
    [Fact]
    public void Seek_WithPending_DropsItAndReturnsToNeedRead()
    {
        var t = DemuxPump.Seek(DemuxPump.Retain(DemuxPumpState.Initial));

        Assert.Equal(DemuxPumpAction.DropPending, t.Action);
        Assert.Equal(DemuxPumpPhase.NeedRead, t.State.Phase);
    }

    [Fact]
    public void Seek_WithNothingPending_IsNoOp()
    {
        var t = DemuxPump.Seek(DemuxPumpState.Initial);

        Assert.Equal(DemuxPumpAction.None, t.Action);
        Assert.Equal(DemuxPumpPhase.NeedRead, t.State.Phase);
    }

    [Fact]
    public void Seek_AfterDrop_NextStepReads()
    {
        // Drop-then-read: once a seek invalidates the held packet, the very next pump turn
        // reads (it does not try to deliver a now-freed packet).
        var afterSeek = DemuxPump.Seek(DemuxPump.Retain(DemuxPumpState.Initial)).State;

        Assert.Equal(DemuxPumpAction.ReadNext, DemuxPump.Step(afterSeek).Action);
    }

    // ── Whole-pump transcripts, driven by a scripted source (no FFmpeg) ───────

    [Fact]
    public void Drive_RoutesEachPacketToItsStream_ThenCompletesAtEof()
    {
        // v, a, v, then EOF.
        var source = new FakeDemuxSource(
            [
                Read.Video(),
                Read.Audio(),
                Read.Video(),
                Read.Eof(),
            ]
        );

        var transcript = DrivePump(source);

        Assert.Equal(
            new[]
            {
                DemuxPumpAction.RouteToVideo,
                DemuxPumpAction.RouteToAudio,
                DemuxPumpAction.RouteToVideo,
                DemuxPumpAction.Complete,
            },
            transcript
        );
    }

    [Fact]
    public void Drive_DiscardsUnselectedPackets_WithoutRouting()
    {
        // An unselected (no-consumer) packet sits between two video packets; it is discarded,
        // never routed, and does not stop the pump (ADR-0059).
        var source = new FakeDemuxSource(
            [
                Read.Video(),
                Read.Unselected(),
                Read.Video(),
                Read.Eof(),
            ]
        );

        var transcript = DrivePump(source);

        Assert.Equal(
            new[]
            {
                DemuxPumpAction.RouteToVideo,
                DemuxPumpAction.DiscardUnselected,
                DemuxPumpAction.RouteToVideo,
                DemuxPumpAction.Complete,
            },
            transcript
        );
    }

    [Fact]
    public void Drive_FaultRead_StopsThePumpAtFault()
    {
        var source = new FakeDemuxSource([Read.Video(), Read.Fault()]);

        var transcript = DrivePump(source);

        Assert.Equal(
            new[] { DemuxPumpAction.RouteToVideo, DemuxPumpAction.FaultRead },
            transcript
        );
    }

    [Fact]
    public void Drive_StartingWithPending_DeliversItBeforeAnyRead()
    {
        // Models a resume after a cancelled run: a packet was retained, so the pump delivers
        // it first, then proceeds to read.
        var source = new FakeDemuxSource([Read.Audio(), Read.Eof()]);

        var transcript = DrivePump(source, startPending: true);

        Assert.Equal(
            new[]
            {
                DemuxPumpAction.DeliverPending,
                DemuxPumpAction.RouteToAudio,
                DemuxPumpAction.Complete,
            },
            transcript
        );
    }

    [Fact]
    public void Drive_SeekBeforeResume_DropsPending_ThenReads()
    {
        // Pause retained a packet; a seek lands before resume. The held pre-seek packet is
        // dropped (not delivered as the post-seek head), then the pump reads the post-seek
        // stream. This is the stale-PTS-hang guard as a transcript.
        var source = new FakeDemuxSource([Read.Video(), Read.Eof()]);

        var transcript = DrivePump(source, startPending: true, seekBeforeRun: true);

        Assert.Equal(
            new[]
            {
                DemuxPumpAction.DropPending,
                DemuxPumpAction.RouteToVideo,
                DemuxPumpAction.Complete,
            },
            transcript
        );
    }

    // ── Driver + scripted source (the FFmpeg-free shell stand-in) ─────────────

    /// <summary>
    /// A scripted read result: the routed <see cref="ReadOutcome"/> a single
    /// <c>av_read_frame</c>+route would have produced. The factory helpers read like the
    /// stream a real pump would see.
    /// </summary>
    private readonly record struct Read(ReadOutcome Outcome)
    {
        public static Read Video() => new(ReadOutcome.SelectedVideo);

        public static Read Audio() => new(ReadOutcome.SelectedAudio);

        public static Read Unselected() => new(ReadOutcome.Unselected);

        public static Read Eof() => new(ReadOutcome.EndOfStream);

        public static Read Fault() => new(ReadOutcome.Fault);
    }

    /// <summary>An in-memory source that hands out scripted <see cref="ReadOutcome"/>s in order.</summary>
    private sealed class FakeDemuxSource
    {
        private readonly Queue<Read> _reads;

        public FakeDemuxSource(IEnumerable<Read> reads) => _reads = new Queue<Read>(reads);

        public ReadOutcome NextOutcome() => _reads.Dequeue().Outcome;
    }

    /// <summary>
    /// Cranks <see cref="DemuxPump"/> exactly the way <c>RunDemuxPumpAsync</c> does — Step to
    /// choose read-vs-deliver, Advance on the routed outcome, thread the state — but performs
    /// no IO: it records the action transcript instead of touching native packets. This is the
    /// pure-control-flow proof that the shell's branching is the core's table.
    /// </summary>
    private static List<DemuxPumpAction> DrivePump(
        FakeDemuxSource source,
        bool startPending = false,
        bool seekBeforeRun = false
    )
    {
        var transcript = new List<DemuxPumpAction>();
        var state = startPending ? DemuxPump.Retain(DemuxPumpState.Initial) : DemuxPumpState.Initial;

        // A seek between runs invalidates any retained packet (DiscardPendingPacket's path).
        if (seekBeforeRun)
        {
            var seek = DemuxPump.Seek(state);
            if (seek.Action != DemuxPumpAction.None)
                transcript.Add(seek.Action);
            state = seek.State;
        }

        while (true)
        {
            var step = DemuxPump.Step(state);

            if (step.Action == DemuxPumpAction.DeliverPending)
            {
                transcript.Add(step.Action);
                state = DemuxPump.PendingDelivered(state);
                continue;
            }

            // ReadNext: the shell would av_read_frame + Route here; the source scripts the result.
            var outcome = source.NextOutcome();
            var transition = DemuxPump.Advance(state, outcome);
            state = transition.State;
            transcript.Add(transition.Action);

            if (
                transition.Action == DemuxPumpAction.Complete
                || transition.Action == DemuxPumpAction.FaultRead
            )
            {
                return transcript;
            }
        }
    }
}
