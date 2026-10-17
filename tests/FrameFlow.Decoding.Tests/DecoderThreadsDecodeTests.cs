using System.Security.Cryptography;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A software decoder opens with the threading <c>DecoderThreading</c> chose, and a threaded decoder
/// still hands back the right frames after a seek (#788).
/// </summary>
/// <remarks>
/// The rule is pinned in <see cref="DecoderThreadingTests"/>. These read the opened codec context
/// and decode a real stream with frames in flight.
/// </remarks>
public sealed class DecoderThreadsDecodeTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string H264 = "test-video-h264-yuv420p.mp4";

    // HEVC with B-frames and keyframes at 0 and 2.667 s, 24 fps. The seek lands mid-GOP.
    private const string Hevc = "test-video-h265-yuv420p.mp4";
    private static readonly TimeSpan SeekTarget = TimeSpan.FromSeconds(2.9);

    private const int FfThreadFrame = 1;
    private const int FfThreadSlice = 2;

    private readonly FfmpegBootstrapFixture _fixture;

    public DecoderThreadsDecodeTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task ASoftwareContextOpensWithTheRequestedThreads()
    {
        _ = _fixture;
        await using var demux = await OpenDemuxAsync(H264);
        await using var decoder = OpenSoftware(demux, new VideoDecoderOptions { DecoderThreads = 3 });

        Assert.Equal((3, FfThreadFrame | FfThreadSlice, FfThreadFrame), decoder.CodecThreads);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASoftwareContextOpensWithAutomaticThreadsByDefault()
    {
        _ = _fixture;
        await using var demux = await OpenDemuxAsync(H264);
        await using var decoder = OpenSoftware(demux, options: null);

        int expected = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var (count, type, active) = decoder.CodecThreads;
        Assert.Equal(expected, count);
        Assert.Equal(expected > 1 ? FfThreadFrame | FfThreadSlice : 0, type);
        Assert.Equal(expected > 1 ? FfThreadFrame : 0, active);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OneThreadOpensASingleThreadedContext()
    {
        _ = _fixture;
        await using var demux = await OpenDemuxAsync(H264);
        await using var decoder = OpenSoftware(demux, new VideoDecoderOptions { DecoderThreads = 1 });

        Assert.Equal((1, 0, 0), decoder.CodecThreads);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AThreadedDecoderGivesTheRightFramesAfterASeek()
    {
        _ = _fixture;
        var reference = await DecodeAllAsync(Hevc, threads: 1);

        await using var demux = await OpenDemuxAsync(Hevc);
        await using var decoder = OpenSoftware(demux, new VideoDecoderOptions { DecoderThreads = 4 });
        var (_, _, active) = decoder.CodecThreads;
        Assert.Equal(FfThreadFrame, active);

        // Decode a few frames from the start with more packets sent than frames taken, so frames
        // are in flight in the decoder's threads when the seek comes.
        await FeedAsync(demux, decoder, maxPackets: 40, complete: false);
        var before = new List<DecodedFrame>();
        await foreach (var frame in decoder.DecodeAsync())
        {
            using (frame)
                before.Add(new DecodedFrame(frame.Pts, Hash(frame)));
            if (before.Count == 3)
                break;
        }

        decoder.ResetPacketQueue();
        await demux.SeekAsync(SeekTarget);
        decoder.Flush();
        decoder.PrerollFloor = SeekTarget;

        var feed = Task.Run(() => FeedAsync(demux, decoder, maxPackets: int.MaxValue, complete: true));
        var after = await CollectAsync(decoder);
        await feed;

        Assert.Equal(3, before.Count);
        Assert.NotEmpty(after);

        // Nothing decoded before the seek comes out after it, and the first frame is the keyframe
        // the seek landed on.
        Assert.All(after, f => Assert.True(f.Pts >= TimeSpan.FromSeconds(2.6), $"stale frame at {f.Pts}"));
        Assert.Equal(reference.Single(f => f.Pts == after[0].Pts), after[0]);

        // From the target on, every frame is the one a single-threaded decode gives.
        Assert.Equal(reference.First(f => f.Pts >= SeekTarget), after.First(f => f.Pts >= SeekTarget));
        Assert.Equal(From(reference, SeekTarget), From(after, SeekTarget));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AThreadedDecoderDrainsEveryFrameAtTheEnd()
    {
        _ = _fixture;
        var single = await DecodeAllAsync(Hevc, threads: 1);
        var threaded = await DecodeAllAsync(Hevc, threads: 4);

        Assert.Equal(single, threaded);
    }

    private static List<DecodedFrame> From(List<DecodedFrame> frames, TimeSpan start) =>
        [.. frames.Where(f => f.Pts >= start)];

    private readonly record struct DecodedFrame(TimeSpan Pts, string Picture);

    private static async Task<DemuxSession> OpenDemuxAsync(string fixture)
    {
        var file = TestEnvironment.GetCorpusFile(fixture);
        Assert.True(file is not null, $"Corpus is present but {fixture} is missing.");
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
    }

    private static VideoDecoder OpenSoftware(DemuxSession demux, VideoDecoderOptions? options) =>
        VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null,
            options
        );

    private static async Task<List<DecodedFrame>> DecodeAllAsync(string fixture, int threads)
    {
        await using var demux = await OpenDemuxAsync(fixture);
        await using var decoder = OpenSoftware(demux, new VideoDecoderOptions { DecoderThreads = threads });

        var feed = Task.Run(() => FeedAsync(demux, decoder, maxPackets: int.MaxValue, complete: true));
        var frames = await CollectAsync(decoder);
        await feed;
        return frames;
    }

    private static async Task<List<DecodedFrame>> CollectAsync(VideoDecoder decoder)
    {
        var decoded = new List<DecodedFrame>();
        await foreach (var frame in decoder.DecodeAsync())
        {
            using (frame)
                decoded.Add(new DecodedFrame(frame.Pts, Hash(frame)));
        }
        return decoded;
    }

    /// <summary>Sends up to <paramref name="maxPackets"/> video packets from where the demuxer is.</summary>
    private static async Task FeedAsync(DemuxSession demux, VideoDecoder decoder, int maxPackets, bool complete)
    {
        int videoIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        nint readPacket = FFAvCodec.av_packet_alloc();
        try
        {
            int sent = 0;
            while (sent < maxPackets && FFAvFormat.av_read_frame(demux.FormatContextPtr, readPacket) >= 0)
            {
                if (new AvPacketAccessor(readPacket).StreamIndex == videoIndex)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, readPacket);
                    await decoder.SendPacketAsync(clone);
                    sent++;
                }

                FFAvCodec.av_packet_unref(readPacket);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref readPacket);
            if (complete)
                decoder.CompletePacketQueue();
        }
    }

    private static string Hash(IVideoFrame frame)
    {
        var cpu = frame.AsCpu();
        Assert.True(cpu is not null, "A software decoder hands back CPU frames.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(cpu.Value.PlaneY.Span);
        sha.AppendData(cpu.Value.PlaneU.Span);
        sha.AppendData(cpu.Value.PlaneV.Span);
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
