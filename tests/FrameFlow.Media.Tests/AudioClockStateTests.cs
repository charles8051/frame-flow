using FrameFlow.Media;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Device-free tests for <see cref="AudioClockState"/>, the pure core of the
/// OpenAL audio master clock (§5.2). These replace the gap left by the
/// <c>[RequiresAudioDeviceFact]</c> clock tests in <see cref="OpenAlAudioSinkTests"/>,
/// which "pass trivially" with no audio device because the clock math was only
/// reachable through the live OpenAL handle. The same arithmetic and origin
/// policy is now a value transform that runs in CI with no device — the
/// master-clock test surface §5.2 is about.
/// </summary>
/// <remarks>
/// The arithmetic mirrored here is the sink's <c>GetPlaybackTimeUnderLock</c>:
/// <c>BaseSourceTime + (ProcessedSamplesPerChannel + deviceSampleOffset) / sampleRate</c>.
/// 48 kHz stereo is the production format (see the sink's CoalesceTarget note), so
/// most cases use 48000.
/// </remarks>
public sealed class AudioClockStateTests
{
    private const int Rate = 48000;

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void Initial_OriginIsZeroUnseatedNoSamplesNoSeed()
    {
        var s = AudioClockState.Initial;

        Assert.Equal(TimeSpan.Zero, s.BaseSourceTime);
        Assert.False(s.OriginSeated);
        Assert.Equal(0, s.ProcessedSamplesPerChannel);
        Assert.Null(s.PendingSeekBaseline);
    }

    [Fact]
    public void Initial_PositionBeforeAnyRateIsZero()
    {
        // Mirrors the sink's `_sampleRate <= 0 → return _baseSourceTime` guard:
        // before any buffer is presented the published position is the (zero) origin,
        // regardless of the device cursor argument.
        var s = AudioClockState.Initial;

        Assert.Equal(TimeSpan.Zero, s.Position(deviceSampleOffset: 0, sampleRate: 0));
        Assert.Equal(TimeSpan.Zero, s.Position(deviceSampleOffset: 9999, sampleRate: 0));
        Assert.Equal(TimeSpan.Zero, s.Position(deviceSampleOffset: 9999, sampleRate: -1));
    }

    // ── Position arithmetic (the clock equation) ───────────────────────────────

    [Fact]
    public void Position_FromDeviceOffsetOnly_OneSecondAtSampleRate()
    {
        // No processed buffers yet; the live device cursor sits at exactly one
        // second's worth of samples → position is 1.0s from a zero origin.
        var s = AudioClockState.Initial;

        Assert.Equal(TimeSpan.FromSeconds(1), s.Position(deviceSampleOffset: Rate, sampleRate: Rate));
    }

    [Fact]
    public void Position_AddsProcessedSamplesAndDeviceOffset()
    {
        // 2.0s already processed + 0.5s in flight on the device = 2.5s.
        var s = AudioClockState.Initial.WithProcessed(2 * Rate);

        Assert.Equal(
            TimeSpan.FromSeconds(2.5),
            s.Position(deviceSampleOffset: Rate / 2, sampleRate: Rate)
        );
    }

    [Fact]
    public void Position_IsOffsetFromBaseSourceTime()
    {
        // Origin seated at 60s (post-seek), 0.25s in flight → 60.25s. This is the
        // source-stream-PTS property the whole seek-clock contract rests on.
        var s = AudioClockState.Initial.SeekBaseline(TimeSpan.FromSeconds(60));

        Assert.Equal(
            TimeSpan.FromSeconds(60.25),
            s.Position(deviceSampleOffset: Rate / 4, sampleRate: Rate)
        );
    }

    [Fact]
    public void Position_MatchesInlineFormulaExactly()
    {
        // Pin bit-for-bit equality with the arithmetic the sink used to inline:
        // base + TimeSpan.FromSeconds((double)(processed + offset) / rate). A
        // non-round rate + odd counts catch any double-rounding divergence.
        var baseTime = TimeSpan.FromSeconds(12.3456789);
        long processed = 123_457;
        int offset = 9_871;
        int rate = 44_100;

        var s = AudioClockState.Initial.SeekBaseline(baseTime).WithProcessed(processed);

        var expected = baseTime + TimeSpan.FromSeconds((double)(processed + offset) / rate);
        Assert.Equal(expected, s.Position(offset, rate));
    }

