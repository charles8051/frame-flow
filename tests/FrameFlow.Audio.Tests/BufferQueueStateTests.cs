using FrameFlow.Audio.OpenAL;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Device-free tests for <see cref="BufferQueueState"/>, the pure core of the OpenAL
/// sink's buffer-queue control logic (§5.2): the coalesce gate, the underrun decision,
/// the upload/backpressure plan, and the pre-buffer playback-start gate. These give the
/// buffer-queue path real in-CI coverage — today the only buffer-queue test is the
/// device-gated end-to-end backpressure test that "passes trivially" with no audio
/// device.
/// </summary>
public sealed class BufferQueueStateTests
{
    // The sink's production constants (see OpenAlAudioSink): ~100ms coalesce target at
    // 48kHz stereo, 4-buffer pre-roll.
    private const int CoalesceTarget = 4800;
    private const int PreBuffer = 4;

    private static BufferQueueState New() => BufferQueueState.Create(CoalesceTarget, PreBuffer);

    // ── Construction ────────────────────────────────────────────────────────────

    [Fact]
    public void Create_StartsEmptyAndNotStarted()
    {
        var s = New();
        Assert.Equal(0, s.StagingCount);
        Assert.False(s.SourceStarted);
        Assert.False(s.ShouldFlush);
    }

