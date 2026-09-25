using System.Diagnostics;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// <see cref="OpenAlAudioSink"/> against a real OpenAL Soft device, for the behaviours that
/// only a real device over real time can show.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the integration suite.</b> Moved from <c>OpenAlAudioSinkTests</c> in
/// <c>FrameFlow.Audio.Tests</c>. Each test here lets a real device play for a stretch of real
/// time and then reads what it did: a paced feed across a deactivate and reactivate, and a
/// three-second silence long enough for the source to starve. Elapsed time is what they
/// measure, and ADR-0072 rule 6 names this suite as the one allowed to measure it.
/// </para>
/// <para>
/// Underrun recovery also runs headless, in <c>OpenAlAudioSinkFakeDeviceTests</c>, against a
/// fake device that models the stopped-source reporting behind it. The run here is the one
/// against OpenAL Soft itself. Both tests are opt-in through
/// <see cref="RequiresAudioDeviceFactAttribute"/> and do not run in CI.
/// </para>
/// </remarks>
[Collection(OpenAlDeviceCollection.Name)]
public sealed class OpenAlAudioSinkRealDeviceTests
{
    // ── Loop-restart regression ─────────────────────────────────────────────

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
        // These play on the machine running the test. Gain is applied at the mix, so it does
        // not change the device-paced playback time this test compares.
        sink.Volume = 0.05f;

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

    // ── Underrun recovery (#133) ────────────────────────────────────────────

    // An intermittently-fed sink went permanently silent after its first underrun: the
    // first burst played, every burst after it was accepted, counted and inaudible.
    //
    // OpenAL Soft reports the whole queue of a stopped source as processed, including
    // buffers queued after it stopped. RecycleProcessedBuffers ran at the top of every
    // flush, so each re-primed buffer was unqueued before the next one arrived, the depth
    // oscillated between 0 and 1, and the pre-buffer gate never reached PreBufferCount to
    // fire SourcePlay again.
    //
    // Nothing in the sink's own reporting said so, which is what makes the assertion below
    // the one worth making. BlocksWritten rose by the full count, GetPlaybackTime advanced
    // by exactly the duration pushed (the fake processed count was credited to the clock),
    // and UnderrunCount stayed at 1 (the latch it would need to re-observe a starve was
    // cleared and never re-latched). The one counter that told the truth was backpressure:
    // a dead sink never fills its pool, so pushing into one returns immediately.
    //
    // Device-gated because this is the run against OpenAL Soft itself. The same mechanism
    // reproduces headless against FakeOpenAlDevice in
    // OpenAlAudioSinkFakeDeviceTests.IntermittentFeed_*, and the decision layer is covered by
    // BufferQueueStateTests.Priming_*, both in FrameFlow.Audio.Tests.