    // ── WithProcessed (RecycleProcessedBuffers accumulation) ───────────────────

    [Fact]
    public void WithProcessed_Accumulates()
    {
        var s = AudioClockState
            .Initial.WithProcessed(100)
            .WithProcessed(250)
            .WithProcessed(50);

        Assert.Equal(400, s.ProcessedSamplesPerChannel);
    }

    [Fact]
    public void WithProcessed_DoesNotMoveOrigin()
    {
        var s = AudioClockState.Initial.SeekBaseline(TimeSpan.FromSeconds(10)).WithProcessed(Rate);

        Assert.Equal(TimeSpan.FromSeconds(10), s.BaseSourceTime);
        Assert.True(s.OriginSeated);
    }

    [Fact]
    public void Position_IsMonotonicAsProcessedAndOffsetClimb()
    {
        // Steady-state property: the published position is monotonic in the combined
        // (processed + deviceOffset) sample count — the IClockSource monotonicity
        // contract the pacer relies on. Models the real device cycle: the cursor
        // climbs 0..buffer within a buffer, then that buffer recycles (processed
        // jumps by the buffer size) and the cursor resets — the *total* never drops.
        const int bufferSamples = 4800; // ~100ms at 48kHz, the sink's CoalesceTarget
        long processed = 0;
        var prev = AudioClockState.Initial.Position(0, Rate);

        for (int buffer = 0; buffer < 100; buffer++)
        {
            var state = AudioClockState.Initial.WithProcessed(processed);
            for (int offset = 0; offset <= bufferSamples; offset += bufferSamples / 4)
            {
                var now = state.Position(offset, Rate);
                Assert.True(
                    now >= prev,
                    $"position regressed: {now} < {prev} (processed={processed}, offset={offset})"
                );
                prev = now;
            }
            // Buffer fully played → recycled into the processed count; cursor rolls to 0.
            processed += bufferSamples;
        }
    }

    // ── First-buffer origin discovery (CaptureFirstBufferPts) ──────────────────

    [Fact]
    public void CaptureFirstBufferPts_WhenUnseated_SeatsOriginFromPts()
    {
        // The default initial-play path: origin discovered from the first
        // post-activation buffer's PTS.
        var s = AudioClockState.Initial.CaptureFirstBufferPts(TimeSpan.FromSeconds(30));

        Assert.True(s.OriginSeated);
        Assert.Equal(TimeSpan.FromSeconds(30), s.BaseSourceTime);
        // And it now reads as 30s + in-flight samples.
        Assert.Equal(
            TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(0.5),
            s.Position(Rate / 2, Rate)
        );
    }

    [Fact]
    public void CaptureFirstBufferPts_OnceSeated_IsNoOp()
    {
        // Only the very first buffer can establish the origin; a later buffer's PTS
        // must not move it (the sink's `if (!_baseSourceTimeCaptured)` guard).
        var seated = AudioClockState.Initial.CaptureFirstBufferPts(TimeSpan.FromSeconds(30));
        var afterSecond = seated.CaptureFirstBufferPts(TimeSpan.FromSeconds(31));

        Assert.Equal(TimeSpan.FromSeconds(30), afterSecond.BaseSourceTime);
        Assert.Equal(seated, afterSecond);
    }

    [Fact]
    public void CaptureFirstBufferPts_AfterSeekSeat_IsNoOp()
    {
        // Regression guard for the frozen-video root cause: after a seek seats the
        // origin to the target, the first post-seek buffer's (possibly stale /
        // keyframe-rounded) PTS must be ignored. SeatOnActivate consumes the seed and
        // marks the origin seated, so CaptureFirstBufferPts is a no-op.
        var afterActivate = AudioClockState
            .Initial.SeekBaseline(TimeSpan.FromSeconds(60))
            .SeatOnActivate();

        var afterBuffer = afterActivate.CaptureFirstBufferPts(TimeSpan.FromSeconds(58)); // stale PTS

        Assert.Equal(TimeSpan.FromSeconds(60), afterBuffer.BaseSourceTime);
        Assert.True(afterBuffer.OriginSeated);
    }

    // ── Activation seating (SeatOnActivate / origin policy) ────────────────────

