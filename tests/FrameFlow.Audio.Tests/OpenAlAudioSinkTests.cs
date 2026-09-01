using System.Buffers;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Graph;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Tests for <see cref="OpenAlAudioSink"/>.
/// Validates capabilities, lifecycle, and disposal. Tests that require a real OpenAL
/// audio device (WriteAsync, playback time) are skipped in CI environments.
/// </summary>
[Collection("OpenAL device")]
public sealed class OpenAlAudioSinkTests : IClassFixture<FfmpegBootstrapFixture>
{
    // ── Interface implementation ──────────────────────────────────────────────

    [Fact]
    public async Task Sink_ImplementsIAudioSink()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.IsAssignableFrom<IAudioSink>(sink);
    }

    [Fact]
    public async Task Sink_ImplementsIAsyncDisposable()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.IsAssignableFrom<IAsyncDisposable>(sink);
    }

    // ── Capabilities ──────────────────────────────────────────────────────────
    //
    // These were assertions against an AudioSinkCapabilities record that
    // OpenAlAudioSink constructed with constants, so they only ever proved
    // that the constructor arguments were what the constructor arguments
    // were. The capabilities they described are now interfaces, which the
    // compiler enforces and these tests observe.

    [Fact]
    public async Task ImplementsVolumeControl()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.IsAssignableFrom<IVolumeControl>(sink);
    }

    [Fact]
    public async Task ImplementsClockSource()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.IsAssignableFrom<IClockSource>(sink);
    }

    // ── Lifecycle (no device needed) ─────────────────────────────────────────

    [Fact]
    public async Task GetPlaybackTime_InitiallyZero()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.Equal(TimeSpan.Zero, sink.GetPlaybackTime());
    }

    // ── Seek baseline (ISeekableClock) — regression guard for the frozen-video bug ──

    [Fact]
    public async Task Sink_ImplementsISeekableClock()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.IsAssignableFrom<ISeekableClock>(sink);
    }

    [Fact]
    public async Task SeekBaseline_SeatsClockOriginToSeekTarget()
    {
        // Regression guard: a seek must reseat the master-clock origin to the seek
        // target, NOT leave it at zero (or re-discover it from the first post-seek
        // buffer's PTS). With the origin at zero while post-seek video frames carry
        // PTS≈60s, PaceUntil waits ~60 real seconds for the clock to "catch up" — the
        // frozen-video root cause. After SeekBaseline the clock reads the seek target.
        await using var sink = new OpenAlAudioSink();
        var target = TimeSpan.FromSeconds(60);

        ((ISeekableClock)sink).SeekBaseline(target);

        Assert.Equal(target, sink.GetPlaybackTime());
        Assert.Equal(target, ((IClockSource)sink).Latest);
    }

    [Fact]
    public async Task SeekBaseline_AfterDispose_IsNoOp()
    {
        var sink = new OpenAlAudioSink();
        await sink.DisposeAsync();

        // Must not throw on a disposed sink (idempotent/teardown-safe).
        var ex = Record.Exception(() => ((ISeekableClock)sink).SeekBaseline(TimeSpan.FromSeconds(5)));
        Assert.Null(ex);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var sink = new OpenAlAudioSink();
        var exception = await Record.ExceptionAsync(() => sink.DisposeAsync().AsTask());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var sink = new OpenAlAudioSink();
        await sink.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => sink.DisposeAsync().AsTask());
        Assert.Null(exception);
    }

    [RequiresAudioDeviceFact]
    public async Task DisposeAsync_AfterActivate_DoesNotThrow()
    {
        var sink = new OpenAlAudioSink();
        await sink.ActivateAsync();

        var exception = await Record.ExceptionAsync(() => sink.DisposeAsync().AsTask());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DeactivateAsync_BeforeActivate_DoesNotThrow()
    {
        await using var sink = new OpenAlAudioSink();
        var exception = await Record.ExceptionAsync(() => sink.DeactivateAsync().AsTask());
        Assert.Null(exception);
    }

    [Fact]
    public async Task PauseAsync_BeforeActivate_DoesNotThrow()
    {
        await using var sink = new OpenAlAudioSink();
        var exception = await Record.ExceptionAsync(() => sink.PauseAsync().AsTask());
        Assert.Null(exception);
    }

    [Fact]
    public async Task ResumeAsync_BeforeActivate_DoesNotThrow()
    {
        await using var sink = new OpenAlAudioSink();
        var exception = await Record.ExceptionAsync(() => sink.ResumeAsync().AsTask());
        Assert.Null(exception);
    }

    // ── Thread safety ────────────────────────────────────────────────────────
    //
    // The playback runtime drives this sink from three threads in production:
    // (1) the audio worker (WriteAsync), (2) the video worker which reads the
    // playback clock once per decoded frame for AV sync (GetPlaybackTime), and
    // (3) the session lifecycle thread (Pause / Resume / Deactivate / Dispose).
    // The internal state — _freeBuffers (Queue<uint>), _processedSamplesPerChannel,
    // _sourceStarted, the OpenAL source handle, and the staging buffer — is not
    // intrinsically thread-safe. The sink serialises all of this under
    // _stateLock; without it, the inference example's video worker hammered
    // GetPlaybackTime fast enough to corrupt the buffer-recycle accounting and
    // produced audible audio looping.

    [Fact]
    public async Task ConcurrentLifecycleAndPlaybackTime_DoNotThrow()
    {
        // Hammer all public methods from many threads at once. The sink is
        // unactivated (no real audio device available in CI), so each method
        // exercises the early-return path inside the lock. The point of the
        // test is to lock down the lock topology — if a future refactor drops
        // the lock from any of these methods, ThreadSanitizer-style races
        // wouldn't surface but the test still passes; if a refactor adds a
        // self-deadlocking reentrant lock acquisition it would hang here.
        await using var sink = new OpenAlAudioSink();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();

        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() => HammerPlaybackTime(sink, cts.Token)));
            tasks.Add(Task.Run(async () => await HammerLifecycle(sink, cts.Token)));
        }

        // Should complete without throw or deadlock once the deadline trips.
        await Task.WhenAll(tasks);
    }

    private static void HammerPlaybackTime(OpenAlAudioSink sink, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _ = sink.GetPlaybackTime();
        }
    }

    private static async Task HammerLifecycle(OpenAlAudioSink sink, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await sink.PauseAsync(ct);
            await sink.ResumeAsync(ct);
            await sink.DeactivateAsync(ct);
        }
    }

    // ── Loop-restart regression ─────────────────────────────────────────────
    //
    // Bug: AvaloniaPlayer's looped playback produced audio on iteration 1 but
    // silence on iterations 2+. Loop restart goes through
    // PlaybackSession.SeekAsync(0) → audioSink.DeactivateAsync →
    // audioSink.ActivateAsync (re-activation branch). The bug was that
    // re-activation reset the bookkeeping counters but left OpenAL source
    // state inconsistent — specifically, DeactivateAsync's trailing
    // FlushStagingBuffer could leave a buffer queued on the stopped source,
    // and re-activation never reconciled that state. Subsequent BufferData
    // / SourceQueueBuffers / SourcePlay calls then ran against a source whose
    // queue head was stale; depending on the OpenAL driver this manifested
    // as silence.
    //
    // This test exercises the same Activate → Present×N → Deactivate →
    // Activate → Present×N sequence at the unit level. Without an audio
    // device it pins the bookkeeping (BlocksWritten resets across the cycle,
    // PresentAsync accepts the second iteration's frames). With a device
    // available, the playback-time advance after each iteration is the
    // load-bearing assertion — proving OpenAL actually consumed the queued
    // samples rather than silently dropping them.

    [RequiresAudioDeviceFact]
    public async Task ReActivation_AfterDeactivate_AcceptsNewFrames()
    {
        await using var sink = new OpenAlAudioSink();
        await sink.ActivateAsync();

        // First iteration: feed enough frames to cross the
        // pre-buffer + coalesce thresholds and trigger SourcePlay.
        for (int i = 0; i < 20; i++)
            await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));

        var firstIterationBlocks = sink.BlocksWritten;
        Assert.True(
            firstIterationBlocks >= 20,
            $"First iteration should have written ≥20 blocks; got {firstIterationBlocks}."
        );

        // Simulate the loop-restart path: Deactivate then re-Activate.
        await sink.DeactivateAsync();
        await sink.ActivateAsync();

        // After re-activation, counters reset to zero (per the re-activation
        // branch in ActivateAsync).
        Assert.Equal(0, sink.BlocksWritten);

        // Second iteration: feed another batch. The sink must accept these
        // frames and write them through OpenAL exactly as on the first
        // iteration. If the re-activation left the OpenAL source in a
        // wedged state, BlocksWritten would still tick (because the count
        // increments in PresentAsync before FlushStagingBuffer) but the
        // assertion below — that the sink stays in a usable state across
        // the cycle — would still pin the bookkeeping invariant.
        for (int i = 0; i < 20; i++)
            await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));

        var secondIterationBlocks = sink.BlocksWritten;
        Assert.True(
            secondIterationBlocks >= 20,
            $"Second iteration should have written ≥20 blocks; got {secondIterationBlocks}. "
                + "Sink stopped accepting frames after Deactivate→Activate cycle."
        );
    }

    [RequiresAudioDeviceFact]
    public async Task ReActivation_DevicePacedPlaybackMatchesFirstIteration()
    {
        // Regression for "AvaloniaPlayer has audio on first loop, silent on
        // loops 2+." The bug signature in OpenAlAudioSink:
        //
        //   - Loop 1: feed 20 frames at faster-than-realtime, sink's
        //     GetPlaybackTime advances at *device-paced* speed (lags input).
        //   - Loop 2+: feed 20 frames at faster-than-realtime, sink's
        //     GetPlaybackTime advances at exactly input rate (i.e.,
        //     OpenAL marks buffers "processed" without playing them).
        //
        // Root cause was DeactivateAsync's trailing FlushStagingBuffer()
        // queueing a leftover buffer onto the stopped source. The next
        // ActivateAsync re-used the source without rewinding; OpenAL Soft's
        // queue head was a stale buffer from the prior iteration, and the
        // driver marked subsequent QueueBuffers as "processed" immediately
        // without device playback — silent loop 2+.
        //
        // This test reproduces both cycles and verifies they show the same
        // device-paced behaviour. Requires a working audio device — on
        // headless CI the playback time stays zero, and the test passes
        // trivially (both iterations equal). On a real machine the
        // assertion catches the regression: iter 1 and iter 2 must show
        // the same *fed-versus-played* gap.

        const int blocksPerIteration = 20;
        const int samplesPerBlock = 4800;
        const int sampleRate = 48000;
        const int channels = 2;

        // Samples fed per iteration → expected wall-clock duration if the
        // device played in real time.
        var fedDurationMs =
            (double)(blocksPerIteration * samplesPerBlock / channels) / sampleRate * 1000.0;

        await using var sink = new OpenAlAudioSink();

        TimeSpan iteration1PlaybackTime;
        TimeSpan iteration2PlaybackTime;

        // ── Iteration 1 ─────────────────────────────────────────────────
        await sink.ActivateAsync();
        for (int i = 0; i < blocksPerIteration; i++)
        {
            await sink.PresentAsync(MakePcmBlock(samplesPerBlock, sampleRate, channels));
            await Task.Delay(40); // pace at ~40ms (slower than 50ms/block real-time)
        }
        iteration1PlaybackTime = sink.GetPlaybackTime();
        await sink.DeactivateAsync();

        // ── Iteration 2 (regression candidate) ──────────────────────────
        await sink.ActivateAsync();
        for (int i = 0; i < blocksPerIteration; i++)
        {
            await sink.PresentAsync(MakePcmBlock(samplesPerBlock, sampleRate, channels));
            await Task.Delay(40);
        }
        iteration2PlaybackTime = sink.GetPlaybackTime();
        await sink.DeactivateAsync();

        // On a machine without an audio device, both iterations report
        // TimeSpan.Zero — assertion below is trivially true and the test
        // passes without flagging false positives.
        if (iteration1PlaybackTime == TimeSpan.Zero)
            return;

        // The pre-fix regression: iter 1 reports realistic device-paced
        // time (e.g. 740ms after feeding 1000ms worth at 40ms intervals),
        // iter 2 reports exactly the fed-rate (1000ms) because the source
        // is wedged and OpenAL marks queues processed without playing.
        //
        // Tolerance: iter 2 should match iter 1 within 100ms. A larger
        // gap (e.g. iter 2 at fedDurationMs while iter 1 lags by 250ms)
        // would indicate the regression has returned.
        var gap = Math.Abs((iteration2PlaybackTime - iteration1PlaybackTime).TotalMilliseconds);
        Assert.True(
            gap < 100,
            $"Iteration 1 reported {iteration1PlaybackTime.TotalMilliseconds:F0}ms playback "
                + $"(device-paced); iteration 2 reported "
                + $"{iteration2PlaybackTime.TotalMilliseconds:F0}ms (gap {gap:F0}ms). "
                + $"Expected gap < 100ms — large gap suggests OpenAL source is wedged "
                + $"after Deactivate→Activate (fed rate would be ~{fedDurationMs:F0}ms)."
        );
    }

    // ── Backpressure (async wait) — end-to-end, device-gated ─────────────────
    //
    // Perf survey A3: FlushStagingBuffer's Thread.Sleep(1) backpressure spin was
    // replaced with an awaited buffer-return signal (AsyncAutoResetEvent). This
    // test drives the *real* sink into backpressure by feeding far more PCM than
    // the 16-buffer pool can hold faster than a real device drains it, so the
    // staging flush repeatedly finds no free buffer, parks on the async wait, and
    // is released as the device recycles processed buffers. The load-bearing
    // assertion is liveness: the run must COMPLETE (not hang) and the sink must
    // register backpressure while still accepting every frame and advancing the
    // clock — i.e. the stalled flush was released by buffer recycles, not wedged.
    //
    // Device-gated (RequiresAudioDeviceFact): the deterministic proof of the
    // release mechanism is AsyncAutoResetEventTests, which runs in CI without a
    // device. On a headless runner with no device the sink stays inert and this
    // returns trivially.

    [RequiresAudioDeviceFact]
    public async Task Backpressure_StalledFlush_DrainsAndCompletes()
    {
        await using var sink = new OpenAlAudioSink();
        await sink.ActivateAsync();

        // If no real device opened, the sink is inert (GetPlaybackTime stays zero
        // and no buffers are ever consumed). Nothing to exercise — pass trivially.
        await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));
        if (sink.GetPlaybackTime() == TimeSpan.Zero && sink.BackpressureCount == 0)
        {
            // Feed a couple more to be sure the device truly isn't draining before
            // declaring "no device"; a real device would start consuming by now.
            await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));
            await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));
            if (sink.GetPlaybackTime() == TimeSpan.Zero)
                return;
        }

        // Feed well beyond the 16-buffer pool, back-to-back (no pacing), so the
        // producer outruns the device and the flush must wait for recycles. The
        // whole loop is bounded by a deadline: if the async wait ever failed to be
        // released by a buffer recycle, this would block and the timeout would fail
        // the test rather than hang the suite.
        const int blocks = 120; // > 16 pool buffers, with margin
        var feed = Task.Run(async () =>
        {
            for (int i = 0; i < blocks; i++)
                await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));
        });

        var finished = await Task.WhenAny(feed, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(
            finished == feed,
            "Feeding past the buffer pool did not complete within 30s — the async "
                + "backpressure wait appears to have wedged instead of being released "
                + "by buffer recycles."
        );
        await feed; // surface any exception from the feed loop

        // The producer outran the device, so at least one flush must have hit the
        // empty-pool path and waited. (On a fast/over-buffered device this could in
        // principle stay zero; assert only that the run made real progress.)
        Assert.True(
            sink.BlocksWritten >= blocks,
            $"Expected ≥{blocks} blocks accepted across the backpressure run; got {sink.BlocksWritten}."
        );
        Assert.True(
            sink.GetPlaybackTime() > TimeSpan.Zero,
            "Clock did not advance — the device never consumed the queued buffers, so "
                + "the backpressure path was not actually exercised."
        );
    }

    // ── Volume / Mute ───────────────────────────────────────────────────────

    [Fact]
    public async Task Volume_DefaultIsUnity()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.Equal(1.0f, sink.Volume);
    }

    [Fact]
    public async Task Muted_DefaultIsFalse()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.False(sink.Muted);
    }

    [Fact]
    public async Task Volume_RoundTrip()
    {
        await using var sink = new OpenAlAudioSink();
        sink.Volume = 0.5f;
        Assert.Equal(0.5f, sink.Volume);
        sink.Volume = 0.0f;
        Assert.Equal(0.0f, sink.Volume);
        sink.Volume = 1.5f; // above-unity values are accepted
        Assert.Equal(1.5f, sink.Volume);
    }

    [Fact]
    public async Task Volume_NegativeOrNaN_Throws()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Volume = -0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Volume = float.NaN);
    }

    [Fact]
    public async Task Muted_RoundTrip_PreservesVolume()
    {
        await using var sink = new OpenAlAudioSink();
        sink.Volume = 0.7f;

        sink.Muted = true;
        Assert.True(sink.Muted);
        // Volume value is preserved across mute toggle — the UX
        // expectation is that unmuting restores the slider position.
        Assert.Equal(0.7f, sink.Volume);

        sink.Muted = false;
        Assert.False(sink.Muted);
        Assert.Equal(0.7f, sink.Volume);
    }

    [RequiresAudioDeviceFact]
    public async Task Volume_PersistsAcrossDeactivateActivate()
    {
        // Set volume before activation; the value must survive both
        // initial activation and a subsequent Deactivate→Activate cycle
        // (loop restart). The native OpenAL source's gain re-applies
        // automatically via ApplyEffectiveGain in both activation paths.
        await using var sink = new OpenAlAudioSink();
        sink.Volume = 0.42f;
        sink.Muted = true;

        await sink.ActivateAsync();
        Assert.Equal(0.42f, sink.Volume);
        Assert.True(sink.Muted);

        await sink.DeactivateAsync();
        await sink.ActivateAsync();
        Assert.Equal(0.42f, sink.Volume);
        Assert.True(sink.Muted);
    }

    [Fact]
    public async Task Volume_SettableBeforeActivation_NoThrow()
    {
        // Pre-activation writes are accepted (deferred until activation).
        await using var sink = new OpenAlAudioSink();
        sink.Volume = 0.3f;
        sink.Muted = true;
        // No throw; values are persisted internally.
        Assert.Equal(0.3f, sink.Volume);
        Assert.True(sink.Muted);
    }

    /// <summary>
    /// Construct a PcmAudioBuffer carrying <paramref name="samples"/>
    /// interleaved int16 samples (total — divide by channels for
    /// per-channel sample count). Sine-wave fill at 440 Hz so the
    /// data isn't all-zero (some drivers special-case silence).
    /// </summary>
    private static PcmAudioBuffer MakePcmBlock(int samples, int sampleRate, int channels) =>
        MakePcmBlock(samples, sampleRate, channels, TimeSpan.Zero);

    /// <summary>
    /// Same as the parameterless-PTS overload but lets the caller supply
    /// a non-zero <see cref="PcmAudioBuffer.PresentationTime"/> — needed
    /// by the IClockSource contract tests below to verify
    /// <see cref="OpenAlAudioSink.GetPlaybackTime"/> reports
    /// source-stream time rather than device-time-since-reactivation.
    /// </summary>
    private static PcmAudioBuffer MakePcmBlock(
        int samples,
        int sampleRate,
        int channels,
        TimeSpan pts
    )
    {
        var owner = MemoryPool<short>.Shared.Rent(samples);
        var span = owner.Memory.Span[..samples];

        for (int i = 0; i < samples; i++)
        {
            double phase = (2.0 * Math.PI * 440.0 * (i / channels)) / sampleRate;
            span[i] = (short)(Math.Sin(phase) * 8000);
        }

        return new PcmAudioBuffer(owner, samples, sampleRate, channels, pts);
    }

    // ── IClockSource contract ───────────────────────────────────────────────
    //
    // OpenAlAudioSink doubles as the master IClockSource whenever audio
    // is present. The contract: the value it publishes via Latest /
    // WaitUntilAsync MUST be in source-stream time — i.e. expressed in
    // the same coordinate system as the PresentationTime of the PCM
    // buffers fed to PresentAsync. Otherwise PacedUntil (which compares
    // clock.Latest against video frame.Pts) breaks across any seek
    // that deactivates+reactivates the sink.
    //
    // These tests pin the contract deterministically — they don't
    // depend on device playback timing, so they pass in headless CI
    // too. They would have caught the bug fixed in commit 1e19ca2
    // (audio clock returned device-time-since-reactivation, not
    // source-stream time).

    [RequiresAudioDeviceFact]
    public async Task IClockSource_AfterFirstBuffer_LatestReflectsBufferPts()
    {
        await using var sink = new OpenAlAudioSink();
        var clock = (IClockSource)sink;

        await sink.ActivateAsync();
        // First buffer post-activate carries PTS=30s — emulates the
        // initial-state-after-fresh-load case with a non-zero start.
        await sink.PresentAsync(
            MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2, pts: TimeSpan.FromSeconds(30))
        );

        // GetPlaybackTime should report ≥ 30s immediately — the
        // baseline is captured from the buffer's PTS, the per-device
        // playback advances from there. Without the baseline capture
        // the value would be 0 (or near-zero device-paced).
        Assert.True(
            sink.GetPlaybackTime() >= TimeSpan.FromSeconds(30),
            $"GetPlaybackTime returned {sink.GetPlaybackTime()}; expected ≥ 30s "
                + "(first buffer PTS=30s should establish the source-time baseline)."
        );
    }

    [RequiresAudioDeviceFact]
    public async Task IClockSource_AfterReactivate_LatestReflectsNewSeekTarget()
    {
        // Regression for the freeze-after-seek bug. The arrow-key
        // seek to position=60s in a 213 s clip froze video for 60
        // real seconds: the audio sink deactivated + reactivated,
        // then PacedUntil saw the published clock start from 0
        // instead of 60s, so it waited for the clock to climb from
        // 0 to 60s before releasing any video frame.
        //
        // PacedUntil reads via `IClockSource.Latest`, which is the
        // ticker-published value — NOT `GetPlaybackTime()` directly.
        // The original fix attempt corrected GetPlaybackTime but
        // the ticker was applying `min(audioTime, sessionTime)`
        // where sessionTime resets to 0 on each activation, clamping
        // the published value back to 0. This test asserts via the
        // IClockSource surface to catch both regressions.
        await using var sink = new OpenAlAudioSink();
        var clock = (IClockSource)sink;

        // ── Iteration 1: feed at PTS=10s (the pre-seek position) ────
        await sink.ActivateAsync();
        await sink.PresentAsync(
            MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2, pts: TimeSpan.FromSeconds(10))
        );
        Assert.True(
            sink.GetPlaybackTime() >= TimeSpan.FromSeconds(10),
            $"Pre-reactivate GetPlaybackTime was {sink.GetPlaybackTime()}; expected ≥ 10s."
        );

        // ── Seek path: deactivate then reactivate ───────────────────
        await sink.DeactivateAsync();
        Assert.Equal(TimeSpan.Zero, sink.GetPlaybackTime());

        await sink.ActivateAsync();
        Assert.Equal(TimeSpan.Zero, sink.GetPlaybackTime());

        // ── Iteration 2: feed at PTS=60s (the seek target) ──────────
        await sink.PresentAsync(
            MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2, pts: TimeSpan.FromSeconds(60))
        );

        Assert.True(
            sink.GetPlaybackTime() >= TimeSpan.FromSeconds(60),
            $"Post-reactivate GetPlaybackTime was {sink.GetPlaybackTime()}; expected ≥ 60s. "
                + "Baseline capture in PresentAsync has regressed."
        );

        // Wait long enough for the 5 ms clock ticker to publish
        // post-baseline. Anything less than 60s after a few ticks
        // means the ticker is publishing a stale or clamped value —
        // exactly what PacedUntil would see, exactly what freezes
        // video for `seek-target` real seconds in the real player.
        await WaitForClockLatest(clock, atLeast: TimeSpan.FromSeconds(60), within: TimeSpan.FromSeconds(2));
    }

    private static async Task WaitForClockLatest(
        IClockSource clock,
        TimeSpan atLeast,
        TimeSpan within
    )
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (clock.Latest >= atLeast)
                return;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException(
            $"IClockSource.Latest stayed at {clock.Latest} after {within} — expected ≥ {atLeast}. "
                + "The ticker's published value is what PacedUntil consumes; if Latest stays "
                + "near zero after a reactivate-with-non-zero-PTS, video freezes for "
                + "`baseline-PTS` real seconds. Check OpenAlAudioSink.RunTickerAsync's "
                + "publication arithmetic — earlier the `min(audioTime, sessionTime)` "
                + "guard clamped it back to 0."
        );
    }

    [RequiresAudioDeviceFact]
    public async Task IClockSource_BeforeAnyBuffers_LatestIsZero()
    {
        // The baseline can only be captured when a buffer arrives.
        // Until then, IClockSource consumers see zero — which is the
        // right thing: PacedUntil with an empty clock at zero waits
        // for the first publication rather than racing forward.
        await using var sink = new OpenAlAudioSink();
        var clock = (IClockSource)sink;

        await sink.ActivateAsync();
        Assert.Equal(TimeSpan.Zero, sink.GetPlaybackTime());
        Assert.Equal(TimeSpan.Zero, clock.Latest);
    }

    [Fact]
    public async Task Sink_ImplementsIClockSource()
    {
        await using var sink = new OpenAlAudioSink();
        Assert.IsAssignableFrom<IClockSource>(sink);
    }
}
