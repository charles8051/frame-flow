// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Content coverage of the <b>real</b> <c>OpenAlAudioSink</c>, driven by a fake
/// OpenAL device so what the sink handed the device is assertable on a headless
/// runner (#146).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Every other content test in this suite substitutes
/// a capturing sink for the production one. Those captures record what the
/// pipeline handed the sink, so they prove the decode and pacing path and say
/// nothing about the sink. The sink's own buffer queue, its recycle, its
/// pre-buffer gate and the clock it publishes back as the pacing master were
/// covered by unit tests over a pure value and by device-gated tests that are
/// skipped in CI. Neither could assert the audio.
/// </para>
/// <para>
/// <b>Cost.</b> These run at 1x: the sink clamps its published clock to elapsed
/// real time (#127), so <see cref="FakeDevicePump"/> cannot fast-forward. The
/// corpus clips used here are three seconds each, and the set is deliberately
/// small for that reason.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Tier", "1")]
public sealed class OpenAlSinkContentTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public OpenAlSinkContentTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The audio the device played is the audio a bare decode of the same file
    /// produces.
    /// </summary>
    /// <remarks>
    /// This is the assertion the suite has never been able to make. It runs the
    /// full path — demux, decode, pace, and the sink's own coalesce, upload,
    /// queue and recycle — and compares the result against
    /// <see cref="ReferenceDecoder"/>'s ground truth. A sink that queued audio the
    /// device never played fails here, and every counter it reports would still
    /// read correct, which is exactly how #133 presented.
    /// </remarks>
    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_DevicePlaysTheAudioTheDecoderProduced()
    {
        var result = await OpenAlPlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");

        Assert.True(
            result.LoadResult.IsSuccess,
            $"Load failed: {result.LoadResult.Error?.Message}"
        );
        Assert.True(
            result.PlayResult.IsSuccess,
            $"Play failed: {result.PlayResult.Error?.Message}"
        );
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        Assert.True(
            result.Drained,
            "The device still held unplayed audio when the drain gave up, so the capture "
                + "below was taken while it was still growing. A shortfall inside the tail "
                + "budget would otherwise pass over an incomplete run."
        );

        Assert.NotEmpty(result.PlayedAudio);

        var reference = await ReferenceDecoder.DecodeAsync("test-av-h264-aac.mp4");

        PlaybackInvariants.AudioPcmMatchesReference(
            result.PlayedAudio,
            reference.Audio,
            maxRmsErrorPerSample: 4.0,
            // One coalesce buffer. The sink builds ~50 ms buffers and discards a
            // trailing staging block that never reaches the threshold, so the last
            // fraction of a clip is never handed to the device. That is deliberate
            // — queueing it onto the stopped source is what wedged loop restarts —
            // and it is the only shortfall this assertion permits.
            maxTailShortfall: TimeSpan.FromMilliseconds(50)
        );
    }

    /// <summary>
    /// The device is not left starved at the end of a clean play-to-EOF. A run
    /// that ends with the source stopped and audio still queued is the shape of a
    /// sink that gave up partway.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_LeavesNoAudioUnplayedOnTheDevice()
    {
        var result = await OpenAlPlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var reference = await ReferenceDecoder.DecodeAsync("test-av-h264-aac.mp4");

        int referenceSamples = reference.Audio.Sum(a => a.InterleavedSamples.Length);
        int playedSamples = result.Device.PlayedSamples.Count;

        // The sink coalesces into ~50 ms buffers and drops a trailing partial that
        // never reaches the threshold, so allow one buffer of shortfall.
        int tolerance = 4800;

        Assert.True(
            playedSamples >= referenceSamples - tolerance,
            $"The device played {playedSamples} samples against a reference of "
                + $"{referenceSamples}, short by {referenceSamples - playedSamples} "
                + $"(tolerance {tolerance}). The sink reports "
                + $"BlocksWritten={result.Sink.BlocksWritten}, "
                + $"UnderrunCount={result.Sink.UnderrunCount}, "
                + $"BackpressureCount={result.Sink.BackpressureCount} — counters that "
                + "stayed correct through #133 while nothing came out."
        );
    }

    /// <summary>
    /// The widened tail tolerance still catches a real dropout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comparing against a real sink meant allowing one coalesce buffer of
    /// shortfall at the end, because the sink discards a sub-threshold trailing
    /// block on purpose. A tolerance is a hole in a gate, so this states where the
    /// hole stops: audio missing from anywhere but the tail still fails, and so
    /// does a tail longer than one buffer.
    /// </para>
    /// <para>
    /// Pure data, no playback. The gate has to be seen failing, or it is
    /// indistinguishable from a gate that cannot fail.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0, 4800, "a whole buffer missing from the start")]
    [InlineData(120000, 4800, "a whole buffer missing from the middle")]
    [InlineData(278400, 9600, "a tail twice the one-buffer budget")]
    public void AudioPcmMatchesReference_StillCatchesAGap(int at, int length, string what)
    {
        const int rate = 48000;
        const int channels = 2;
        const int total = 288000; // 3 s stereo

        var reference = new short[total];
        for (int i = 0; i < total; i++)
            reference[i] = (short)((i % 2000) - 1000);

        // The capture with `length` samples cut out at `at`, and nothing put back:
        // a dropout shortens the stream, which is what the length check is for.
        var damaged = new short[total - length];
        Array.Copy(reference, 0, damaged, 0, at);
        Array.Copy(reference, at + length, damaged, at, total - at - length);

        var ex = Record.Exception(() =>
            PlaybackInvariants.AudioPcmMatchesReference(
                [new AudioCapture(TimeSpan.Zero, damaged, rate, channels)],
                [new AudioCapture(TimeSpan.Zero, reference, rate, channels)],
                maxRmsErrorPerSample: 4.0,
                maxTailShortfall: TimeSpan.FromMilliseconds(50)
            )
        );

        Assert.True(ex is not null, $"The invariant did not catch {what}.");
    }

    /// <summary>
    /// Raising the shortfall budget does not license extra audio.
    /// </summary>
    /// <remarks>
    /// The budget is one-directional, and this is why. A capture carrying the
    /// whole reference plus a duplicated tail is audible duplication — the
    /// ADR-0031 bug class — and with a symmetric budget it passed: the excess sat
    /// inside the allowance, and the sample comparison only ever reads the common
    /// prefix, so the duplicate was never looked at.
    /// </remarks>
    [Theory]
    [InlineData(2, "a two-sample duplicated tail")]
    [InlineData(480, "5 ms of duplicated tail, which the old 10 ms allowance passed")]
    [InlineData(2400, "half a buffer of duplicated tail")]
    [InlineData(4800, "a whole buffer of duplicated tail")]
    public void AudioPcmMatchesReference_RejectsExtraAudio(int extra, string what)
    {
        const int rate = 48000;
        const int channels = 2;
        const int total = 288000;

        var reference = new short[total];
        for (int i = 0; i < total; i++)
            reference[i] = (short)((i % 2000) - 1000);

        // The full reference with its own last `extra` samples played again.
        var duplicated = new short[total + extra];
        reference.CopyTo(duplicated, 0);
        Array.Copy(reference, total - extra, duplicated, total, extra);

        var ex = Record.Exception(() =>
            PlaybackInvariants.AudioPcmMatchesReference(
                [new AudioCapture(TimeSpan.Zero, duplicated, rate, channels)],
                [new AudioCapture(TimeSpan.Zero, reference, rate, channels)],
                maxRmsErrorPerSample: 4.0,
                maxTailShortfall: TimeSpan.FromMilliseconds(50)
            )
        );

        Assert.True(ex is not null, $"The invariant did not catch {what}.");
    }

    /// <summary>
    /// The other side of the tolerance: a shortfall inside the budget, at the
    /// tail, is accepted. Without this the widening is untested in the direction
    /// it was made for.
    /// </summary>
    [Fact]
    public void AudioPcmMatchesReference_AcceptsAShortfallInsideTheTailBudget()
    {
        const int rate = 48000;
        const int channels = 2;
        const int total = 288000;
        const int dropped = 2230; // what the real sink dropped on the corpus clip

        var reference = new short[total];
        for (int i = 0; i < total; i++)
            reference[i] = (short)((i % 2000) - 1000);

        var truncated = reference.AsSpan(0, total - dropped).ToArray();

        PlaybackInvariants.AudioPcmMatchesReference(
            [new AudioCapture(TimeSpan.Zero, truncated, rate, channels)],
            [new AudioCapture(TimeSpan.Zero, reference, rate, channels)],
            maxRmsErrorPerSample: 4.0,
            maxTailShortfall: TimeSpan.FromMilliseconds(50)
        );
    }

    /// <summary>
    /// The sink masters the pacing clock in this topology, so the video that came
    /// out has to be right too: every frame presented is the reference decode's
    /// frame, and every frame not presented is one the pacer counted as dropped
    /// late.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserted an exact frame count, and failed intermittently under suite
    /// load (#400). The sink's clock follows the device, which plays in real time,
    /// so a runner descheduled for longer than a frame interval makes the pacer
    /// drop a frame on purpose. The count measured the runner as much as the sink.
    /// The frame account in
    /// <see cref="PlaybackInvariants.VideoFramePixelsMatchReference"/> holds at any
    /// speed (#299).
    /// </para>
    /// <para>
    /// What the count also stood in for, a sink clock running ahead of the device
    /// and flooding the pacer with late frames, now shows here only as late drops,
    /// which are accounted rather than failed. The rate limit that holds the clock
    /// to real time is <c>AudioClockRateLimit</c>, covered without a clock by
    /// <c>AudioClockRateLimitTests</c>. A clock that stops still fails here: each
    /// frame then waits out the pacer's 5 s cap, so the run does not reach
    /// <see cref="PlaybackState.Ended"/> within the harness's bound.
    /// </para>
    /// </remarks>
    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_UnderTheRealSinkClock_PresentsOrCountsEveryFrame()
    {
        var result = await OpenAlPlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var reference = await ReferenceDecoder.DecodeAsync("test-av-h264-aac.mp4");

        PlaybackInvariants.PtsStrictlyMonotonic(result.Video, v => v.Pts, "video");

        // The clip has 90 video packets against a queue of at least 512, so the decoder
        // cannot shed and the account needs no term for it.
        PlaybackInvariants.VideoFramePixelsMatchReference(
            result.Video,
            reference.Video,
            droppedLate: result.Diagnostics.Pipeline.VideoFramesDroppedForSync
        );
    }
}