    [Fact]
    public void SeatOnActivate_WithNoSeed_ResetsToUnseatedZeroOrigin()
    {
        // No pending seek seed → origin resets to zero, unseated (to be discovered
        // from the first buffer), processed zeroed. This is the fresh / loop-restart
        // activation path.
        var dirty = AudioClockState
            .Initial.CaptureFirstBufferPts(TimeSpan.FromSeconds(5))
            .WithProcessed(Rate);

        var seated = dirty.SeatOnActivate();

        Assert.Equal(TimeSpan.Zero, seated.BaseSourceTime);
        Assert.False(seated.OriginSeated);
        Assert.Equal(0, seated.ProcessedSamplesPerChannel);
        Assert.Null(seated.PendingSeekBaseline);
    }

    [Fact]
    public void SeatOnActivate_WithSeed_SeatsToSeekTargetAndConsumesSeed()
    {
        // Pending seek seed → origin = seek target, marked seated, seed consumed,
        // processed zeroed. The deactivate/reactivate seek path.
        var afterSeek = AudioClockState.Initial.SeekBaseline(TimeSpan.FromSeconds(42));
        Assert.Equal(TimeSpan.FromSeconds(42), afterSeek.PendingSeekBaseline);

        var seated = afterSeek.SeatOnActivate();

        Assert.Equal(TimeSpan.FromSeconds(42), seated.BaseSourceTime);
        Assert.True(seated.OriginSeated);
        Assert.Equal(0, seated.ProcessedSamplesPerChannel);
        Assert.Null(seated.PendingSeekBaseline); // consumed
    }

    [Fact]
    public void SeatOnActivate_ZeroesProcessedSamples()
    {
        // Every activation path starts the device-sample accounting fresh (the sink
        // resets _processedSamplesPerChannel = 0 on activate).
        var s = AudioClockState.Initial.WithProcessed(5 * Rate).SeatOnActivate();
        Assert.Equal(0, s.ProcessedSamplesPerChannel);
    }

    // ── SeekBaseline (ISeekableClock reseat) ───────────────────────────────────

    [Fact]
    public void SeekBaseline_SeatsOriginRetainsSeedAndZeroesProcessed()
    {
        // SeekBaseline does all four of the sink's inline assignments at once: origin
        // = target, seated, seed retained for the next activation, processed zeroed.
        var s = AudioClockState.Initial.WithProcessed(Rate).SeekBaseline(TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(60), s.BaseSourceTime);
        Assert.True(s.OriginSeated);
        Assert.Equal(TimeSpan.FromSeconds(60), s.PendingSeekBaseline);
        Assert.Equal(0, s.ProcessedSamplesPerChannel);
    }

    [Fact]
    public void SeekBaseline_PublishesTargetImmediately()
    {
        // The immediate-seat half of the contract (a seek that does NOT recycle the
        // sink): with no rate yet, the published position is exactly the seek target —
        // this is what SeekBaseline_SeatsClockOriginToSeekTarget asserts at the sink
        // level, now provable device-free.
        var s = AudioClockState.Initial.SeekBaseline(TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(60), s.Position(deviceSampleOffset: 0, sampleRate: 0));
        // And once samples flow it climbs from the target, never from zero.
        Assert.True(s.Position(deviceSampleOffset: Rate, sampleRate: Rate) >= TimeSpan.FromSeconds(60));
    }

    // ── Deactivation (OnDeactivate) ────────────────────────────────────────────

    [Fact]
    public void OnDeactivate_ReturnsToInitial()
    {
        // Deactivation drops the origin, the processed count, AND any unconsumed seek
        // seed — so a never-activated seed can't leak into a later, unrelated
        // activation (the sink's DeactivateAsync resets all four fields).
        var dirty = AudioClockState
            .Initial.SeekBaseline(TimeSpan.FromSeconds(60))
            .WithProcessed(3 * Rate);

        var deactivated = dirty.OnDeactivate();

        Assert.Equal(AudioClockState.Initial, deactivated);
        Assert.Null(deactivated.PendingSeekBaseline);
        Assert.False(deactivated.OriginSeated);
        Assert.Equal(0, deactivated.ProcessedSamplesPerChannel);
    }

    // ── End-to-end transition sequences (mirror the real lifecycle) ────────────

