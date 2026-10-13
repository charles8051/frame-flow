using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The decoders stamp every frame and buffer they make with the timeline whoever runs them has
/// installed (ADR-0084, decision 2), and <see cref="TimelineId.None"/> until one is.
/// </summary>
/// <remarks>
/// When to install a new one, so that nothing decoded before a break comes out under it, is the
/// session's decision and is pinned with real playback in the integration suite.
/// </remarks>
public sealed class DecoderTimelineStampTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-av-h264-aac.mp4";

    private readonly FfmpegBootstrapFixture _fixture;

    public DecoderTimelineStampTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task EveryFrameAndBuffer_CarriesTheTimelineTheDecodersWereGiven()
    {
        _ = _fixture;
        var timeline = TimelineId.Next();

        var (frames, buffers) = await DecodeAsync(timeline);

        Assert.NotEmpty(frames);
        Assert.NotEmpty(buffers);
        Assert.All(frames, id => Assert.Equal(timeline, id));
        Assert.All(buffers, id => Assert.Equal(timeline, id));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task DecodersGivenNoTimeline_StampNone()
    {
        var (frames, buffers) = await DecodeAsync(timeline: null);

        Assert.NotEmpty(frames);
        Assert.NotEmpty(buffers);
        Assert.All(frames, id => Assert.Equal(TimelineId.None, id));
        Assert.All(buffers, id => Assert.Equal(TimelineId.None, id));
    }

    /// <summary>
    /// Decodes the whole fixture with a software video decoder and an audio decoder, each given
    /// <paramref name="timeline"/> when it is not null, and returns the timeline of every frame and
    /// buffer they made.
    /// </summary>
    private static async Task<(List<TimelineId> Frames, List<TimelineId> Buffers)> DecodeAsync(TimelineId? timeline)
    {
        var file = TestEnvironment.GetCorpusFile(Fixture);
        Assert.True(file is not null, $"Corpus is present but {Fixture} is missing.");

        await using var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
        var demux = (DemuxSession)session;
        var videoIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        var audioIndex = demux.MediaInfo.AudioStreams[0].StreamIndex;

        await using var video = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );
        await using var audio = new AudioDecoder(demux.FormatContextPtr, audioIndex);
        if (timeline is { } id)
        {
            video.Timeline = id;
            audio.Timeline = id;
        }

        // Fed and drained together: the audio queue is shorter than the fixture.
        var feed = Task.Run(async () =>
        {
            nint readPacket = FFAvCodec.av_packet_alloc();
            try
            {
                while (FFAvFormat.av_read_frame(demux.FormatContextPtr, readPacket) >= 0)
                {
                    var stream = new AvPacketAccessor(readPacket).StreamIndex;
                    if (stream == videoIndex || stream == audioIndex)
                    {
                        nint clone = FFAvCodec.av_packet_alloc();
                        FFAvCodec.av_packet_ref(clone, readPacket);
                        if (stream == videoIndex)
                            await video.SendPacketAsync(clone);
                        else
                            await audio.SendPacketAsync(clone);
                    }

                    FFAvCodec.av_packet_unref(readPacket);
                }
            }
            finally
            {
                FFAvCodec.av_packet_free(ref readPacket);
                video.CompletePacketQueue();
                audio.CompletePacketQueue();
            }
        });

        var frames = Task.Run(async () =>
        {
            var ids = new List<TimelineId>();
            await foreach (var frame in video.DecodeAsync())
            {
                ids.Add(frame.Timeline);
                frame.Dispose();
            }
            return ids;
        });

        var buffers = Task.Run(async () =>
        {
            var ids = new List<TimelineId>();
            await foreach (var buffer in audio.DecodeAsync())
            {
                ids.Add(buffer.Timeline);
                buffer.Dispose();
            }
            return ids;
        });

        await Task.WhenAll(feed, frames, buffers).WaitAsync(TimeSpan.FromSeconds(60));
        return (await frames, await buffers);
    }
}
