using FrameFlow.Graph;
using System.Collections.Concurrent;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A hardware decoder whose frames are all held waits at its pool budget instead of decoding into
/// an exhausted pool (#383). Without the guard, the same hold fails <c>avcodec_send_packet</c> and
/// the decode enumeration throws (#370). An allowance for held frames (#384) grows the pool, so a
/// caller can hold more than its default spare surfaces.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class DecodePoolGuardHardwareTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-video-h264-yuv420p.mp4";

    // The #370 reproduction's HEVC clip: it ran out at 5 held frames, which is all its default
    // pool leaves spare.
    private const string HevcPressureFixture = "test-portrait-hevc-pressure.mp4";
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(30);

    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27, fixedPool: true)]
    public Task HoldingEveryFrame_ParksTheDecoderAtItsBudget_AndReleasingThemLetsItFinish() =>
        HoldEveryFrameThenReleaseAsync(Fixture, options: null);

    // 172 is AV_CODEC_ID_HEVC.
    [RequiresHardwareDecodeFact(codecId: 172, fixedPool: true)]
    public async Task AnAllowancePastTheDefaultSpareSurfaces_IsHeldWithoutAFault()
    {
        const int held = 8;

        var parked = await HoldEveryFrameThenReleaseAsync(
            HevcPressureFixture,
            new VideoDecoderOptions { HeldHardwareFrames = held }
        );

        Assert.Equal(held, parked.HardwareFrameBudget);
    }

    /// <summary>
    /// A graph whose path holds hardware frames without bound is refused before it runs, and
    /// names the holder (ADR-0081, decision 4). No pool size could cover it.
    /// </summary>
    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27, fixedPool: true)]
    public async Task AGraphThatHoldsWithoutBound_IsRefusedBeforeItRuns()
    {
        await using var demux = await OpenAsync(Fixture);
        await using var decoder = OpenHardware(demux, options: null);
        // Every packet is queued, so a run that is not refused decodes to the end and the
        // assertion fails, rather than waiting for input.
        await QueueAllAsync(demux, decoder);
        var graph = new FrameFlow.Graph.Graph();
        graph
            .Pipeline(decoder.AsSourceNode("video-source"))
            .To(new SinkNode<IVideoFrame>("keeps-everything", (_, _) => ValueTask.CompletedTask));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None).WaitAsync(FailureBound)
        );

        Assert.Contains("keeps-everything", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Holds every frame the decoder yields until it parks, and checks it parked at its budget
    /// rather than faulting. Then releases them and checks every frame arrives.
    /// </summary>
    private async Task<VideoDecoderDiagnosticsSnapshot> HoldEveryFrameThenReleaseAsync(
        string clip,
        VideoDecoderOptions? options
    )
    {
        int expected = await CountSoftwareFramesAsync(clip);
        await using var demux = await OpenAsync(clip);
        await using var decoder = OpenHardware(demux, options);
        await QueueAllAsync(demux, decoder);

        var held = new ConcurrentQueue<IVideoFrame>();
        int released = 0;
        bool keep = true;
        var consumer = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var frame in decoder.DecodeAsync())
            {
                count++;
                if (Volatile.Read(ref keep))
                    held.Enqueue(frame);
                else
                    frame.Dispose();
            }
            return count;
        });

        // Parked: the decoder counted a wait and has handed out exactly its budget. Or the
        // enumeration ended, which is the fault this test exists to catch.
        await SpinUntil(() => decoder.GetDiagnostics().PoolBudgetWaits > 0 || consumer.IsCompleted);
        Assert.False(
            consumer.IsFaulted,
            $"The decoder faulted instead of waiting: {consumer.Exception?.InnerException?.Message}"
        );
        var parked = decoder.GetDiagnostics();
        Assert.True(parked.HardwareFrameBudget > 0);
        Assert.Equal(parked.HardwareFrameBudget, parked.HardwareFramesOutstanding);
        Assert.Equal(parked.HardwareFrameBudget, held.Count);

        Volatile.Write(ref keep, false);
        while (held.TryDequeue(out var frame))
        {
            frame.Dispose();
            released++;
        }

        int decoded = await consumer.WaitAsync(FailureBound);

        Assert.Equal(expected, decoded);
        Assert.Equal(0, decoder.GetDiagnostics().DecodeErrors);
        Assert.Equal(parked.HardwareFrameBudget, released);
        return parked;
    }

    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27, fixedPool: true)]
    public async Task AParkedDecoder_EndsItsEnumerationWhenCancelled()
    {
        await using var demux = await OpenAsync(Fixture);
        await using var decoder = OpenHardware(demux, options: null);
        await QueueAllAsync(demux, decoder);
        using var cts = new CancellationTokenSource();

        var held = new ConcurrentQueue<IVideoFrame>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var frame in decoder.DecodeAsync(cts.Token))
                held.Enqueue(frame);
        });

        await SpinUntil(() => decoder.GetDiagnostics().PoolBudgetWaits > 0 || consumer.IsCompleted);
        Assert.False(consumer.IsCompleted, "the decoder ended before it parked");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.WaitAsync(FailureBound));
        while (held.TryDequeue(out var frame))
            frame.Dispose();
    }

    private VideoDecoder OpenHardware(DemuxSession demux, VideoDecoderOptions? options)
    {
        var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required },
            fixture.Capabilities,
            loggerFactory: null,
            videoOptions: options
        );
        decoder.YieldHardwareFrames = true;
        return decoder;
    }

    private static async Task<int> CountSoftwareFramesAsync(string clip)
    {
        await using var demux = await OpenAsync(clip);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );
        await QueueAllAsync(demux, decoder);
        int count = 0;
        await foreach (var frame in decoder.DecodeAsync())
        {
            frame.Dispose();
            count++;
        }
        return count;
    }

    private static async Task<DemuxSession> OpenAsync(string clip)
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing.");
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
    }

    /// <summary>Queues every video packet and completes the queue.</summary>
    private static async Task QueueAllAsync(DemuxSession demux, VideoDecoder decoder)
    {
        int streamIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (new AvPacketAccessor(read).StreamIndex == streamIndex)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                FFAvCodec.av_packet_unref(read);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
    }

    /// <summary>
    /// Waits for a state the decoder exposes, yielding between checks and involving no duration
    /// (ADR-0072). The bound only fails a test that would otherwise hang.
    /// </summary>
    private static Task SpinUntil(Func<bool> condition) =>
        Task.Run(async () =>
        {
            while (!condition())
                await Task.Yield();
        }).WaitAsync(FailureBound);
}