    [Theory]
    [InlineData(0, PreBuffer)]
    [InlineData(CoalesceTarget, 0)]
    [InlineData(-1, PreBuffer)]
    public void Create_RejectsNonPositiveConfig(int coalesce, int preBuffer)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BufferQueueState.Create(coalesce, preBuffer));
    }

    // ── Coalesce gate (AppendStaging / ShouldFlush) ──────────────────────────────

    [Fact]
    public void AppendStaging_Accumulates()
    {
        var s = New().AppendStaging(1000).AppendStaging(2000).AppendStaging(500);
        Assert.Equal(3500, s.StagingCount);
    }

    [Fact]
    public void ShouldFlush_FalseBelowTarget_TrueAtOrAboveTarget()
    {
        Assert.False(New().AppendStaging(CoalesceTarget - 1).ShouldFlush);
        Assert.True(New().AppendStaging(CoalesceTarget).ShouldFlush); // exactly at threshold
        Assert.True(New().AppendStaging(CoalesceTarget + 1).ShouldFlush);
    }

    [Fact]
    public void AppendStaging_NegativeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => New().AppendStaging(-1));
    }

    [Fact]
    public void ClearStaging_ZeroesFillKeepsSourceStarted()
    {
        // After an upload the staging drains but the source-started latch must persist.
        var started = New().AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).Next;
        Assert.True(started.SourceStarted);

        var cleared = started.ClearStaging();
        Assert.Equal(0, cleared.StagingCount);
        Assert.True(cleared.SourceStarted);
    }

    // ── Upload / backpressure plan (PlanUpload) ──────────────────────────────────

    [Fact]
    public void PlanUpload_NothingWhenStagingEmpty()
    {
        var s = New(); // staging == 0
        Assert.Equal(
            UploadDecision.Nothing,
            s.PlanUpload(sinkActive: true, freeBufferCount: 4, sourceState: AlSourceState.PlayingOrInitial)
        );
    }

    [Fact]
    public void PlanUpload_NothingWhenInactive()
    {
        var s = New().AppendStaging(CoalesceTarget);
        Assert.Equal(
            UploadDecision.Nothing,
            s.PlanUpload(sinkActive: false, freeBufferCount: 4, sourceState: AlSourceState.PlayingOrInitial)
        );
    }

    [Fact]
    public void PlanUpload_UploadWhenFreeBufferAvailable()
    {
        var s = New().AppendStaging(CoalesceTarget);
        Assert.Equal(
            UploadDecision.Upload,
            s.PlanUpload(sinkActive: true, freeBufferCount: 1, sourceState: AlSourceState.PlayingOrInitial)
        );
    }

    [Fact]
    public void PlanUpload_NeedBufferWhenPoolEmptyAndDraining()
    {
        // No free buffer but the source is playing → it will drain one; wait for it.
        var s = New().AppendStaging(CoalesceTarget);
        Assert.Equal(
            UploadDecision.NeedBuffer,
            s.PlanUpload(sinkActive: true, freeBufferCount: 0, sourceState: AlSourceState.PlayingOrInitial)
        );
    }

    [Theory]
    [InlineData(AlSourceState.Paused)]
    [InlineData(AlSourceState.Stopped)]
    public void PlanUpload_AbortWhenPoolEmptyAndSourceWontDrain(AlSourceState state)
    {
        // No free buffer AND the source is paused/stopped → no buffer will ever return;
        // abandon the flush so the pipeline worker reaches the barrier promptly.
        var s = New().AppendStaging(CoalesceTarget);
        Assert.Equal(
            UploadDecision.Abort,
            s.PlanUpload(sinkActive: true, freeBufferCount: 0, sourceState: state)
        );
    }

    // ── Underrun decision (ObserveUnderrun) ──────────────────────────────────────

    [Fact]
    public void ObserveUnderrun_DetectsWhenStartedSourceStopped_FirstPass()
    {
        // The starved-source case: playback was running, the device drained to Stopped.
        var started = New().AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).Next;
        Assert.True(started.SourceStarted);

        var outcome = started.ObserveUnderrun(firstPass: true, AlSourceState.Stopped);

        Assert.True(outcome.Underran);
        Assert.False(outcome.Next.SourceStarted); // latch cleared so the gate re-arms
    }

    [Fact]
    public void ObserveUnderrun_IgnoredOnLaterPasses()
    {
        // The underrun check runs once per flush (firstPass only) — a later retry pass
        // must not re-count the same stall.
        var started = New().AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).Next;

        var outcome = started.ObserveUnderrun(firstPass: false, AlSourceState.Stopped);

        Assert.False(outcome.Underran);
        Assert.True(outcome.Next.SourceStarted); // unchanged
    }

    [Fact]
    public void ObserveUnderrun_IgnoredWhenNeverStarted()
    {
        // Before playback starts, a Stopped source is the normal Initial state, not an
        // underrun.
        var notStarted = New().AppendStaging(CoalesceTarget);
        Assert.False(notStarted.SourceStarted);

        var outcome = notStarted.ObserveUnderrun(firstPass: true, AlSourceState.Stopped);

        Assert.False(outcome.Underran);
        Assert.False(outcome.Next.SourceStarted);
    }

    [Theory]
    [InlineData(AlSourceState.PlayingOrInitial)]
    [InlineData(AlSourceState.Paused)]
    public void ObserveUnderrun_NotFlaggedWhenSourceNotStopped(AlSourceState state)
    {
        // A still-playing or merely-paused source is not starved.
        var started = New().AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).Next;

        var outcome = started.ObserveUnderrun(firstPass: true, state);

        Assert.False(outcome.Underran);
        Assert.True(outcome.Next.SourceStarted);
    }

    // ── Pre-buffer playback-start gate (ObserveQueueDepth) ───────────────────────

    [Fact]
    public void ObserveQueueDepth_DoesNotStartBelowPreBuffer()
    {
        var s = New().AppendStaging(CoalesceTarget);
        for (int queued = 1; queued < PreBuffer; queued++)
        {
            var outcome = s.ObserveQueueDepth(queued);
            Assert.False(outcome.ShouldStartPlayback, $"should not start at queued={queued}");
            Assert.False(outcome.Next.SourceStarted);
        }
    }

    [Fact]
    public void ObserveQueueDepth_StartsAtPreBufferThreshold()
    {
        var s = New().AppendStaging(CoalesceTarget);
        var outcome = s.ObserveQueueDepth(PreBuffer);

        Assert.True(outcome.ShouldStartPlayback);
        Assert.True(outcome.Next.SourceStarted);
    }

    [Fact]
    public void ObserveQueueDepth_StartsOnlyOnce()
    {
        // The gate fires exactly once; once started, further depth observations must not
        // re-request SourcePlay (the !_sourceStarted guard).
        var started = New().AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).Next;
        Assert.True(started.SourceStarted);

        var again = started.ObserveQueueDepth(PreBuffer + 10);
        Assert.False(again.ShouldStartPlayback);
        Assert.True(again.Next.SourceStarted);
    }

    [Fact]
    public void ObserveQueueDepth_ReArmsAfterUnderrunStop()
    {
        // After an underrun clears the latch, the gate must re-arm and fire again once
        // enough buffers are re-queued (the loop-restart-after-starve path).
        var started = New().AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).Next;
        var afterUnderrun = started.ObserveUnderrun(firstPass: true, AlSourceState.Stopped).Next;
        Assert.False(afterUnderrun.SourceStarted);

        var restarted = afterUnderrun.ObserveQueueDepth(PreBuffer);
        Assert.True(restarted.ShouldStartPlayback);
        Assert.True(restarted.Next.SourceStarted);
    }

    // ── Latch clears (MarkSourceStopped) ─────────────────────────────────────────

    [Fact]
    public void MarkSourceStopped_ClearsLatchKeepsStaging()
    {
        var s = New()
            .AppendStaging(1234)
            .ObserveQueueDepth(PreBuffer)
            .Next; // started, but with leftover staging
        Assert.True(s.SourceStarted);

        var stopped = s.MarkSourceStopped();
        Assert.False(stopped.SourceStarted);
        Assert.Equal(1234, stopped.StagingCount); // staging untouched
    }

    // ── Activation reset (ResetForActivation) ────────────────────────────────────

    [Fact]
    public void ResetForActivation_ClearsStagingAndLatchPreservesConfig()
    {
        var dirty = New().AppendStaging(CoalesceTarget * 2).ObserveQueueDepth(PreBuffer).Next;
        Assert.True(dirty.SourceStarted);

        var reset = dirty.ResetForActivation();
        Assert.Equal(0, reset.StagingCount);
        Assert.False(reset.SourceStarted);
        // Config preserved: the coalesce + pre-buffer thresholds still apply.
        Assert.False(reset.AppendStaging(CoalesceTarget - 1).ShouldFlush);
        Assert.True(reset.AppendStaging(CoalesceTarget).ShouldFlush);
        Assert.True(
            reset.AppendStaging(CoalesceTarget).ObserveQueueDepth(PreBuffer).ShouldStartPlayback
        );
    }

    // ── End-to-end flush sequence (mirrors TryFlushStagingBufferOnce) ────────────

    [Fact]
    public void FlushSequence_FillUploadStartDrain_MatchesSinkLoop()
    {
        // Walk the exact decision sequence the sink's flush loop produces over several
        // coalesce-sized blocks, with no device — proving the queue control logic is
        // correct end-to-end.
        var s = New();

        // Block 1: fill to target → flush gate opens.
        s = s.AppendStaging(CoalesceTarget);
        Assert.True(s.ShouldFlush);

        // Upload attempt with a free buffer → Upload, then drain staging.
        Assert.Equal(
            UploadDecision.Upload,
            s.PlanUpload(sinkActive: true, freeBufferCount: 2, sourceState: AlSourceState.PlayingOrInitial)
        );
        s = s.ClearStaging();
        Assert.Equal(0, s.StagingCount);

        // Queue depth climbs but is still below pre-roll → no playback yet.
        for (int q = 1; q < PreBuffer; q++)
        {
            var step = s.ObserveQueueDepth(q);
            Assert.False(step.ShouldStartPlayback);
            s = step.Next;
        }

        // Crossing the pre-roll threshold → SourcePlay fires exactly once.
        var startStep = s.ObserveQueueDepth(PreBuffer);
        Assert.True(startStep.ShouldStartPlayback);
        s = startStep.Next;
        Assert.True(s.SourceStarted);

        // Later block while running, pool momentarily empty but source playing → wait.
        // (The shell counts the backpressure episode in its own lock-free tally; the
        // value's job is only the wait/upload/abort verdict, which holds staging intact.)
        s = s.AppendStaging(CoalesceTarget);
        Assert.Equal(
            UploadDecision.NeedBuffer,
            s.PlanUpload(sinkActive: true, freeBufferCount: 0, sourceState: AlSourceState.PlayingOrInitial)
        );
        Assert.True(s.SourceStarted);
        Assert.Equal(CoalesceTarget, s.StagingCount); // staging held for the retry
    }

    // ── Value semantics ──────────────────────────────────────────────────────────

    [Fact]
    public void Transforms_DoNotMutateReceiver()
    {
        var original = New().AppendStaging(1000).ObserveQueueDepth(PreBuffer).Next; // started, staging 1000

        _ = original.AppendStaging(500);
        _ = original.ClearStaging();
        _ = original.MarkSourceStopped();
        _ = original.ResetForActivation();
        _ = original.ObserveUnderrun(true, AlSourceState.Stopped);
        _ = original.ObserveQueueDepth(PreBuffer + 5);

        Assert.Equal(1000, original.StagingCount);
        Assert.True(original.SourceStarted);
    }
}
