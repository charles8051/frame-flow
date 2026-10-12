// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Audio.OpenAL;
using FrameFlow.Audio.TestKit;
using FrameFlow.Media;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// <see cref="OpenAlAudioSink.DrainAsync"/> against <see cref="FakeOpenAlDevice"/> (#609).
/// </summary>
/// <remarks>
/// <para>
/// Playback and time are both virtual: the device plays only when a test advances it, and the
/// drain's wait and stall limit run on a <see cref="FakeTimeProvider"/>. Everything the sink does
/// up to its first wait is synchronous against the fake, so the state a test reads straight after
/// calling <see cref="OpenAlAudioSink.DrainAsync"/> is the state the drain left before it waited.
/// </para>
/// <para>
/// The assertions are on what the device played, as in <see cref="OpenAlAudioSinkFakeDeviceTests"/>,
/// because a drain that returned early leaves every counter looking right and the end unheard.
/// </para>
/// </remarks>
public sealed class OpenAlAudioSinkDrainTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    // One block is one OpenAL buffer: 4800 scalars, the coalesce target, 50 ms of stereo.
    private const int ScalarsPerBlock = 4800;
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(50);

    // The sink's drain geometry, mirrored so the assertions can name it.
    private static readonly TimeSpan DrainPollSlice = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DrainStallLimit = TimeSpan.FromSeconds(2);

    // ── #609: the end of the stream is heard ────────────────────────────────

    /// <summary>
    /// The drain hands over the partial block the coalesce gate held back, and completes only
    /// once the device has played it.
    /// </summary>
    /// <remarks>
    /// This is the cut #609 measured: the sink queues up to a pool of buffers ahead of the play
    /// cursor and holds the tail of the stream in staging, and a deactivation straight after the
    /// last <see cref="OpenAlAudioSink.PresentAsync"/> dropped both.
    /// </remarks>
    [Fact]
    public async Task Drain_HandsOverTheHeldBackRemainder_AndCompletesOnceTheDeviceHasPlayedIt()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: 6, value: 100);
        const short tail = 777;
        await PresentAsync(sink, scalars: ScalarsPerBlock / 2, value: tail, pts: 6 * BlockDuration);
        Assert.Equal(6, QueueCalls(device));

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();

        Assert.False(
            drain.IsCompleted,
            "The drain completed while the device still held 325 ms it had not played.\n"
                + device.DescribeCalls()
        );
        Assert.Equal(7, QueueCalls(device));

        Play(device, time, 6 * BlockDuration + BlockDuration / 2);
        await AssertCompletesAsync(drain, "The device played everything, but the drain did not end.");

        Assert.True(device.AllQueuedAudioPlayed);
        Assert.Equal(6 * ScalarsPerBlock + ScalarsPerBlock / 2, device.PlayedSamples.Count);
        Assert.Equal(tail, device.PlayedSamples[^1]);
    }

    /// <summary>
    /// A stream shorter than the pre-buffer depth never reaches the depth that starts the
    /// source. The drain starts it, or the stream is never heard at all.
    /// </summary>
    [Fact]
    public async Task Drain_StartsAStreamTooShortToReachThePreBufferDepth()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: 2, value: 300);
        Assert.DoesNotContain("SourcePlay", device.CallNames);

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted, "The drain completed over a source that never played.");

        Play(device, time, 2 * BlockDuration);
        await AssertCompletesAsync(drain, "The short stream played out, but the drain did not end.");

        Assert.Equal(2 * ScalarsPerBlock, device.PlayedSamples.Count);
    }

    /// <summary>
    /// A drained sink reads the end of the stream on its clock, and takes more audio as a fresh
    /// stream rather than as a source that starved.
    /// </summary>
    [Fact]
    public async Task Drain_LeavesTheClockAtTheEnd_AndTheSinkReadyForMore()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: 8, value: 100);
        var drain = sink.DrainAsync(CancellationToken.None).AsTask();
        Play(device, time, 8 * BlockDuration);
        await AssertCompletesAsync(drain, "The device played everything, but the drain did not end.");

        Assert.Equal(8 * BlockDuration, sink.GetPlaybackTime());

        // A loop restart or the next item can follow without an activation in between.
        await PushBlocksAsync(sink, blocks: 6, value: 200, firstBlock: 8);
        Play(device, time, 6 * BlockDuration);

        Assert.Equal(0, sink.UnderrunCount);
        Assert.Equal(6 * ScalarsPerBlock, device.PlayedSamples.Count(s => s == 200));
    }

    /// <summary>
    /// A device that stops playing does not hold the drain forever. It gives up once the device
    /// has made no progress for the stall limit, with the audio it could not play still queued.
    /// </summary>
    [Fact]
    public async Task Drain_GivesUpOnADeviceThatStopsPlaying()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 8, value: 100);

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted);

        // The device never moves.
        time.Advance(DrainStallLimit);
        await AssertCompletesAsync(
            drain,
            "The device played nothing for the whole stall limit, and the drain is still waiting."
        );

        Assert.False(device.AllQueuedAudioPlayed);
    }

    /// <summary>
    /// A pause is the caller holding playback, not the device failing: a drain over a paused
    /// sink waits as long as the pause lasts, and finishes once it resumes.
    /// </summary>
    /// <remarks>
    /// The negative half needs a happens-before. Each step waits for the drain loop to read the
    /// device again after the time advance, and a loop that had given up would not read it
    /// again, so reaching the next read is the proof that the previous one decided to wait.
    /// </remarks>
    [Fact]
    public async Task Drain_WaitsOutAPause_HoweverLongItLasts()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 8, value: 100);
        Play(device, time, 2 * BlockDuration);
        await sink.PauseAsync(CancellationToken.None);

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();

        // Five stall limits of pause, then one more slice to see the last of them decided.
        for (int i = 0; i < 5; i++)
            await AdvanceAndAwaitTheNextReadAsync(device, time, DrainStallLimit, drain);
        await AdvanceAndAwaitTheNextReadAsync(device, time, DrainPollSlice, drain);

        await sink.ResumeAsync(CancellationToken.None);
        Play(device, time, 6 * BlockDuration);
        await AssertCompletesAsync(drain, "The resumed device played everything, but the drain did not end.");

        Assert.True(device.AllQueuedAudioPlayed);
        Assert.Equal(8 * ScalarsPerBlock, device.PlayedSamples.Count);
    }

    [Fact]
    public async Task Drain_EndsWithCancellation_WhenItsTokenIsCancelled()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 8, value: 100);

        using var cts = new CancellationTokenSource();
        var drain = sink.DrainAsync(cts.Token).AsTask();
        Assert.False(drain.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
    }

    [Fact]
    public async Task Drain_EndsQuietly_WhenTheSinkIsDisposed()
    {
        var device = new FakeOpenAlDevice();
        var time = new FakeTimeProvider();
        var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 8, value: 100);

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted);

        await sink.DisposeAsync();

        await AssertCompletesAsync(drain, "The sink was disposed, but its drain is still waiting.");
        Assert.True(drain.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Drain_OnASinkNeverActivated_CompletesAtOnce()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: new FakeTimeProvider());

        var drain = sink.DrainAsync(CancellationToken.None);

        Assert.True(drain.IsCompletedSuccessfully);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int QueueCalls(FakeOpenAlDevice device) =>
        device.Calls.Count(c => c.Name == "SourceQueueBuffers");

    private static async Task PushBlocksAsync(OpenAlAudioSink sink, int blocks, short value, int firstBlock = 0)
    {
        for (int i = 0; i < blocks; i++)
            await PresentAsync(sink, ScalarsPerBlock, value, (firstBlock + i) * BlockDuration);
    }

    private static ValueTask PresentAsync(OpenAlAudioSink sink, int scalars, short value, TimeSpan pts) =>
        sink.PresentAsync(
            PcmAudioBuffer.Create(
                scalars,
                Rate,
                Channels,
                pts,
                value,
                static (span, v) =>
                {
                    span.Fill(v);
                    return span.Length;
                }
            ),
            CancellationToken.None
        );

    /// <summary>Plays <paramref name="duration"/> and lets the same time pass on the sink's clock.</summary>
    private static void Play(FakeOpenAlDevice device, FakeTimeProvider time, TimeSpan duration)
    {
        device.AdvancePlayback(duration);
        time.Advance(duration);
    }

    /// <summary>
    /// Advances time and waits for the drain loop to read the device afterwards. A loop that
    /// had decided to stop never reads it again, and the bounded wait fails the test.
    /// </summary>
    private static async Task AdvanceAndAwaitTheNextReadAsync(
        FakeOpenAlDevice device,
        FakeTimeProvider time,
        TimeSpan by,
        Task drain
    )
    {
        var read = device.WhenCalledAsync("GetSourceProperty");
        time.Advance(by);
        var first = await Task.WhenAny(read, drain).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(
            first == read,
            "The drain ended while the sink was paused, instead of waiting for it to resume."
        );
    }

    // The timeout only bounds a failure: a working drain completes as soon as the device or
    // the clock releases it, and a broken one never does.
    private static async Task AssertCompletesAsync(Task task, string because)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail(because);
        }
    }
}
