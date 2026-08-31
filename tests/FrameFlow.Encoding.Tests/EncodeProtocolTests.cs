using FrameFlow.Encoding.Internal;
using FrameFlow.Native.Interop;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Tests for the pure encode Mealy core (<see cref="EncodeProtocol"/>) and the thin shell
/// that cranks it (<see cref="EncodeDriver"/>) — the encode-direction mirror of ADR-0055's
/// decode core.
/// </summary>
/// <remarks>
/// The whole point of the core is that the FFmpeg <c>send_frame</c> / <c>receive_packet</c>
/// protocol can be exercised, and its transition table asserted, with <b>nothing plugged
/// in</b>: no FFmpeg binaries, no media files, no codec context. The transition theory below
/// is the entire decision table; the driver tests script a fake codec that speaks the
/// protocol vocabulary directly. These tests always run (they are FFmpeg-free), so they
/// cover the encode protocol even in environments without the native shared libraries.
/// </remarks>
public sealed class EncodeProtocolTests
{
    // ── The pure transition table ───────────────────────────────────────────

    [Fact]
    public void Begin_AsksToSendFirst()
    {
        var t = EncodeProtocol.Begin();

        Assert.Equal(EncodePhase.Feeding, t.State.Phase);
        Assert.Equal(EncodeAction.SendInput, t.Action);
    }

    /// <summary>
    /// The complete transition function δ(phase, codecReturn) → (phase', action).
    /// Twelve rows — every advanceable (phase, return) pair. If a future change adds a
    /// branch or flips an action, exactly one row here moves, in review, with no encoder
    /// or hardware involved. This table is the mirror image of <c>DecodeProtocolTests</c>'s,
    /// with the encode-direction action names (ReceivePacket / EmitThenReceive).
    /// </summary>
    [Theory]
    // ── Feeding: awaiting the result of a send ──
    [InlineData(EncodePhase.Feeding, CodecReturn.Ok, EncodePhase.Draining, EncodeAction.ReceivePacket)]
    [InlineData(EncodePhase.Feeding, CodecReturn.EndOfStream, EncodePhase.Draining, EncodeAction.ReceivePacket)]
    [InlineData(EncodePhase.Feeding, CodecReturn.Again, EncodePhase.DrainingThenRetry, EncodeAction.ReceivePacket)]
    [InlineData(EncodePhase.Feeding, CodecReturn.Fault, EncodePhase.Done, EncodeAction.FaultOnSend)]
    // ── Draining: send accepted, pulling packets ──
    [InlineData(EncodePhase.Draining, CodecReturn.Ok, EncodePhase.Draining, EncodeAction.EmitThenReceive)]
    [InlineData(EncodePhase.Draining, CodecReturn.Again, EncodePhase.Idle, EncodeAction.NeedNextInput)]
    [InlineData(EncodePhase.Draining, CodecReturn.EndOfStream, EncodePhase.Done, EncodeAction.Complete)]
    [InlineData(EncodePhase.Draining, CodecReturn.Fault, EncodePhase.Done, EncodeAction.FaultOnReceive)]
    // ── DrainingThenRetry: send said Again; drain, then re-send the SAME input ──
    [InlineData(EncodePhase.DrainingThenRetry, CodecReturn.Ok, EncodePhase.DrainingThenRetry, EncodeAction.EmitThenReceive)]
    [InlineData(EncodePhase.DrainingThenRetry, CodecReturn.Again, EncodePhase.Feeding, EncodeAction.SendInput)]
    [InlineData(EncodePhase.DrainingThenRetry, CodecReturn.EndOfStream, EncodePhase.Done, EncodeAction.Complete)]
    [InlineData(EncodePhase.DrainingThenRetry, CodecReturn.Fault, EncodePhase.Done, EncodeAction.FaultOnReceive)]
    public void Advance_FollowsTransitionTable(
        object phase,
        object result,
        object expectedPhase,
        object expectedAction
    )
    {
        // Parameters are typed as object because xUnit test methods must be public, and
        // a public signature cannot expose the internal protocol enums (CS0051). The
        // [InlineData] rows still reference them by name for readability; they arrive
        // boxed and are unboxed here.
        var t = EncodeProtocol.Advance(new EncodeState((EncodePhase)phase), (CodecReturn)result);

        Assert.Equal((EncodePhase)expectedPhase, t.State.Phase);
        Assert.Equal((EncodeAction)expectedAction, t.Action);
    }

