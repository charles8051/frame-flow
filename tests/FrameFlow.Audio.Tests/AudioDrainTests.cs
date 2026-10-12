// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Audio.OpenAL;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// The decisions <see cref="OpenAlAudioSink.DrainAsync"/> turns on, with no device, no thread
/// and no clock (#609).
/// </summary>
public sealed class AudioDrainTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private static DrainObservation Seen(
        DrainSourceState state,
        int queued,
        long played,
        TimeSpan now,
        bool staged = false
    ) => new(staged, queued, state, played, now);

    private static AudioDrain Begin() => AudioDrain.Begin(Limit, TimeSpan.Zero);

    // ── When it is over ─────────────────────────────────────────────────────

    [Fact]
    public void NothingStagedAndNothingQueued_IsComplete_InEveryState()
    {
        foreach (var state in Enum.GetValues<DrainSourceState>())
        {
            var outcome = Begin().Observe(Seen(state, queued: 0, played: 0, TimeSpan.Zero));

            Assert.True(outcome.Step == DrainStep.Complete, $"{state}: {outcome.Step}");
        }
    }

    [Fact]
    public void AStoppedSourceWithNothingStaged_IsComplete_WhateverItStillHasQueued()
    {
        // A playing source stops when it finishes the last buffer in its queue, and the queue
        // stays populated until the sink unqueues it (OpenAL 1.1 §4.3.5).
        var outcome = Begin().Observe(Seen(DrainSourceState.Stopped, queued: 16, played: 9000, TimeSpan.Zero));

        Assert.Equal(DrainStep.Complete, outcome.Step);
    }

    [Fact]
    public void AStoppedSourceWithSamplesStillStaged_IsNotComplete()
    {
        // The remainder has not reached the device yet; the shell's next flush re-primes the
        // source and uploads it.
        var outcome = Begin().Observe(
            Seen(DrainSourceState.Stopped, queued: 16, played: 9000, TimeSpan.Zero, staged: true)
        );

        Assert.Equal(DrainStep.Wait, outcome.Step);
    }

    [Fact]
    public void APlayingSourceWithAudioQueued_Waits()
    {
        var outcome = Begin().Observe(Seen(DrainSourceState.Playing, queued: 6, played: 100, TimeSpan.Zero));

        Assert.Equal(DrainStep.Wait, outcome.Step);
    }

    [Fact]
    public void SamplesStillStaged_WithNothingQueued_Waits()
    {
        // A stream shorter than one coalesce block: the shell uploads it on the next pass.
        var outcome = Begin().Observe(
            Seen(DrainSourceState.Initial, queued: 0, played: 0, TimeSpan.Zero, staged: true)
        );

        Assert.Equal(DrainStep.Wait, outcome.Step);
    }

    // ── A source that never started ─────────────────────────────────────────

    [Fact]
    public void ASourceThatNeverStarted_IsStarted()
    {
        var outcome = Begin().Observe(Seen(DrainSourceState.Initial, queued: 2, played: 0, TimeSpan.Zero));

        Assert.Equal(DrainStep.StartSource, outcome.Step);
    }

    [Fact]
    public void ASourceThatWillNotStart_StillRunsOutOfTime()
    {
        // Starting is not progress. A source that stays in AL_INITIAL however often it is told
        // to play must not keep the drain alive.
        var drain = Begin();
        foreach (var at in new[] { 0.5, 1.0, 1.5 })
        {
            var outcome = drain.Observe(
                Seen(DrainSourceState.Initial, queued: 2, played: 0, TimeSpan.FromSeconds(at))
            );
            Assert.Equal(DrainStep.StartSource, outcome.Step);
            drain = outcome.Next;
        }

        var last = drain.Observe(Seen(DrainSourceState.Initial, queued: 2, played: 0, Limit));
        Assert.Equal(DrainStep.Stalled, last.Step);
    }

    // ── Bounded by progress ─────────────────────────────────────────────────

    [Fact]
    public void ADeviceThatStopsPlaying_IsGivenUpOnAtTheLimit()
    {
        var drain = Begin()
            .Observe(Seen(DrainSourceState.Playing, queued: 6, played: 500, TimeSpan.Zero))
            .Next;

        var before = drain.Observe(Seen(DrainSourceState.Playing, queued: 6, played: 500, Limit - TimeSpan.FromTicks(1)));
        Assert.Equal(DrainStep.Wait, before.Step);

        var at = drain.Observe(Seen(DrainSourceState.Playing, queued: 6, played: 500, Limit));
        Assert.Equal(DrainStep.Stalled, at.Step);
    }

    [Fact]
    public void ALongQueueThatKeepsPlaying_IsNeverCutShort()
    {
        // The limit runs from the last progress, not from the start: a device that keeps
        // moving is waited for however long its queue is.
        var drain = Begin();
        for (int second = 0; second <= 30; second++)
        {
            var outcome = drain.Observe(
                Seen(DrainSourceState.Playing, queued: 16, played: second * 48_000L, TimeSpan.FromSeconds(second))
            );
            Assert.Equal(DrainStep.Wait, outcome.Step);
            drain = outcome.Next;
        }
    }

    [Fact]
    public void Progress_RestartsTheLimit()
    {
        var drain = Begin()
            .Observe(Seen(DrainSourceState.Playing, queued: 6, played: 0, TimeSpan.Zero))
            .Next;
        drain = drain
            .Observe(Seen(DrainSourceState.Playing, queued: 6, played: 960, TimeSpan.FromSeconds(1.5)))
            .Next;

        // 2 s after the start but 0.5 s after the last progress.
        var outcome = drain.Observe(Seen(DrainSourceState.Playing, queued: 6, played: 960, Limit));

        Assert.Equal(DrainStep.Wait, outcome.Step);
    }

    [Fact]
    public void APausedSource_NeverRunsOutOfTime_AndTheLimitRunsFromTheResume()
    {
        var drain = Begin()
            .Observe(Seen(DrainSourceState.Playing, queued: 6, played: 500, TimeSpan.Zero))
            .Next;

        // Paused for five times the limit: the caller holding playback, not the device failing.
        foreach (var at in new[] { 1.0, 4.0, 10.0 })
        {
            var outcome = drain.Observe(
                Seen(DrainSourceState.Paused, queued: 6, played: 500, TimeSpan.FromSeconds(at))
            );
            Assert.Equal(DrainStep.Wait, outcome.Step);
            drain = outcome.Next;
        }

        // Resumed at 10 s with the device not moving: it has the whole limit from there.
        var justAfter = drain.Observe(
            Seen(DrainSourceState.Playing, queued: 6, played: 500, TimeSpan.FromSeconds(10) + Limit - TimeSpan.FromTicks(1))
        );
        Assert.Equal(DrainStep.Wait, justAfter.Step);

        var atLimit = drain.Observe(
            Seen(DrainSourceState.Playing, queued: 6, played: 500, TimeSpan.FromSeconds(10) + Limit)
        );
        Assert.Equal(DrainStep.Stalled, atLimit.Step);
    }

    [Fact]
    public void Begin_RefusesALimitThatIsNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioDrain.Begin(TimeSpan.Zero, TimeSpan.Zero));
    }
}
