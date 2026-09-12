// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Audio.TestKit;
using FrameFlow.Media;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Drives <see cref="OpenAlAudioSink"/> against <see cref="FakeOpenAlDevice"/>,
/// so what the sink hands the device — and the order it does it in — are
/// assertable with no sound card (#138).
/// </summary>
/// <remarks>
/// <para>
/// These run everywhere. The existing device-gated tests prove the same
/// behaviours against a real OpenAL Soft but are opt-in
/// (<c>FRAMEFLOW_AUDIO_DEVICE_TESTS=1</c>), depend on a real device's timing, and
/// can assert counters only. <see cref="BufferQueueStateTests"/> covers the pure
/// decision layer, which cannot see the shell's calls at all. This is the middle
/// neither reaches.
/// </para>
/// <para>
/// Playback is virtual: nothing advances until a test calls
/// <see cref="FakeOpenAlDevice.AdvancePlayback"/>. A whole burst-gap-burst
/// session therefore runs in microseconds and is exactly reproducible.
/// </para>
/// </remarks>
public sealed class OpenAlAudioSinkFakeDeviceTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    // One PresentAsync block maps 1:1 onto one OpenAL buffer: the sink coalesces
    // to 4800 scalars, which is 2400 stereo frames, which is 50 ms at 48 kHz.
    private const int ScalarsPerBlock = 4800;
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(50);

    // OpenAlAudioSink's own pool geometry, mirrored so the assertions can name it.
    private const int PreBufferCount = 4;
    private const int BufferPoolSize = 16;

    // ── #133, headless ──────────────────────────────────────────────────────

    /// <summary>
    /// A sink fed intermittently must still play the bursts that follow its first
    /// underrun. This is #133 reproduced with no device and no waiting: burst one,
    /// a gap long enough for the source to starve, then burst two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion is on the PCM the device actually played, not on a counter.
    /// Every counter stayed correct while #133 was live — blocks written rose by
    /// the full count, the reported playback time advanced by exactly the duration
    /// pushed, and the underrun count stayed at one. The only thing that was wrong
    /// was that nothing came out.
    /// </para>
    /// <para>
    /// The mechanism the fake reproduces is OpenAL 1.1 §4.3.2: a stopped source
    /// reports its whole queue processed, including buffers queued after it
    /// stopped and never played. A sink that recycles on that count sees its queue
    /// depth oscillate below the pre-buffer threshold and never starts the source
    /// again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IntermittentFeed_PlaysTheBurstAfterAnUnderrun()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);

        const short firstBurst = 1000;
        const short secondBurst = 2000;

        await PushBlocksAsync(sink, blocks: 10, amplitude: firstBurst);
        DrainFully(device);

        // The source has starved. This is the state #133 never recovered from.
        Assert.Equal(AlState.Stopped, device.StateOf(device.SingleSource()));
        Assert.Contains(firstBurst, device.PlayedSamples);

        await PushBlocksAsync(sink, blocks: 10, amplitude: secondBurst);
        DrainFully(device);

        Assert.True(
            device.PlayedSamples.Contains(secondBurst),
            "The second burst was accepted by the sink but never reached the device. "
                + $"Underruns={sink.UnderrunCount}, blocks written={sink.BlocksWritten}, "
                + $"samples played={device.PlayedSamples.Count}.\n"
                + device.DescribeCalls()
        );
    }

    /// <summary>
    /// The recovery is not partial: after the underrun the device receives every
    /// sample of the second burst, not a prefix of it.
    /// </summary>
    [Fact]
    public async Task IntermittentFeed_PlaysTheWholeSecondBurst()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: 8, amplitude: 1000);
        DrainFully(device);

        await PushBlocksAsync(sink, blocks: 8, amplitude: 2000);
        DrainFully(device);

        int secondBurstPlayed = device.PlayedSamples.Count(s => s == 2000);

        // Eight blocks of 4800 scalars. The sink may hold back a trailing partial
        // block that never reached the coalesce threshold, so allow one block of
        // slack rather than demanding the exact count.
        Assert.True(
            secondBurstPlayed >= 7 * ScalarsPerBlock,
            $"Expected the device to play at least {7 * ScalarsPerBlock} samples of the "
                + $"second burst; it played {secondBurstPlayed}."
        );
    }

    /// <summary>
    /// Recovery from an underrun has to repeat. Burst, starve, burst, starve, for as many
    /// cycles as the theory asks — every burst must reach the device, not just the second one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two tests above stop after one recovery, and a sink that recovered once and then
    /// wedged would pass both. #133's actual reproducer was four 2-second tones six seconds
    /// apart, so the shape that was reported is the four-burst case rather than the two-burst
    /// one. #141 asks for the gap pattern as a test parameter for this reason: the feed pattern
    /// is a property of how the consumer calls the sink, not of any media file, so no corpus
    /// entry can produce it.
    /// </para>
    /// <para>
    /// Each burst carries its own amplitude, so "did burst N reach the device" is a question
    /// about content rather than about counters — the same distinction
    /// <c>ContinuousFeed_DeviceReceivesTheSamplesInOrder</c> draws. Every burst is checked and
    /// the failure names all the ones that went missing, so a sink that dies on the fourth
    /// cycle reports that rather than just "something was missing".
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(4)] // the shape #133 was reported in
    [InlineData(6)]
    public async Task IntermittentFeed_RecoversOnEveryBurst(int bursts)
    {
        const int blocksPerBurst = 8;
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);

        var failures = new List<string>();
        for (int burst = 0; burst < bursts; burst++)
        {
            int playedBefore = device.PlayedSamples.Count;

            // One distinct value per block, not per burst. A constant-amplitude burst cannot
            // tell a duplicated block from the one it replaced, or two blocks swapped, because
            // every sample in it looks the same. Stepping the value per block makes the device's
            // record a sequence that only the right blocks in the right order can match.
            var expected = new List<short>(blocksPerBurst * ScalarsPerBlock);
            for (int block = 0; block < blocksPerBurst; block++)
            {
                var value = (short)(1000 * (burst + 1) + block);
                await PushBlocksAsync(
                    sink,
                    blocks: 1,
                    amplitude: value,
                    firstBlockIndex: (burst * blocksPerBurst) + block
                );
                expected.AddRange(Enumerable.Repeat(value, ScalarsPerBlock));
            }
            DrainFully(device);

            // Exactly what this cycle appended — not a count over the cumulative record, which
            // stale or duplicated samples from another cycle could satisfy. No slack: blocks are
            // pushed at exactly the coalesce size, so nothing is held back as a partial, and
            // measured delivery is every block, in order, 4800 samples each.
            var appended = device.PlayedSamples.Skip(playedBefore).ToList();
            if (!appended.SequenceEqual(expected))
                failures.Add($"burst {burst + 1}: {DescribeRuns(appended)}");
        }

        Assert.True(
            failures.Count == 0,
            $"Of {bursts} bursts, {failures.Count} did not reach the device intact and in order. "
                + $"Underruns={sink.UnderrunCount}, blocks written={sink.BlocksWritten}."
                + Environment.NewLine
                + string.Join(Environment.NewLine, failures)
                + Environment.NewLine
                + device.DescribeCalls()
        );

        // Proves the gaps are real. Every burst but the first is preceded by a drain that
        // starves the source, so N bursts must cost N-1 underruns. Without this a change that
        // stopped the drain from starving anything would leave the test passing while it had
        // quietly stopped testing recovery at all — and the six-burst case would be five
        // recovery cycles on paper and one in fact.
        Assert.True(
            sink.UnderrunCount == bursts - 1,
            $"{bursts} bursts separated by full drains should starve the source {bursts - 1} "
                + $"times; the sink counted {sink.UnderrunCount}."
        );
    }

    // ── Ordering against the device ─────────────────────────────────────────

    /// <summary>
    /// The source is not started until the queue has reached the pre-buffer
    /// depth. Starting earlier is what makes a sink audibly crackle on a
    /// high-latency stack, and it is invisible to every counter.
    /// </summary>
    [Fact]
    public async Task SourcePlay_WaitsForThePreBufferDepth()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: PreBufferCount + 2, amplitude: 500);

        var calls = device.Calls;
        int firstPlay = IndexOfFirst(calls, "SourcePlay");
        Assert.True(firstPlay >= 0, "The sink never started the source.\n" + device.DescribeCalls());

        int queuedBeforePlay = calls
            .Take(firstPlay)
            .Count(c => c.Name == "SourceQueueBuffers");

        Assert.True(
            queuedBeforePlay >= PreBufferCount,
            $"SourcePlay fired with only {queuedBeforePlay} buffers queued; the pre-buffer "
                + $"gate is {PreBufferCount}.\n"
                + device.DescribeCalls()
        );
    }

    /// <summary>
    /// Deactivation stops the source before draining its queue. The order matters
    /// because only a stopped source reports its whole queue processed, which is
    /// what makes the drain able to return every buffer to the pool (§4.3.2).
    /// </summary>
    [Fact]
    public async Task Deactivate_StopsTheSourceBeforeDrainingIt()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 6, amplitude: 500);

        int before = device.Calls.Count;
        await sink.DeactivateAsync(CancellationToken.None);

        var during = device.Calls.Skip(before).ToArray();
        int stop = IndexOfFirst(during, "SourceStop");
        int unqueue = IndexOfFirst(during, "SourceUnqueueBuffers");

        Assert.True(stop >= 0, "Deactivate did not stop the source.\n" + device.DescribeCalls());
        Assert.True(
            unqueue < 0 || stop < unqueue,
            "Deactivate unqueued before stopping the source; a playing source reports only "
                + "the buffers it has finished, so the drain would leave buffers behind.\n"
                + device.DescribeCalls()
        );
    }

    /// <summary>
    /// Gain reaches the device before any audio does, so the first buffer plays at
    /// the volume the consumer asked for rather than at unity.
    /// </summary>
    [Fact]
    public async Task Volume_ReachesTheDeviceBeforeTheFirstBufferIsQueued()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        sink.Volume = 0.25f;

        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 2, amplitude: 500);

        var calls = device.Calls;
        int gain = IndexOfFirst(calls, "SetSourceProperty");
        int firstQueue = IndexOfFirst(calls, "SourceQueueBuffers");

        Assert.True(gain >= 0, "Gain was never pushed to the device.\n" + device.DescribeCalls());
        Assert.True(
            firstQueue < 0 || gain < firstQueue,
            "The first buffer was queued before the gain was set.\n" + device.DescribeCalls()
        );
        Assert.Equal(0.25f, device.GainOf(device.SingleSource()));
    }

    // ── Resource lifetime ───────────────────────────────────────────────────

    /// <summary>
    /// Disposal returns every name the sink generated. A leak here is a native
    /// resource leak per playback session, and nothing in the sink's own
    /// reporting would show it.
    /// </summary>
    [Fact]
    public async Task Dispose_DeletesEverySourceAndBufferItGenerated()
    {
        var device = new FakeOpenAlDevice();
        var sink = NewSink(device);

        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 6, amplitude: 500);
        DrainFully(device);

        Assert.Equal(BufferPoolSize, device.Calls.Count(c => c.Name == "GenBuffer"));

        await sink.DisposeAsync();

        Assert.Equal(0, device.LiveBuffers);
        Assert.Equal(0, device.LiveSources);
        Assert.Equal(0, device.LeasesOutstanding);
    }

    // ── The clock, headless ─────────────────────────────────────────────────

    /// <summary>
    /// A paused sink reports a still clock. The device's counters keep moving
    /// while it is paused — a paused source that drains reports its whole queue
    /// processed — and crediting that is what put the clock a buffer-pool ahead of
    /// the picture in #127.
    /// </summary>
    [Fact]
    public async Task GetPlaybackTime_HoldsStillWhilePaused()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: 8, amplitude: 500);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(200));

        await sink.PauseAsync(CancellationToken.None);
        var atPause = sink.GetPlaybackTime();

        // Whatever the device does now is not evidence of anything.
        device.AdvancePlayback(TimeSpan.FromMilliseconds(500));
        device.SourceStop(device.SingleSource());

        Assert.Equal(atPause, sink.GetPlaybackTime());
    }

    // ── Content, headless ───────────────────────────────────────────────────

    /// <summary>
    /// Under a continuous feed the device receives the samples that were pushed,
    /// in order. This is the assertion the integration suite cannot make today:
    /// every content test there substitutes a capturing sink for this one, so it
    /// records what the pipeline handed the sink and never what the sink handed
    /// OpenAL (#140, #142).
    /// </summary>
    [Fact]
    public async Task ContinuousFeed_DeviceReceivesTheSamplesInOrder()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = NewSink(device);
        await sink.ActivateAsync(CancellationToken.None);

        // A rising ramp, one distinct value per block, so a reordering or a
        // dropped block is visible in the played sequence rather than only in a count.
        var expected = new List<short>();
        for (short block = 1; block <= 10; block++)
        {
            await PushBlocksAsync(sink, blocks: 1, amplitude: block);
            for (int i = 0; i < ScalarsPerBlock; i++)
                expected.Add(block);
        }

        DrainFully(device);

        var played = device.PlayedSamples;
        Assert.Equal(expected.Count, played.Count);
        Assert.Equal(expected, played);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static OpenAlAudioSink NewSink(FakeOpenAlDevice device) =>
        FakeOpenAlSink.Create(device, timeProvider: TimeProvider.System);

    private static async Task PushBlocksAsync(
        OpenAlAudioSink sink,
        int blocks,
        short amplitude,
        int firstBlockIndex = 0
    )
    {
        for (int i = 0; i < blocks; i++)
        {
            var owner = MemoryPool<short>.Shared.Rent(ScalarsPerBlock);
            owner.Memory.Span[..ScalarsPerBlock].Fill(amplitude);
            var pts = TimeSpan.FromTicks(BlockDuration.Ticks * (firstBlockIndex + i));
            await sink.PresentAsync(
                new PcmAudioBuffer(owner, ScalarsPerBlock, Rate, Channels, pts),
                CancellationToken.None
            );
        }
    }

    /// <summary>
    /// Plays out everything currently queued, one block at a time so each buffer
    /// boundary is crossed the way a real device crosses it. Leaves the source
    /// starved, which is the state a gap in the feed produces.
    /// </summary>
    private static void DrainFully(FakeOpenAlDevice device)
    {
        for (int i = 0; i < BufferPoolSize + PreBufferCount; i++)
            device.AdvancePlayback(BlockDuration);
    }

    // "1000x4800 1001x4800 ..." — a burst's shape at a glance, so a failure says which block
    // went missing or arrived twice instead of printing tens of thousands of samples.
    private static string DescribeRuns(IReadOnlyList<short> samples)
    {
        if (samples.Count == 0)
            return "nothing played";
        var runs = new List<string>();
        int i = 0;
        while (i < samples.Count)
        {
            short value = samples[i];
            int length = 0;
            while (i < samples.Count && samples[i] == value)
            {
                length++;
                i++;
            }
            runs.Add($"{value}x{length}");
        }
        return string.Join(" ", runs);
    }

    private static int IndexOfFirst(IReadOnlyList<AlCall> calls, string name)
    {
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i].Name == name)
                return i;
        }
        return -1;
    }
}
