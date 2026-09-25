using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A hardware decoder publishes its pool's size once it knows it, and withdraws it when it goes
/// (#229). FFmpeg creates the pool in <c>get_format</c>, on the first decoded frame, so the size
/// is unknown when <c>Open</c> returns.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class DecodePoolCeilingTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-video-h264-yuv420p.mp4";

    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27)]
    public async Task AHardwareDecoder_PublishesItsPoolSize_AndWithdrawsItWhenDisposed()
    {
        int before = DecodePoolMetrics.Capacity;
        var demux = await OpenAsync();
        await using (demux)
        {
            var decoder = VideoDecoder.Open(
                demux.FormatContextPtr,
                demux.MediaInfo.VideoStreams[0].StreamIndex,
                new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required },
                fixture.Capabilities,
                loggerFactory: null
            );
            decoder.YieldHardwareFrames = true;
            await using (decoder)
            {
                Assert.Equal(0, decoder.GetDiagnostics().HardwarePoolSize);

                await QueueAllAsync(demux, decoder);
                await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();

                // The first frame is out, so get_format has run and the pool exists.
                Assert.True(await frames.MoveNextAsync());
                using (var first = frames.Current)
                    Assert.IsType<GpuVideoFrame>(first);

                int poolSize = decoder.GetDiagnostics().HardwarePoolSize;
                Assert.True(poolSize > 0, "a hardware decoder reported no pool after its first frame");
                Assert.Equal(before + poolSize, DecodePoolMetrics.Capacity);
            }

            Assert.Equal(before, DecodePoolMetrics.Capacity);
        }
    }

    // 27 is AV_CODEC_ID_H264.
    [RequiresHardwareDecodeFact(codecId: 27)]
    public async Task APoolHeldByAFrame_StaysInTheCapacity_AfterItsDecoderIsDisposed()
    {
        // The frame keeps the pool alive, and its lease stays in the outstanding count, so the
        // pool's surfaces must stay in the capacity too, or the two gauges disagree.
        int before = DecodePoolMetrics.Capacity;
        var demux = await OpenAsync();
        GpuVideoFrame held;
        int poolSize;
        await using (demux)
        {
            var decoder = VideoDecoder.Open(
                demux.FormatContextPtr,
                demux.MediaInfo.VideoStreams[0].StreamIndex,
                new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required },
                fixture.Capabilities,
                loggerFactory: null
            );
            decoder.YieldHardwareFrames = true;
            await using (decoder)
            {
                await QueueAllAsync(demux, decoder);
                await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
                Assert.True(await frames.MoveNextAsync());
                held = Assert.IsType<GpuVideoFrame>(frames.Current);
                poolSize = decoder.GetDiagnostics().HardwarePoolSize;
            }
        }

        Assert.Equal(before + poolSize, DecodePoolMetrics.Capacity);

        held.Dispose();
        Assert.Equal(before, DecodePoolMetrics.Capacity);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASoftwareDecoder_ReportsNoPool()
    {
        int before = DecodePoolMetrics.Capacity;
        var demux = await OpenAsync();
        await using (demux)
        {
            var decoder = VideoDecoder.Open(
                demux.FormatContextPtr,
                demux.MediaInfo.VideoStreams[0].StreamIndex,
                new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
                HardwareDecodeCapabilities.Empty,
                loggerFactory: null
            );
            await using (decoder)
            {
                await QueueAllAsync(demux, decoder);
                int frames = 0;
                await foreach (var frame in decoder.DecodeAsync())
                {
                    frame.Dispose();
                    frames++;
                }

                Assert.True(frames > 0);
                Assert.Equal(0, decoder.GetDiagnostics().HardwarePoolSize);
                Assert.Equal(before, DecodePoolMetrics.Capacity);
            }
        }
    }

    private static async Task<DemuxSession> OpenAsync()
    {
        var file = TestEnvironment.GetCorpusFile(Fixture);
        Assert.True(file is not null, $"Corpus is present but {Fixture} is missing.");
        var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
        return (DemuxSession)session;
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
                    // The decoder queue takes ownership, so it gets its own reference.
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
}
