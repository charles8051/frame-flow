using FrameFlow.Decoding.Internal;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for the pure decode Mealy core (<see cref="DecodeProtocol"/>) and the shared
/// shell that cranks it (<see cref="DecodeDriver"/>) — ADR-0055.
/// </summary>
/// <remarks>
/// The whole point of the core is that the FFmpeg send/receive protocol can be exercised,
/// and its transition table asserted, with <b>nothing plugged in</b>: no FFmpeg binaries,
/// no media files, no codec context. The transition theory below is the entire decision
/// table; the driver tests script a fake codec that speaks the protocol vocabulary
/// directly.
/// </remarks>
public sealed class DecodeProtocolTests
{
    // ── The pure transition table ───────────────────────────────────────────

    [Fact]
    public void Begin_AsksToSendFirst()
    {
        var t = DecodeProtocol.Begin();

        Assert.Equal(DecodePhase.Feeding, t.State.Phase);
        Assert.Equal(DecodeAction.SendInput, t.Action);
    }

    /// <summary>
    /// The complete transition function δ(phase, codecReturn) → (phase', action).
    /// Twelve rows — every advanceable (phase, return) pair. If a future change adds a
    /// branch or flips an action, exactly one row here moves, in review, with no decoder
    /// or hardware involved.
    /// </summary>
    [Theory]
    // ── Feeding: awaiting the result of a send ──
    [InlineData(DecodePhase.Feeding, CodecReturn.Ok, DecodePhase.Draining, DecodeAction.ReceiveFrame)]
    [InlineData(DecodePhase.Feeding, CodecReturn.EndOfStream, DecodePhase.Draining, DecodeAction.ReceiveFrame)]
    [InlineData(DecodePhase.Feeding, CodecReturn.Again, DecodePhase.DrainingThenRetry, DecodeAction.ReceiveFrame)]
    [InlineData(DecodePhase.Feeding, CodecReturn.Fault, DecodePhase.Done, DecodeAction.FaultOnSend)]
    // ── Draining: send accepted, pulling frames ──
    [InlineData(DecodePhase.Draining, CodecReturn.Ok, DecodePhase.Draining, DecodeAction.EmitThenReceive)]
    [InlineData(DecodePhase.Draining, CodecReturn.Again, DecodePhase.Idle, DecodeAction.NeedNextInput)]
    [InlineData(DecodePhase.Draining, CodecReturn.EndOfStream, DecodePhase.Done, DecodeAction.Complete)]
    [InlineData(DecodePhase.Draining, CodecReturn.Fault, DecodePhase.Done, DecodeAction.FaultOnReceive)]
    // ── DrainingThenRetry: send said Again; drain, then re-send the SAME input ──
    [InlineData(DecodePhase.DrainingThenRetry, CodecReturn.Ok, DecodePhase.DrainingThenRetry, DecodeAction.EmitThenReceive)]
    [InlineData(DecodePhase.DrainingThenRetry, CodecReturn.Again, DecodePhase.Feeding, DecodeAction.SendInput)]
    [InlineData(DecodePhase.DrainingThenRetry, CodecReturn.EndOfStream, DecodePhase.Done, DecodeAction.Complete)]
    [InlineData(DecodePhase.DrainingThenRetry, CodecReturn.Fault, DecodePhase.Done, DecodeAction.FaultOnReceive)]
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
        var t = DecodeProtocol.Advance(new DecodeState((DecodePhase)phase), (CodecReturn)result);