    [Theory]
    [InlineData(EncodePhase.Idle)] // requires Begin()
    [InlineData(EncodePhase.Done)] // terminal
    public void Advance_InNonAdvanceablePhase_Throws(object phase)
    {
        Assert.Throws<InvalidOperationException>(
            () => EncodeProtocol.Advance(new EncodeState((EncodePhase)phase), CodecReturn.Ok)
        );
    }

    [Fact]
    public void Initial_IsIdle() => Assert.Equal(EncodePhase.Idle, EncodeState.Initial.Phase);

    // ── The FFmpeg seam (the only ABI-aware line) ────────────────────────────

    [Fact]
    public void Classify_NonNegative_IsOk()
    {
        Assert.Equal(CodecReturn.Ok, EncodeDriver.Classify(0));
        Assert.Equal(CodecReturn.Ok, EncodeDriver.Classify(42));
    }

    [Fact]
    public void Classify_Eagain_IsAgain() =>
        Assert.Equal(CodecReturn.Again, EncodeDriver.Classify(FFAvUtil.AvErrorEagain));

    [Fact]
    public void Classify_Eof_IsEndOfStream() =>
        Assert.Equal(CodecReturn.EndOfStream, EncodeDriver.Classify(FFAvUtil.AvErrorEof));

    [Fact]
    public void Classify_OtherNegative_IsFault() =>
        Assert.Equal(CodecReturn.Fault, EncodeDriver.Classify(-1394));

    // ── The shell, driven by a scripted fake codec ───────────────────────────