    [Fact]
    public void Lifecycle_SeekDeactivateReactivate_OriginTracksSeekTarget()
    {
        // The exact sequence the seek path drives through the sink:
        //   SeekBaseline(60s)  [seed retained]
        //   OnDeactivate()      [DeactivateAsync — note: drops the seed]
        //   ...but the real seek path seeds AFTER deactivate, so model that order:
        //
        // Real order in SubstrateSession.SeekAsync: DeactivateAsync → SeekBaseline →
        // ActivateAsync. So: deactivate (clears), then SeekBaseline seeds, then
        // SeatOnActivate consumes the seed → origin = 60s.
        var afterDeactivate = AudioClockState.Initial.WithProcessed(Rate).OnDeactivate();
        var afterSeed = afterDeactivate.SeekBaseline(TimeSpan.FromSeconds(60));
        var afterActivate = afterSeed.SeatOnActivate();

        Assert.Equal(TimeSpan.FromSeconds(60), afterActivate.BaseSourceTime);
        Assert.True(afterActivate.OriginSeated);
        // First post-seek buffer at a stale PTS does NOT move it.
        var afterBuffer = afterActivate.CaptureFirstBufferPts(TimeSpan.FromSeconds(57));
        Assert.Equal(TimeSpan.FromSeconds(60), afterBuffer.BaseSourceTime);
    }

    [Fact]
    public void Lifecycle_FreshPlay_DiscoversOriginThenAdvances()
    {
        // Fresh load (no seek): activate (unseated) → first buffer at PTS=0 seats the
        // origin → recycle buffers advance processed → position climbs.
        var activated = AudioClockState.Initial.SeatOnActivate(); // unseated, zero
        Assert.False(activated.OriginSeated);

        var seated = activated.CaptureFirstBufferPts(TimeSpan.Zero);
        Assert.True(seated.OriginSeated);
        Assert.Equal(TimeSpan.Zero, seated.BaseSourceTime);

        // Recycle 1s of buffers, device 0.1s in flight → 1.1s.
        var advanced = seated.WithProcessed(Rate);
        Assert.Equal(
            TimeSpan.FromSeconds(1.1),
            advanced.Position(deviceSampleOffset: Rate / 10, sampleRate: Rate)
        );
    }

    [Fact]
    public void Lifecycle_LoopRestart_ReseatsToEpochZero()
    {
        // RepeatMode.One loop boundary: a seek-to-zero. After the loop's
        // SeekBaseline(0) + reactivate, the origin is epoch zero — not re-discovered
        // from the first post-loop buffer's (climbing) PTS, which is the gapless-loop
        // drift the reseat exists to prevent.
        var beforeLoop = AudioClockState
            .Initial.CaptureFirstBufferPts(TimeSpan.Zero)
            .WithProcessed(5 * Rate); // ~5s into the clip

        var afterLoopSeek = beforeLoop.OnDeactivate().SeekBaseline(TimeSpan.Zero).SeatOnActivate();

        Assert.Equal(TimeSpan.Zero, afterLoopSeek.BaseSourceTime);
        Assert.True(afterLoopSeek.OriginSeated);
        Assert.Equal(0, afterLoopSeek.ProcessedSamplesPerChannel);
        // A late first-post-loop buffer carrying the clip's tail PTS is ignored.
        var afterBuffer = afterLoopSeek.CaptureFirstBufferPts(TimeSpan.FromSeconds(4.9));
        Assert.Equal(TimeSpan.Zero, afterBuffer.BaseSourceTime);
    }

    // ── Value semantics ────────────────────────────────────────────────────────

    [Fact]
    public void Transforms_DoNotMutateReceiver()
    {
        // It's a readonly record struct, but pin the value semantics explicitly: every
        // transform returns a new value and leaves the receiver untouched.
        var original = AudioClockState.Initial.SeekBaseline(TimeSpan.FromSeconds(10));

        _ = original.WithProcessed(Rate);
        _ = original.SeatOnActivate();
        _ = original.CaptureFirstBufferPts(TimeSpan.FromSeconds(99));
        _ = original.OnDeactivate();
        _ = original.Position(Rate, Rate);

        Assert.Equal(TimeSpan.FromSeconds(10), original.BaseSourceTime);
        Assert.Equal(TimeSpan.FromSeconds(10), original.PendingSeekBaseline);
        Assert.Equal(0, original.ProcessedSamplesPerChannel);
        Assert.True(original.OriginSeated);
    }
}