        Assert.Equal((DecodePhase)expectedPhase, t.State.Phase);
        Assert.Equal((DecodeAction)expectedAction, t.Action);
    }

    [Theory]
    [InlineData(DecodePhase.Idle)] // requires Begin()
    [InlineData(DecodePhase.Done)] // terminal
    public void Advance_InNonAdvanceablePhase_Throws(object phase)
    {
        Assert.Throws<InvalidOperationException>(
            () => DecodeProtocol.Advance(new DecodeState((DecodePhase)phase), CodecReturn.Ok)
        );
    }

    // ── The FFmpeg seam (the only ABI-aware line) ────────────────────────────

    [Fact]
    public void Classify_NonNegative_IsOk()
    {
        Assert.Equal(CodecReturn.Ok, DecodeDriver.Classify(0));
        Assert.Equal(CodecReturn.Ok, DecodeDriver.Classify(42));
    }

    [Fact]
    public void Classify_Eagain_IsAgain() =>
        Assert.Equal(CodecReturn.Again, DecodeDriver.Classify(FFAvUtil.AvErrorEagain));

    [Fact]
    public void Classify_Eof_IsEndOfStream() =>
        Assert.Equal(CodecReturn.EndOfStream, DecodeDriver.Classify(FFAvUtil.AvErrorEof));

    [Fact]
    public void Classify_OtherNegative_IsFault() =>
        Assert.Equal(CodecReturn.Fault, DecodeDriver.Classify(-1394));

    // ── The shared shell, driven by a scripted fake codec ────────────────────

    [Fact]
    public async Task Drive_TwoPacketsThenFlush_YieldsAllFramesInOrder()
    {
        var codec = new FakeCodec([new FakeInput(Stall: 0, Frames: [1]), new FakeInput(0, [2, 3])]);

        var ids = await CollectIdsAsync(codec);

        Assert.Equal(new[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public async Task Drive_FramesHeldUntilFlush_AreDrained()
    {
        // Models decoder reorder latency: frame 99 only emerges when the decoder is
        // flushed at end-of-stream, after the packet that produced frame 1.
        var codec = new FakeCodec([new FakeInput(0, [1])], bufferedAtEof: [99]);

        var ids = await CollectIdsAsync(codec);

        Assert.Equal(new[] { 1, 99 }, ids);
    }

    /// <summary>
    /// The headline of ADR-0055. When <c>avcodec_send_packet</c> returns
    /// <c>EAGAIN</c>, the decoder is refusing the packet until its output is drained.
    /// The protocol drains, then re-sends the <b>same</b> packet — so its frame is
    /// produced, not dropped. <c>AudioDecoder.DecodeAsync</c> omits this branch today
    /// (it sends once and moves to the next packet), which would silently drop the
    /// stalled packet's output; routing audio through this shared core fixes it by
    /// construction.
    /// </summary>
    [Fact]
    public async Task Drive_SendEagain_DrainsThenResends_WithoutDroppingFrames()
    {
        // The packet stalls once (send → EAGAIN) before the decoder accepts it.
        var codec = new FakeCodec([new FakeInput(Stall: 1, Frames: [10])]);

        var ids = await CollectIdsAsync(codec);

        Assert.Equal(new[] { 10 }, ids); // the stalled packet's frame survives
        Assert.Equal(3, codec.SendCalls); // EAGAIN send + accepted re-send + flush send
    }

    [Fact]
    public async Task Drive_ReceiveFault_Throws()
    {
        var codec = new FakeCodec([new FakeInput(0, [1])], faultOnReceiveNumber: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectIdsAsync(codec));
    }

    [Fact]
    public async Task Drive_CancelledToken_StopsCleanly()
    {
        var codec = new FakeCodec([new FakeInput(0, [1])]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectIdsAsync(codec, cts.Token)
        );
    }

    // ── The shared post-flush residual-frame drain (ADR-0055 §Context) ───────

    /// <summary>
    /// The drain pulls every residual frame the codec still holds after
    /// <c>avcodec_flush_buffers</c>, discarding each, and stops on the first negative
    /// (empty-buffer) code. Both decoders' <c>Flush</c> bodies now share this one loop
    /// instead of carrying near-identical copies that cited each other.
    /// </summary>
    [Theory]
    [InlineData(0)] // already empty: stops immediately, discards nothing
    [InlineData(1)]
    [InlineData(5)]
    public void DrainResidualFrames_PullsEveryResidualFrame_ThenStops(int residual)
    {
        // A scripted codec output queue: `residual` produced frames (code 0), then a
        // negative code forever — no FFmpeg, exactly the AudioDecoder.Flush /
        // VideoDecoder.Flush contract over avcodec_receive_frame / av_frame_unref.
        int remaining = residual;
        int discarded = 0;

        int drained = DecodeDriver.DrainResidualFrames(
            receive: () => remaining-- > 0 ? 0 : FFAvUtil.AvErrorEagain,
            discard: () => discarded++
        );

        Assert.Equal(residual, drained); // every residual frame was pulled
        Assert.Equal(residual, discarded); // and each one discarded (unref'd)
        Assert.True(remaining < 0); // the loop consumed the terminating negative code
    }

    [Fact]
    public void DrainResidualFrames_StopsOnEof_NotOnlyEagain()
    {
        // The terminator may be EOF rather than EAGAIN; the predicate is "any negative".
        int remaining = 2;
        int discarded = 0;

        int drained = DecodeDriver.DrainResidualFrames(
            receive: () => remaining-- > 0 ? 0 : FFAvUtil.AvErrorEof,
            discard: () => discarded++
        );

        Assert.Equal(2, drained);
        Assert.Equal(2, discarded);
    }

    [Fact]
    public void DrainResidualFrames_DiscardsExactlyOncePerReceivedFrame_InOrder()
    {
        // Asserts the receive/discard interleaving: each produced frame is discarded
        // before the next receive — the post-flush ordering both Flush bodies rely on.
        var events = new List<string>();
        int remaining = 3;

        DecodeDriver.DrainResidualFrames(
            receive: () =>
            {
                if (remaining-- > 0)
                {
                    events.Add("receive");
                    return 0;
                }

                events.Add("empty");
                return FFAvUtil.AvErrorEagain;
            },
            discard: () => events.Add("discard")
        );

        Assert.Equal(
            new[] { "receive", "discard", "receive", "discard", "receive", "discard", "empty" },
            events
        );
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<int>> CollectIdsAsync(
        IDecodeCodec<FakeFrame> codec,
        CancellationToken cancellationToken = default
    )
    {
        var ids = new List<int>();
        await foreach (var frame in DecodeDriver.RunAsync(codec, cancellationToken))
        {
            ids.Add(frame.Id);
        }

        return ids;
    }

    /// <summary>A managed "frame" — just an id so tests can assert ordering.</summary>
    private sealed record FakeFrame(int Id);

    /// <summary>
    /// One scripted input: <paramref name="Stall"/> send-EAGAINs before the decoder
    /// accepts it, then it produces <paramref name="Frames"/>.
    /// </summary>
    private sealed record FakeInput(int Stall, int[] Frames);

    /// <summary>
    /// An in-memory codec that speaks the <see cref="CodecReturn"/> vocabulary directly —
    /// no FFmpeg. It buffers a per-packet frame list on acceptance, releases buffered
    /// frames on receive, optionally stalls a send (EAGAIN) to exercise the re-send
    /// branch, optionally faults a receive, and optionally holds frames back until the
    /// end-of-stream flush.
    /// </summary>
    private sealed class FakeCodec : IDecodeCodec<FakeFrame>
    {
        private readonly Queue<FakeInput> _inputs;
        private readonly Queue<int> _bufferedAtEof;
        private readonly int _faultOnReceiveNumber; // 0 = never
        private readonly Queue<int> _pending = new();

        private FakeInput? _current;
        private int _stallRemaining;
        private bool _flushing;
        private bool _flushAccepted;
        private int _lastBuilt = -1;

        public int BeginCalls { get; private set; }
        public int SendCalls { get; private set; }
        public int ReceiveCalls { get; private set; }

        public FakeCodec(
            IEnumerable<FakeInput> inputs,
            int[]? bufferedAtEof = null,
            int faultOnReceiveNumber = 0
        )
        {
            _inputs = new Queue<FakeInput>(inputs);
            _bufferedAtEof = new Queue<int>(bufferedAtEof ?? []);
            _faultOnReceiveNumber = faultOnReceiveNumber;
        }

        public ValueTask<bool> TryBeginNextInputAsync(CancellationToken cancellationToken)
        {
            BeginCalls++;
            if (_inputs.Count > 0)
            {
                _current = _inputs.Dequeue();
                _stallRemaining = _current.Stall;
                return ValueTask.FromResult(true);
            }

            _current = null;
            _flushing = true;
            return ValueTask.FromResult(false);
        }

        public CodecReturn SendCurrentInput()
        {
            SendCalls++;

            if (_flushing)
            {
                _flushAccepted = true; // flush accepted; receives drain then EOF
                return CodecReturn.Ok;
            }

            if (_stallRemaining > 0)
            {
                // Decoder full: it will not accept this packet until drained. The driver
                // must drain, then re-send THIS packet (DrainingThenRetry).
                _stallRemaining--;
                return CodecReturn.Again;
            }

            foreach (var id in _current!.Frames)
            {
                _pending.Enqueue(id);
            }

            return CodecReturn.Ok;
        }

        public CodecReturn ReceiveFrame()
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

            return _flushAccepted ? CodecReturn.EndOfStream : CodecReturn.Again;
        }

        public FakeFrame? BuildFrame() => new(_lastBuilt);
    }
}
