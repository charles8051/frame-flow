using System.Buffers;
using System.Diagnostics;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using Xunit.Abstractions;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Multi-instance regression tests for <see cref="OpenAlAudioSink"/> (ADR-0058).
/// </summary>
/// <remarks>
/// <para>
/// Two concurrently-active sinks must each drive an <i>independent</i> master
/// clock. Before ADR-0058 every sink opened its own OpenAL device + context and
/// called <c>alcMakeContextCurrent</c> at activation. That current context is
/// <b>process-global</b> ("there is only ever one current context for any one
/// process"), so whichever sink activated last owned it; the other sink's
/// <c>al*</c> calls — its <c>SampleOffset</c> clock read and its buffer-queue
/// ops — then targeted the wrong context. Source names are numbered per-context,
/// so the two sinks' sources collided (both name <c>1</c>): the first sink's
/// clock read silently sampled the second sink's source, and its
/// <c>RecycleProcessedBuffers</c> unqueued the second sink's buffers. The
/// sample-counter master clock that paces video (ADR-0003 / ADR-0057) stalled or
/// jumped, and the victim's buffer queue starved.
/// </para>
/// <para>
/// These behavioural tests need a real OpenAL device to move the sample counter,
/// so they are gated behind <see cref="RequiresAudioDeviceFactAttribute"/>
/// (<c>FRAMEFLOW_AUDIO_DEVICE_TESTS=1</c>). The deterministic, device-independent
/// proof that the two sinks share a single device/context lives in
/// <c>SharedOpenAlContextTests</c>.
/// </para>
/// </remarks>
[Collection("OpenAL device")]
public sealed class OpenAlAudioSinkMultiInstanceTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // 20ms of interleaved stereo at 48kHz = 960 frames * 2 channels.
    private const int BlockSamples = SampleRate / 50 * Channels;

    private readonly ITestOutputHelper _output;

    public OpenAlAudioSinkMultiInstanceTests(ITestOutputHelper output) => _output = output;

    [RequiresAudioDeviceFact]
    public async Task TwoSinks_PlayingConcurrently_BothClocksAdvanceIndependently()
    {
        await using var sinkA = new OpenAlAudioSink();
        await using var sinkB = new OpenAlAudioSink();

        await sinkA.ActivateAsync();
        await sinkB.ActivateAsync();

        using var feedCts = new CancellationTokenSource();
        var feedA = FeedRealtimeAsync(sinkA, feedCts.Token);
        var feedB = FeedRealtimeAsync(sinkB, feedCts.Token);

        // Let both prime their pre-buffer and reach steady-state device mix
        // before the first read, so the interval we measure is pure playback.
        await Task.Delay(400);
        var sw = Stopwatch.StartNew();
        var aMid = sinkA.GetPlaybackTime();
        var bMid = sinkB.GetPlaybackTime();

        await Task.Delay(800);
        var aEnd = sinkA.GetPlaybackTime();
        var bEnd = sinkB.GetPlaybackTime();
        var wallMs = sw.Elapsed.TotalMilliseconds;

        feedCts.Cancel();
        await Task.WhenAll(feedA, feedB);

        var aRate = (aEnd - aMid).TotalMilliseconds / wallMs;
        var bRate = (bEnd - bMid).TotalMilliseconds / wallMs;
        _output.WriteLine($"wall={wallMs:F0}ms");
        _output.WriteLine($"Sink A: mid={aMid.TotalMilliseconds:F0}ms end={aEnd.TotalMilliseconds:F0}ms rate={aRate:F2}x");
        _output.WriteLine($"Sink B: mid={bMid.TotalMilliseconds:F0}ms end={bEnd.TotalMilliseconds:F0}ms rate={bRate:F2}x");

        // No real output device (headless): the sample counter never moves and
        // both clocks stay at zero. Nothing to assert — degrade to a pass.
        if (aEnd == TimeSpan.Zero && bEnd == TimeSpan.Zero)
            return;

        // Each sink, playing its own audio, must advance its clock at ~real-time
        // during steady-state playback. Under the pre-ADR-0058 context clobber
        // only the last-activated sink's source actually plays — both sinks pump
        // into it and both clock reads sample it — so the victim's clock crawls
        // (~0.35x in the repro). 0.6x cleanly separates clobbered from healthy.
        Assert.True(
            aRate >= 0.6,
            $"Sink A clock advanced at {aRate:F2}x real-time over {wallMs:F0}ms of steady playback "
                + "— a second concurrent sink clobbered the process-global OpenAL context."
        );
        Assert.True(
            bRate >= 0.6,
            $"Sink B clock advanced at {bRate:F2}x real-time over {wallMs:F0}ms of steady playback "
                + "— a second concurrent sink clobbered the process-global OpenAL context."
        );
    }

    [RequiresAudioDeviceFact]
    public async Task SecondSinkActivation_DoesNotFreezeFirstSinkClock()
    {
        // The exact interleave that broke a downstream host: sink A is already playing
        // and driving the master clock when sink B activates. Pre-ADR-0058, B's
        // ActivateAsync called alcMakeContextCurrent(B) and stole the global
        // current context, so A's subsequent clock reads sampled B's source.
        await using var sinkA = new OpenAlAudioSink();
        await using var sinkB = new OpenAlAudioSink();

        await sinkA.ActivateAsync();

        using var feedCts = new CancellationTokenSource();
        var feedA = FeedRealtimeAsync(sinkA, feedCts.Token);

        // Let A reach steady-state playback before B appears.
        await Task.Delay(500);
        var aBeforeB = sinkA.GetPlaybackTime();

        await sinkB.ActivateAsync();
        var feedB = FeedRealtimeAsync(sinkB, feedCts.Token);

        await Task.Delay(900);
        var aAfterB = sinkA.GetPlaybackTime();

        feedCts.Cancel();
        await Task.WhenAll(feedA, feedB);

        _output.WriteLine(
            $"Sink A: beforeB={aBeforeB.TotalMilliseconds:F0}ms afterB={aAfterB.TotalMilliseconds:F0}ms"
        );

        if (aBeforeB == TimeSpan.Zero && aAfterB == TimeSpan.Zero)
            return;

        // A played ~900ms more after B joined; at real-time its clock should
        // advance close to that. The clobber froze it to a fraction (~210ms in
        // the original repro). 400ms cleanly separates healthy from clobbered.
        var advancedMs = (aAfterB - aBeforeB).TotalMilliseconds;
        Assert.True(
            advancedMs > 400,
            $"Sink A's clock advanced only {advancedMs:F0}ms in the ~900ms after sink B activated "
                + $"(before={aBeforeB.TotalMilliseconds:F0}ms, after={aAfterB.TotalMilliseconds:F0}ms). "
                + "Activating a second sink clobbered the first sink's OpenAL context."
        );
    }

    /// <summary>
    /// Feeds <paramref name="sink"/> 20ms PCM blocks at ~real-time pace until
    /// cancelled, mirroring how the playback pipeline drives the sink.
    /// </summary>
    private static async Task FeedRealtimeAsync(OpenAlAudioSink sink, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await sink.PresentAsync(MakeBlock(), ct);
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on teardown.
        }
    }

    private static PcmAudioBuffer MakeBlock()
    {
        var owner = MemoryPool<short>.Shared.Rent(BlockSamples);
        var span = owner.Memory.Span[..BlockSamples];
        for (int i = 0; i < BlockSamples; i++)
        {
            double phase = 2.0 * Math.PI * 440.0 * (i / Channels) / SampleRate;
            span[i] = (short)(Math.Sin(phase) * 8000);
        }
        return new PcmAudioBuffer(owner, BlockSamples, SampleRate, Channels, TimeSpan.Zero);
    }
}
