using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A decode that fails on corrupt data throws a <see cref="MediaException"/> carrying FFmpeg's
/// error code, the stage that failed and <see cref="ErrorCategory.Decode"/> (#755). The fixture is
/// the corpus clip with 4 KB in its middle overwritten with <c>0xFF</c>, written per test.
/// </summary>
public sealed class CorruptStreamFailureTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private const int InvalidData = -1094995529; // AVERROR_INVALIDDATA
    private const int CorruptLength = 4096;

    public CorruptStreamFailureTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ADecodeOfCorruptData_ThrowsTheCodecsErrorCode()
    {
        var clip = TestEnvironment.GetCorpusFile(Clip);
        Assert.True(clip is not null, $"Corpus is present but {Clip} is missing.");
        var path = Path.Combine(Path.GetTempPath(), $"frameflow-corrupt-{Guid.NewGuid():N}.mp4");
        var bytes = await File.ReadAllBytesAsync(clip);
        Array.Fill(bytes, (byte)0xFF, bytes.Length / 2, CorruptLength);
        await File.WriteAllBytesAsync(path, bytes);

        try
        {
            var failure = await Assert.ThrowsAsync<MediaException>(() => DecodeAllAsync(path));

            Assert.Equal(
                (ErrorCategory.Decode, InvalidData, MediaFailureStage.SendPacket),
                (failure.Category, failure.ErrorCode, failure.Stage)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task DecodeAllAsync(string path)
    {
        await using var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path));
        var demux = (DemuxSession)session;
        var videoIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;

        await using var video = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );

        // A failed decode stops draining the queue, so the feed is cancelled rather than left
        // waiting on a full one.
        using var stopFeed = new CancellationTokenSource();
        var feed = Task.Run(async () =>
        {
            nint readPacket = FFAvCodec.av_packet_alloc();
            try
            {
                while (FFAvFormat.av_read_frame(demux.FormatContextPtr, readPacket) >= 0)
                {
                    if (new AvPacketAccessor(readPacket).StreamIndex == videoIndex)
                    {
                        nint clone = FFAvCodec.av_packet_alloc();
                        FFAvCodec.av_packet_ref(clone, readPacket);
                        await video.SendPacketAsync(clone, stopFeed.Token);
                    }

                    FFAvCodec.av_packet_unref(readPacket);
                }
            }
            finally
            {
                FFAvCodec.av_packet_free(ref readPacket);
                video.CompletePacketQueue();
            }
        });

        try
        {
            await foreach (var frame in video.DecodeAsync())
                frame.Dispose();
        }
        finally
        {
            await stopFeed.CancelAsync();
            try
            {
                await feed;
            }
            catch (OperationCanceledException) { }
        }
    }
}
