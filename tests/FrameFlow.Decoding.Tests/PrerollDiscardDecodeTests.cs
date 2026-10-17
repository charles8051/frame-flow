using System.Security.Cryptography;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A decoder given a run's start skips the non-reference frames before it and decodes every frame
/// from the start on exactly as it would without one (#784).
/// </summary>
/// <remarks>
/// The rule itself is pinned in <see cref="PrerollDiscardTests"/>. These decode real streams in
/// software: one with B-frames, where the frames before the start shrink to the reference frames,
/// and one without, where nothing changes.
/// </remarks>
public sealed class PrerollDiscardDecodeTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string WithBFrames = "test-video-mpeg2-bframes.mp4";
    private const string WithoutBFrames = "test-video-h264-yuv420p.mp4";

    // Mid-GOP in both fixtures: 3 s at 24 fps, keyframes 2 s apart in the MPEG-2 one.
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(1.5);

    private readonly FfmpegBootstrapFixture _fixture;

    public PrerollDiscardDecodeTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task WithBFrames_TheFramesBeforeTheStartAreOnlyTheReferenceFrames()
    {
        _ = _fixture;
        var all = await DecodeAsync(WithBFrames, floor: TimeSpan.Zero);
        var preroll = await DecodeAsync(WithBFrames, Start);

        var allBefore = all.Count(f => f.Pts < Start);
        var prerollBefore = preroll.Count(f => f.Pts < Start);

        // Two B-frames in every three: about a third of the frames before the start are left.
        Assert.InRange(prerollBefore, 1, allBefore / 2);
        Assert.Equal(From(all, Start), From(preroll, Start));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task WithoutBFrames_NothingChanges()
    {
        _ = _fixture;
        var all = await DecodeAsync(WithoutBFrames, floor: TimeSpan.Zero);
        var preroll = await DecodeAsync(WithoutBFrames, Start);

        Assert.Contains(all, f => f.Pts < Start);
        Assert.Equal(all, preroll);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task WithBFrames_ARunWithNoStartDecodesEverything()
    {
        _ = _fixture;
        var all = await DecodeAsync(WithBFrames, floor: TimeSpan.Zero);

        Assert.Equal(72, all.Count);
    }

    private static List<DecodedFrame> From(List<DecodedFrame> frames, TimeSpan start) =>
        [.. frames.Where(f => f.Pts >= start)];

    private readonly record struct DecodedFrame(TimeSpan Pts, string Picture);

    /// <summary>
    /// Decodes the whole fixture in software with <paramref name="floor"/> as the run's start, and
    /// returns every frame's time and a hash of its picture.
    /// </summary>
    private static async Task<List<DecodedFrame>> DecodeAsync(string fixture, TimeSpan floor)
    {
        var file = TestEnvironment.GetCorpusFile(fixture);
        Assert.True(file is not null, $"Corpus is present but {fixture} is missing.");

        await using var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
        var demux = (DemuxSession)session;
        var videoIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;

        await using var video = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );
        video.PrerollFloor = floor;

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
                        await video.SendPacketAsync(clone);
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

        var frames = Task.Run(async () =>
        {
            var decoded = new List<DecodedFrame>();
            await foreach (var frame in video.DecodeAsync())
            {
                using (frame)
                    decoded.Add(new DecodedFrame(frame.Pts, Hash(frame)));
            }
            return decoded;
        });

        await Task.WhenAll(feed, frames).WaitAsync(TimeSpan.FromSeconds(60));
        return await frames;
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