    [Fact]
    public void Drive_TwoFramesThenFlush_EmitsAllPacketsInOrder()
    {
        // Frame A produces packet 1; frame B produces packets 2 and 3. CFR, no reorder.
        var codec = new FakeCodec([new FakeInput(Stall: 0, Packets: [1]), new FakeInput(0, [2, 3])]);

        var ids = DriveToCompletion(codec);

        Assert.Equal(new[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public void Drive_PerFrameRun_DoesNotSignalEndOfStream()
    {
        // A normal per-frame encode consumes the input and asks for the next (NeedNextInput
        // → Run returns false). Only the flush reaches Complete.
        var codec = new FakeCodec([new FakeInput(0, [1])]);
        var output = new List<FakePacket>();

        codec.Present(codec.NextScriptedInput());
        bool eofOnFrame = EncodeDriver.Run(codec, output);

        Assert.False(eofOnFrame);
        Assert.Equal(new[] { 1 }, output.Select(p => p.Id));
    }

    [Fact]
    public void Drive_FlushRun_SignalsEndOfStream()
    {
        var codec = new FakeCodec([new FakeInput(0, [1])]);
        var output = new List<FakePacket>();

        codec.Present(codec.NextScriptedInput());
        EncodeDriver.Run(codec, output);

        output.Clear();
        codec.Present(null); // flush
        bool eofOnFlush = EncodeDriver.Run(codec, output);

        Assert.True(eofOnFlush);
    }

    [Fact]
    public void Drive_PacketsHeldUntilFlush_AreDrained()
    {
        // Models encoder lookahead/reorder latency: packet 99 only emerges when the encoder
        // is flushed at end-of-stream, after the frame that produced packet 1.
        var codec = new FakeCodec([new FakeInput(0, [1])], bufferedAtEof: [99]);

        var ids = DriveToCompletion(codec);

        Assert.Equal(new[] { 1, 99 }, ids);
    }

    /// <summary>
    /// The headline of the encode mirror. When <c>avcodec_send_frame</c> returns
    /// <c>EAGAIN</c>, the encoder is refusing the frame until its output is drained.
    /// The protocol drains, then re-sends the <b>same</b> frame — so its packet is
    /// produced, not dropped. This is the exact branch the hand-inlined
    /// <c>SendFrameAndDrain</c> loop relied on (its <c>while (sendRc == EAGAIN)</c>),
    /// now represented as <see cref="EncodePhase.DrainingThenRetry"/>.
    /// </summary>
    [Fact]
    public void Drive_SendEagain_DrainsThenResends_WithoutDroppingPackets()
    {
        // The frame stalls once (send → EAGAIN) before the encoder accepts it.
        var codec = new FakeCodec([new FakeInput(Stall: 1, Packets: [10])]);

        var ids = DriveToCompletion(codec);

        Assert.Equal(new[] { 10 }, ids); // the stalled frame's packet survives
        // Sends: EAGAIN send + accepted re-send (frame), then the flush send.
        Assert.Equal(3, codec.SendCalls);
    }

    [Fact]
    public void Drive_SendEagain_EmitsBufferedPacketDuringDrain_ThenResendsFrame()
    {
        // The encoder is full: it holds packet 7 buffered AND refuses the new frame once.
        // The drain-then-retry branch must emit 7 (during the forced drain) and still land
        // the re-sent frame's packet 8 — in production order 7 then 8.
        var codec = new FakeCodec([new FakeInput(Stall: 1, Packets: [8], BufferedBeforeAccept: [7])]);

        var ids = DriveToCompletion(codec);

        Assert.Equal(new[] { 7, 8 }, ids);
    }

    [Fact]
    public void Drive_SendFault_Throws()
    {
        var codec = new FakeCodec([new FakeInput(0, [1])], faultOnSendNumber: 1);
        var output = new List<FakePacket>();
        codec.Present(codec.NextScriptedInput());

        var ex = Assert.Throws<InvalidOperationException>(() => EncodeDriver.Run(codec, output));
        Assert.Contains("send_frame", ex.Message);
    }

    [Fact]
    public void Drive_ReceiveFault_Throws()
    {
        var codec = new FakeCodec([new FakeInput(0, [1])], faultOnReceiveNumber: 1);
        var output = new List<FakePacket>();
        codec.Present(codec.NextScriptedInput());

        var ex = Assert.Throws<InvalidOperationException>(() => EncodeDriver.Run(codec, output));
        Assert.Contains("receive_packet", ex.Message);
    }

    [Fact]
    public void Drive_FrameProducingNoPackets_StillAdvancesToFlush()
    {
        // A frame the encoder swallows entirely (buffered, no immediate output) must not
        // wedge the machine: Run returns false, then the flush drains the held packet.
        var codec = new FakeCodec([new FakeInput(0, Packets: [])], bufferedAtEof: [5]);

        var ids = DriveToCompletion(codec);

        Assert.Equal(new[] { 5 }, ids);
    }

    [Fact]
    public void Run_NullCodec_Throws() =>
        Assert.Throws<ArgumentNullException>(() => EncodeDriver.Run<FakePacket>(null!, []));

    [Fact]
    public void Run_NullOutput_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => EncodeDriver.Run(new FakeCodec([]), null!)
        );

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the codec the way <c>H264VideoEncoder</c> does: one <see cref="EncodeDriver.Run"/>
    /// per scripted frame (each presents the frame, returns false), then one final
    /// <see cref="EncodeDriver.Run"/> for the null flush (returns true). Returns the produced
    /// packet ids in order.
    /// </summary>
    private static List<int> DriveToCompletion(FakeCodec codec)
    {
        var output = new List<FakePacket>();

        FakeInput? input;
        while ((input = codec.NextScriptedInput()) is not null)
        {
            codec.Present(input);
            bool eof = EncodeDriver.Run(codec, output);
            Assert.False(eof, "A per-frame encode must not reach end-of-stream.");
        }

        codec.Present(null); // flush
        bool eofOnFlush = EncodeDriver.Run(codec, output);
        Assert.True(eofOnFlush, "The flush must reach end-of-stream.");

        return output.Select(p => p.Id).ToList();
    }

    /// <summary>A managed "packet" — just an id so tests can assert ordering.</summary>
    private sealed record FakePacket(int Id);

    /// <summary>
    /// One scripted input frame: <paramref name="Stall"/> send-EAGAINs before the encoder
    /// accepts it; <paramref name="BufferedBeforeAccept"/> packets are already held and drain
    /// during the stall; then it produces <paramref name="Packets"/>.
    /// </summary>
    private sealed record FakeInput(int Stall, int[] Packets, int[]? BufferedBeforeAccept = null);

    /// <summary>
    /// An in-memory codec that speaks the <see cref="CodecReturn"/> vocabulary directly —
    /// no FFmpeg. It is presented one input at a time (mirroring <c>H264VideoEncoder</c>
    /// setting <c>_pendingSendFrame</c> before each <see cref="EncodeDriver.Run"/>): a frame
    /// buffers its packet list on acceptance, releases buffered packets on receive, optionally
    /// stalls a send (EAGAIN) to exercise the re-send branch, optionally faults a send or
    /// receive, and optionally holds packets back until the end-of-stream flush.
    /// </summary>
    private sealed class FakeCodec : IEncodeCodec<FakePacket>
    {
        private readonly Queue<FakeInput> _script;
        private readonly Queue<int> _bufferedAtEof;
        private readonly int _faultOnSendNumber; // 0 = never
        private readonly int _faultOnReceiveNumber; // 0 = never
        private readonly Queue<int> _pending = new();

        private FakeInput? _current;
        private bool _flushing;
        private int _stallRemaining;
        private bool _flushAccepted;
        private int _lastBuilt = -1;

        public int SendCalls { get; private set; }
        public int ReceiveCalls { get; private set; }

        public FakeCodec(
            IEnumerable<FakeInput> inputs,
            int[]? bufferedAtEof = null,
            int faultOnSendNumber = 0,
            int faultOnReceiveNumber = 0
        )
        {
            _script = new Queue<FakeInput>(inputs);
            _bufferedAtEof = new Queue<int>(bufferedAtEof ?? []);
            _faultOnSendNumber = faultOnSendNumber;
            _faultOnReceiveNumber = faultOnReceiveNumber;
        }

        /// <summary>Dequeue the next scripted frame the harness should present, or null when exhausted.</summary>
        public FakeInput? NextScriptedInput() => _script.Count > 0 ? _script.Dequeue() : null;

        /// <summary>
        /// Present an input to the codec before driving it: a frame, or <see langword="null"/>
        /// for the end-of-stream flush. Mirrors <c>H264VideoEncoder</c> assigning
        /// <c>_pendingSendFrame</c> immediately before <see cref="EncodeDriver.Run"/>.
        /// </summary>
        public void Present(FakeInput? input)
        {
            if (input is null)
            {
                _current = null;
                _flushing = true;
                _stallRemaining = 0;
                return;
            }

            _current = input;
            _flushing = false;
            _stallRemaining = input.Stall;
            foreach (var id in input.BufferedBeforeAccept ?? [])
            {
                _pending.Enqueue(id);
            }
        }

        public CodecReturn TrySendFrame()
        {
            SendCalls++;

            if (_faultOnSendNumber != 0 && SendCalls == _faultOnSendNumber)
            {
                return CodecReturn.Fault;
            }

            if (_flushing)
            {
                _flushAccepted = true; // flush accepted; receives drain then EOF
                return CodecReturn.Ok;
            }

            if (_stallRemaining > 0)
            {
                // Encoder full: it will not accept this frame until drained. The driver
                // must drain, then re-send THIS frame (DrainingThenRetry).
                _stallRemaining--;
                return CodecReturn.Again;
            }

            foreach (var id in _current!.Packets)
            {
                _pending.Enqueue(id);
            }

            return CodecReturn.Ok;
        }

        public CodecReturn ReceivePacket()
        {
            ReceiveCalls++;

            if (_faultOnReceiveNumber != 0 && ReceiveCalls == _faultOnReceiveNumber)
            {
                return CodecReturn.Fault;
            }

            if (_pending.Count > 0)
            {
                _lastBuilt = _pending.Dequeue();
                return CodecReturn.Ok;
            }

            if (_flushAccepted && _bufferedAtEof.Count > 0)
            {
                _lastBuilt = _bufferedAtEof.Dequeue();
                return CodecReturn.Ok;
            }

            // Outside the flush, an exhausted drain means "feed the next frame" (Again);
            // once the flush is accepted and all buffered packets are gone, it is EOF.
            return _flushAccepted ? CodecReturn.EndOfStream : CodecReturn.Again;
        }

        public FakePacket? BuildPacket() => new(_lastBuilt);
    }
}
