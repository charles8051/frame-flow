// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Audio.TestKit;
using Silk.NET.OpenAL;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Proves <see cref="FakeOpenAlDevice"/> reproduces the OpenAL 1.1 semantics the
/// sink is written against. A fake that drifts from the driver turns every test
/// built on it into a statement about the fake, so the semantics it models are
/// asserted here against the spec directly (#138).
/// </summary>
public sealed class FakeOpenAlDeviceTests
{
    private const int Rate = 48000;
    private const int FramesPerBuffer = 2400; // 50 ms

    // ── AL_BUFFERS_PROCESSED (§4.3.2) ───────────────────────────────────────

    /// <summary>
    /// OpenAL 1.1 §4.3.2: "On a source in the AL_STOPPED state, all buffers are
    /// processed." This is the mechanism of #133, and it is normative rather than
    /// an OpenAL Soft quirk, so the fake has to reproduce it exactly.
    /// </summary>
    [Fact]
    public void StoppedSource_ReportsEveryQueuedBufferProcessed()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 3);

        device.SourcePlay(source);
        device.SourceStop(source);

        Assert.Equal(AlState.Stopped, device.StateOf(source));
        Assert.Equal(3, device.BuffersProcessed(source));
    }

    /// <summary>
    /// The buffers a stopped source reports processed were never played. Nothing
    /// in the counters distinguishes the two, which is why #133 produced a
    /// session whose whole log looked correct and whose output was silent.
    /// </summary>
    [Fact]
    public void StoppedSource_ReportsProcessed_ForBuffersItNeverPlayed()
    {
        var device = new FakeOpenAlDevice();
        var source = device.GenSource();

        // Queue onto a source that was never started: it is AL_INITIAL.
        QueueBuffer(device, source, amplitude: 111);
        Assert.Equal(0, device.BuffersProcessed(source));

        // Start it, let it starve, then queue more while it is stopped.
        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(200));
        Assert.Equal(AlState.Stopped, device.StateOf(source));

        QueueBuffer(device, source, amplitude: 222);
        QueueBuffer(device, source, amplitude: 333);

        // All three are reported processed, though only the first was ever played.
        Assert.Equal(3, device.BuffersProcessed(source));
        Assert.Contains((short)111, device.PlayedSamples);
        Assert.DoesNotContain((short)222, device.PlayedSamples);
        Assert.DoesNotContain((short)333, device.PlayedSamples);
    }

    /// <summary>
    /// §4.3.2: "On a source in the AL_INITIAL state, no buffers are processed,
    /// all buffers are pending."
    /// </summary>
    [Fact]
    public void InitialSource_ReportsNothingProcessed()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 3);

        Assert.Equal(AlState.Initial, device.StateOf(source));
        Assert.Equal(0, device.BuffersProcessed(source));
        Assert.Equal(3, device.BuffersQueued(source));
    }

    /// <summary>
    /// A playing source reports only the buffers its cursor has fully crossed.
    /// </summary>
    [Fact]
    public void PlayingSource_ReportsOnlyTheBuffersTheCursorCrossed()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 4); // 200 ms

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(120)); // into the third

        Assert.Equal(AlState.Playing, device.StateOf(source));
        Assert.Equal(2, device.BuffersProcessed(source));
        Assert.Equal(4, device.BuffersQueued(source));
    }

    // ── Underrun (§4.3.5) ───────────────────────────────────────────────────

    /// <summary>
    /// §4.3.5: "A playing source will enter the AL_STOPPED state if it completes
    /// playback of the last buffer in its queue."
    /// </summary>
    [Fact]
    public void PlayingSource_ThatCompletesItsQueue_Stops()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 2); // 100 ms

        device.SourcePlay(source);
        Assert.Equal(AlState.Playing, device.StateOf(source));

        device.AdvancePlayback(TimeSpan.FromMilliseconds(150));

        Assert.Equal(AlState.Stopped, device.StateOf(source));
    }

    // ── State transitions (§4.3.6) ──────────────────────────────────────────

    /// <summary>
    /// §4.3.6: "alSourcePlay applied to a AL_STOPPED source will propagate it to
    /// AL_INITIAL then to AL_PLAYING immediately" — so the cursor restarts at the
    /// head of the queue rather than resuming.
    /// </summary>
    [Fact]
    public void Play_FromStopped_RestartsAtTheHeadOfTheQueue()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 2);

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(200)); // starve
        Assert.Equal(AlState.Stopped, device.StateOf(source));
        Assert.Equal(0, SampleOffset(device, source));

        device.SourcePlay(source);
        Assert.Equal(AlState.Playing, device.StateOf(source));
        Assert.Equal(0, SampleOffset(device, source));
    }

    /// <summary>
    /// §4.3.6: "alSourcePlay applied to a AL_PLAYING source will restart the
    /// source from the beginning. It will not affect the configuration, and will
    /// leave the source in AL_PLAYING state, but reset the sampling offset to the
    /// beginning." §4.3.2 states it from the other side: "An alSourceStop,
    /// alSourceRewind, or a second alSourcePlay call will reset the offset to the
    /// beginning of the buffer."
    /// </summary>
    /// <remarks>
    /// A redundant <c>SourcePlay</c> is therefore not a no-op, and a sink path that
    /// issues one replays audio the device has already played. Pinned here so the
    /// fake is not softened into treating it as harmless.
    /// </remarks>
    [Fact]
    public void Play_OnAnAlreadyPlayingSource_RestartsFromTheBeginning()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 4);

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(120));
        Assert.True(SampleOffset(device, source) > 0);
        Assert.Equal(2, device.BuffersProcessed(source));

        device.SourcePlay(source);

        Assert.Equal(AlState.Playing, device.StateOf(source));
        Assert.Equal(0, SampleOffset(device, source));
        Assert.Equal(0, device.BuffersProcessed(source));
    }

    /// <summary>
    /// §4.3.6: "alSourcePlay applied to a AL_PAUSED source will resume processing
    /// using the source state as preserved at the alSourcePause operation."
    /// </summary>
    [Fact]
    public void Play_FromPaused_ResumesWhereItLeftOff()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 4);

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(120));
        device.SourcePause(source);

        int offsetAtPause = SampleOffset(device, source);
        Assert.True(offsetAtPause > 0, "a paused source keeps its offset");

        device.SourcePlay(source);

        Assert.Equal(AlState.Playing, device.StateOf(source));
        Assert.Equal(offsetAtPause, SampleOffset(device, source));
    }

    /// <summary>
    /// §4.3.2 on AL_SAMPLE_OFFSET: "An alSourceStop, alSourceRewind, or a second
    /// alSourcePlay call will reset the offset to the beginning of the buffer."
    /// </summary>
    [Fact]
    public void Stop_ResetsTheSampleOffset()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 4);

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(120));
        Assert.True(SampleOffset(device, source) > 0);

        device.SourceStop(source);

        Assert.Equal(0, SampleOffset(device, source));
    }

    /// <summary>
    /// §4.3.6: alSourceRewind "promotes the source to AL_INITIAL, resetting the
    /// sampling offset to the beginning" from every state.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rewind_ReturnsToInitialWithTheOffsetAtZero(bool pauseFirst)
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 4);

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(120));
        if (pauseFirst)
            device.SourcePause(source);

        device.SourceRewind(source);

        Assert.Equal(AlState.Initial, device.StateOf(source));
        Assert.Equal(0, SampleOffset(device, source));
    }

    /// <summary>
    /// §4.3.6: Pause is "a legal NOP" on an AL_INITIAL and on an AL_STOPPED source.
    /// </summary>
    [Fact]
    public void Pause_IsANopOnInitialAndStopped()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 2);

        device.SourcePause(source);
        Assert.Equal(AlState.Initial, device.StateOf(source));

        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(200));
        Assert.Equal(AlState.Stopped, device.StateOf(source));

        device.SourcePause(source);
        Assert.Equal(AlState.Stopped, device.StateOf(source));
    }

    // ── Queue and unqueue (§4.3.5, §5) ──────────────────────────────────────

    /// <summary>
    /// §4.3.5: unqueueing "will fail with an AL_INVALID_VALUE error if more
    /// buffers are requested than available." The fake throws rather than
    /// silently returning nothing, because in production that failure is invisible
    /// and leaves the sink's pool short.
    /// </summary>
    [Fact]
    public void Unqueue_MoreThanProcessed_Fails()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 3);
        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(60)); // one processed

        Assert.Equal(1, device.BuffersProcessed(source));

        var ex = Assert.Throws<InvalidOperationException>(() => Unqueue(device, source, count: 2));
        Assert.Contains("processed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unqueueing removes from the head, and the offset — which §4.3.2 defines as
    /// "relative to the beginning of all the queued buffers" — shifts back with it.
    /// </summary>
    [Fact]
    public void Unqueue_RemovesFromTheHeadAndShiftsTheOffset()
    {
        var device = new FakeOpenAlDevice();
        var source = Prime(device, buffers: 4);
        device.SourcePlay(source);
        device.AdvancePlayback(TimeSpan.FromMilliseconds(120)); // 2 processed

        int before = SampleOffset(device, source);
        var returned = Unqueue(device, source, count: 2);

        Assert.Equal(2, returned.Length);
        Assert.Equal(2, device.BuffersQueued(source));
        Assert.Equal(before - (2 * FramesPerBuffer), SampleOffset(device, source));
    }

    /// <summary>
    /// §4.3.5: "All buffers in a queue must have the same format and attributes...
    /// If the queue operation fails, the source queue will remain unchanged."
    /// </summary>
    [Fact]
    public void Queue_MixingFormats_Fails()
    {
        var device = new FakeOpenAlDevice();
        var source = device.GenSource();
        QueueBuffer(device, source, amplitude: 1);

        var odd = device.GenBuffer();
        Fill(device, odd, amplitude: 2, format: BufferFormat.Mono16);

        Assert.Throws<InvalidOperationException>(() => Queue(device, source, odd));
        Assert.Equal(1, device.BuffersQueued(source));
    }

    /// <summary>
    /// §5.1: a PENDING buffer "cannot be deleted or changed". Refilling a buffer
    /// that is still queued overwrites audio the device has not finished with.
    /// </summary>
    [Fact]
    public void BufferData_OnAStillQueuedBuffer_Fails()
    {
        var device = new FakeOpenAlDevice();
        var source = device.GenSource();
        var buffer = device.GenBuffer();
        Fill(device, buffer, amplitude: 1);
        Queue(device, source, buffer);

        Assert.Throws<InvalidOperationException>(() => Fill(device, buffer, amplitude: 2));
    }

    /// <summary>§5.2.2: "A buffer which is attached to a source can not be deleted."</summary>
    [Fact]
    public void DeleteBuffer_WhileStillQueued_Fails()
    {
        var device = new FakeOpenAlDevice();
        var source = device.GenSource();
        var buffer = device.GenBuffer();
        Fill(device, buffer, amplitude: 1);
        Queue(device, source, buffer);

        Assert.Throws<InvalidOperationException>(() => device.DeleteBuffer(buffer));
    }

    // ── Device liveness ─────────────────────────────────────────────────────

    /// <summary>
    /// A lease reports the device's connection state live, so a test can take the
    /// endpoint away underneath an open context the way a disconnecting Remote
    /// Desktop session does (#130).
    /// </summary>
    [Fact]
    public void Lease_ReportsDisconnectionLive()
    {
        var device = new FakeOpenAlDevice();
        var lease = device.Lease();

        Assert.NotNull(lease);
        Assert.True(lease.IsConnected);

        device.Connected = false;

        Assert.False(lease.IsConnected);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static uint Prime(FakeOpenAlDevice device, int buffers)
    {
        var source = device.GenSource();
        for (int i = 0; i < buffers; i++)
            QueueBuffer(device, source, amplitude: (short)(i + 1));
        return source;
    }

    private static void QueueBuffer(FakeOpenAlDevice device, uint source, short amplitude)
    {
        var buffer = device.GenBuffer();
        Fill(device, buffer, amplitude);
        Queue(device, source, buffer);
    }

    private static unsafe void Fill(
        FakeOpenAlDevice device,
        uint buffer,
        short amplitude,
        BufferFormat format = BufferFormat.Stereo16
    )
    {
        int channels = format is BufferFormat.Mono16 or BufferFormat.Mono8 ? 1 : 2;
        var samples = new short[FramesPerBuffer * channels];
        Array.Fill(samples, amplitude);
        fixed (short* ptr = samples)
            device.BufferData(buffer, format, ptr, samples.Length * sizeof(short), Rate);
    }

    private static unsafe void Queue(FakeOpenAlDevice device, uint source, uint buffer)
    {
        var one = buffer;
        device.SourceQueueBuffers(source, 1, &one);
    }

    private static unsafe uint[] Unqueue(FakeOpenAlDevice device, uint source, int count)
    {
        var names = new uint[count];
        fixed (uint* ptr = names)
            device.SourceUnqueueBuffers(source, count, ptr);
        return names;
    }

    private static int SampleOffset(FakeOpenAlDevice device, uint source)
    {
        device.GetSourceProperty(source, GetSourceInteger.SampleOffset, out int offset);
        return offset;
    }
}
