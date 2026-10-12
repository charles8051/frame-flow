// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Audio.OpenAL;
using FrameFlow.Audio.TestKit;
using FrameFlow.Media;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// <see cref="OpenAlAudioSink.DrainAsync"/> against <see cref="FakeOpenAlDevice"/> (#609), and the
/// sink giving its device back when the process exits (#633).
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
    /// The negative half needs a happens-before. Each advance fires the wait the drain loop is
    /// parked on, and the loop then reads the device and decides. Arming its next wait is the
    /// proof that it decided to keep waiting, and a loop that had given up would arm nothing.
    /// It is also what makes the next advance land: an advance made before the loop armed its
    /// next wait would pass under a timer that did not exist yet.
    /// </remarks>
    [Fact]
    public async Task Drain_WaitsOutAPause_HoweverLongItLasts()
    {
        var device = new FakeOpenAlDevice();
        var time = new ArmSignallingTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 8, value: 100);
        Play(device, time, 2 * BlockDuration);
        await sink.PauseAsync(CancellationToken.None);

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();

        // Five stall limits of pause.
        for (int i = 0; i < 5; i++)
        {
            await AdvanceAndAwaitTheNextWaitAsync(
                time,
                DrainStallLimit,
                drain,
                "The drain ended while the sink was paused, instead of waiting for it to resume."
            );
        }

        await sink.ResumeAsync(CancellationToken.None);
        Play(device, time, 6 * BlockDuration);
        await AssertCompletesAsync(drain, "The resumed device played everything, but the drain did not end.");

        Assert.True(device.AllQueuedAudioPlayed);
        Assert.Equal(8 * ScalarsPerBlock, device.PlayedSamples.Count);
    }

    /// <summary>
    /// A sink paused before its source started stays silent through the drain. Neither the
    /// remainder the drain hands over nor the drain itself starts the source; the resume does.
    /// </summary>
    /// <remarks>
    /// Pausing a source that has not started leaves it in <c>AL_INITIAL</c> (OpenAL 1.1 §4.3.6),
    /// so the device alone cannot tell this sink from one whose stream ended too soon to start.
    /// Three blocks sit below the pre-buffer depth, and the half block still in staging is the
    /// upload that would carry the queue to it. Everything up to the drain's first wait is
    /// synchronous against the fake, so the first assertion reads what that pass did.
    /// </remarks>
    [Fact]
    public async Task Drain_KeepsASinkPausedBeforeItsSourceStarted_SilentUntilItResumes()
    {
        var device = new FakeOpenAlDevice();
        var time = new ArmSignallingTimeProvider();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: time);
        await sink.ActivateAsync(CancellationToken.None);

        await PushBlocksAsync(sink, blocks: 3, value: 300);
        const short tail = 777;
        await PresentAsync(sink, scalars: ScalarsPerBlock / 2, value: tail, pts: 3 * BlockDuration);
        await sink.PauseAsync(CancellationToken.None);

        var drain = sink.DrainAsync(CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted, "The drain completed over a paused sink that had played nothing.");
        Assert.True(
            !device.CallNames.Contains("SourcePlay"),
            "The drain started the source of a paused sink.\n" + device.DescribeCalls()
        );

        // A whole stall limit of pause: the drain neither starts the source nor gives up.
        await AdvanceAndAwaitTheNextWaitAsync(
            time,
            DrainStallLimit,
            drain,
            "The drain gave up on a paused sink, instead of waiting for it to resume."
        );
        Assert.True(
            !device.CallNames.Contains("SourcePlay"),
            "The drain started the source of a paused sink.\n" + device.DescribeCalls()
        );

        await sink.ResumeAsync(CancellationToken.None);
        await AdvanceAndAwaitTheNextWaitAsync(
            time,
            DrainPollSlice,
            drain,
            "The drain ended after the resume without playing the stream."
        );
        Assert.Contains("SourcePlay", device.CallNames);

        Play(device, time, 3 * BlockDuration + BlockDuration / 2);
        await AssertCompletesAsync(drain, "The resumed device played everything, but the drain did not end.");

        Assert.True(device.AllQueuedAudioPlayed);
        Assert.Equal(3 * ScalarsPerBlock + ScalarsPerBlock / 2, device.PlayedSamples.Count);
        Assert.Equal(tail, device.PlayedSamples[^1]);
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

    // ── #633: the device is given back at process exit ──────────────────────

    /// <summary>
    /// At exit the sink gives back everything it holds on the device: its source, every buffer
    /// it generated, and its lease, so the device can be closed under it.
    /// </summary>
    [Fact]
    public async Task ProcessExit_TheSinkGivesBackItsSourceBuffersAndLease()
    {
        var device = new FakeOpenAlDevice();
        await using var sink = FakeOpenAlSink.Create(device, timeProvider: new FakeTimeProvider());
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 6, value: 100);

        device.ExitProcess();

        Assert.Equal(0, device.LiveSources);
        Assert.Equal(0, device.LiveBuffers);
        Assert.Equal(0, device.LeasesOutstanding);
    }

    /// <summary>
    /// After the exit took its device, the sink is inert, as it is when no device could be
    /// opened: nothing it is asked to do reaches the device, and nothing throws.
    /// </summary>
    [Fact]
    public async Task ProcessExit_LeavesTheSinkInert()
    {
        var device = new FakeOpenAlDevice();
        var sink = FakeOpenAlSink.Create(device, timeProvider: new FakeTimeProvider());
        await sink.ActivateAsync(CancellationToken.None);
        await PushBlocksAsync(sink, blocks: 6, value: 100);

        device.ExitProcess();
        int callsAtExit = device.Calls.Count;

        await PushBlocksAsync(sink, blocks: 6, value: 200, firstBlock: 6);
        await AssertCompletesAsync(
            sink.DrainAsync(CancellationToken.None).AsTask(),
            "A sink with no device waited on a drain."
        );
        await sink.PauseAsync(CancellationToken.None);
        await sink.ResumeAsync(CancellationToken.None);
        sink.Volume = 0.5f;
        _ = sink.GetPlaybackTime();
        await sink.DeactivateAsync(CancellationToken.None);
        await sink.ActivateAsync(CancellationToken.None); // the exiting device refuses the lease
        await PushBlocksAsync(sink, blocks: 6, value: 300);
        await sink.DisposeAsync();

        Assert.True(
            device.Calls.Count == callsAtExit,
            "The sink kept calling a device the exit had taken:\n"
                + string.Join(Environment.NewLine, device.Calls.Skip(callsAtExit))
        );
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
    /// Advances time, and waits for the drain loop to arm its next wait. The advance fires the
    /// wait the loop is parked on; arming the next one proves the observation after the advance
    /// decided to keep waiting. A loop that had decided to stop arms nothing, and the drain
    /// completes instead, which fails the test with <paramref name="because"/>.
    /// </summary>
    private static async Task AdvanceAndAwaitTheNextWaitAsync(
        ArmSignallingTimeProvider time,
        TimeSpan by,
        Task drain,
        string because
    )
    {
        var armed = time.NextTimerCreated;
        time.Advance(by);
        var first = await Task.WhenAny(armed, drain).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(first == armed, because);
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

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that signals when it creates a timer, so a test can wait
    /// for the drain loop to arm its next wait instead of guessing when it has.
    /// </summary>
    /// <remarks>
    /// The signal fires after the base provider has registered the timer, so an advance made once
    /// it completes is guaranteed to fire that timer. Nothing else creates timers on the provider
    /// while a drain runs in these tests: no flush parks on its backpressure wait, because the
    /// pool is never full, and nothing paces against the sink's clock.
    /// </remarks>
    private sealed class ArmSignallingTimeProvider : FakeTimeProvider
    {
        private TaskCompletionSource _next = NewSignal();

        /// <summary>Completes when the first timer created after this was read is armed.</summary>
        public Task NextTimerCreated => Volatile.Read(ref _next).Task;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Exchange(ref _next, NewSignal()).TrySetResult();
            return timer;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