    [RequiresAudioDeviceFact]
    public async Task Underrun_RefeedAfterSilenceResumesPlayback()
    {
        await using var sink = new OpenAlAudioSink();
        // These play on the machine running the test. Audible enough to check by ear,
        // quiet enough not to matter.
        sink.Volume = 0.05f;
        await sink.ActivateAsync();

        // Twenty blocks into a sixteen-buffer pool. The overflow is what makes the
        // assertions below arithmetic rather than timing: the producer cannot finish
        // pushing until the device has finished at least BurstBlocks - BufferPoolSize of
        // them, and the device finishes buffers in real time, not at memcpy speed.
        const int burstBlocks = 20;
        const int bufferPoolSize = 16;
        const int blockSamplesPerChannel = 2400; // 4800 interleaved / 2 channels
        const long mustDrainPerChannel = (burstBlocks - bufferPoolSize) * blockSamplesPerChannel;

        // ── Burst 1: two seconds, then let the source starve ────────────────
        await FeedBurstAsync(sink, blocks: burstBlocks);

        // Is there a device at all. The clock only advances on buffers OpenAL reported
        // finished, so two seconds that left it at zero is a device that did not open or
        // is not playing. Availability only — whether the pool saturated is the scenario's
        // business, asserted below where a failure names the right thing.
        //
        // FRAMEFLOW_AUDIO_DEVICE_TESTS=1 is the operator asserting there is a device, so
        // this fails rather than returning green. The neighbouring device-gated tests
        // return instead; xUnit v2 has no dynamic skip, so the choice is between failing
        // and passing a test that exercised nothing, and a regression that turns a sink
        // silently inaudible is exactly the kind that hides behind the second. CI does not
        // set the variable and skips at the attribute rather than reaching here.
        Assert.True(
            sink.GetPlaybackTime() > TimeSpan.Zero,
            "Burst 1 left the playback clock at zero, so nothing drained it and the "
                + "underrun this test depends on cannot be provoked. With "
                + "FRAMEFLOW_AUDIO_DEVICE_TESTS set that is a device that did not open or "
                + "did not play — not a reason to pass."
        );

        // Two seconds of audio, three seconds of silence: the queue is empty and the
        // source is AL_STOPPED well before the next burst arrives.
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Everything burst 1 played, credited. Burst 1 is fully drained by now, so the
        // delta across burst 2 is burst 2's own audio and nothing else.
        long processedBeforeSecond = sink.GetDiagnostics().ProcessedSamplesPerChannel;

        // ── Burst 2: the same feed into the same sink ───────────────────────
        var sw = Stopwatch.StartNew();
        await FeedBurstAsync(sink, blocks: burstBlocks);
        sw.Stop();

        Assert.True(
            sink.UnderrunCount >= 1,
            "The source was expected to starve during the three-second gap, but no "
                + "underrun was observed — the test never reached the state it guards."
        );

        // The source restarted. IsActive is the source-started latch, and the only thing
        // that sets it is the pre-buffer gate firing and the shell calling SourcePlay. A
        // sink wedged on a stopped source clears the latch at the underrun and never
        // re-latches, because the queue depth never reaches PreBufferCount again.
        Assert.True(
            sink.GetDiagnostics().IsActive,
            "The source-started latch is still clear after the second burst, so the "
                + "pre-buffer gate never re-fired and SourcePlay was never re-issued. "
                + "Everything pushed since the underrun is queued on a stopped source "
                + "(#133)."
        );

        // And burst 2's own buffers went through the device. The pool cannot hold the
        // whole burst, so the push above could not have returned until the device
        // finished at least the overflow — a lower bound from the pool arithmetic, not
        // from how fast anything ran. A latched-but-idle source never reaches it.
        long drainedDuringSecond =
            sink.GetDiagnostics().ProcessedSamplesPerChannel - processedBeforeSecond;
        Assert.True(
            drainedDuringSecond >= mustDrainPerChannel,
            $"The device finished {drainedDuringSecond} samples/channel of burst 2, below the "
                + $"{mustDrainPerChannel} the pool overflow requires. Burst 2's push returned in "
                + $"{sw.Elapsed.TotalSeconds:0.00}s, so the sink accepted {burstBlocks} blocks "
                + $"into a {bufferPoolSize}-buffer pool without the device draining any of "
                + "them (#133)."
        );

        // Neither assertion proves audibility, and nothing short of capturing the output
        // would: a dead endpoint marks buffers processed without playing them, which is
        // what DeviceDisconnected rather than any of these counters exists for (#127).
        Assert.False(
            sink.DeviceDisconnected,
            "The output endpoint went away mid-test, so nothing above says what was played."
        );
    }

    private static async Task FeedBurstAsync(OpenAlAudioSink sink, int blocks)
    {
        for (int i = 0; i < blocks; i++)
            await sink.PresentAsync(MakePcmBlock(samples: 4800, sampleRate: 48000, channels: 2));
    }

    /// <summary>
    /// Construct a PcmAudioBuffer carrying <paramref name="samples"/>
    /// interleaved int16 samples (total — divide by channels for
    /// per-channel sample count). Sine-wave fill at 440 Hz so the
    /// data isn't all-zero (some drivers special-case silence).
    /// </summary>
    private static PcmAudioBuffer MakePcmBlock(int samples, int sampleRate, int channels)
    {
        return PcmAudioBuffer.Create(
            samples,
            sampleRate,
            channels,
            TimeSpan.Zero,
            (Rate: sampleRate, Channels: channels),
            static (span, p) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    double phase = (2.0 * Math.PI * 440.0 * (i / p.Channels)) / p.Rate;
                    span[i] = (short)(Math.Sin(phase) * 8000);
                }
                return span.Length;
            }
        );
    }
}
