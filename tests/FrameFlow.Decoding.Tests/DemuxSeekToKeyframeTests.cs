namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="DemuxSession.SeekToKeyframeAsync"/> over real containers (#483): after it, the
/// first packet of the stream is the last keyframe at or before the position. On the MPEG-TS
/// clip, <see cref="DemuxSession.SeekAsync"/> to a mid-GOP position lands on a packet that is not
/// a keyframe.
/// </summary>
public sealed class DemuxSeekToKeyframeTests : IClassFixture<FfmpegBootstrapFixture>
{
    public DemuxSeekToKeyframeTests(FfmpegBootstrapFixture _) { }

    /// <summary>MPEG-TS with a keyframe a second, at 0, 1 and 2 s in media time.</summary>
    private const string Ts = "test-video-h264-start-offset.ts";

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(Ts, 1.52, 1.0)]
    [InlineData(Ts, 1.0, 1.0)]
    [InlineData(Ts, 0.99, 0.0)]
    [InlineData(Ts, 2.9, 2.0)]
    [InlineData(Ts, 10.0, 2.0)]
    [InlineData("test-video-h265-yuv420p.mp4", 2.8, 2.6666666)]
    public async Task TheFirstPacketAfterIt_IsTheLastKeyframeAtOrBeforeThePosition(
        string clip,
        double position,
        double keyframe
    )
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(file);
        await using var session = (DemuxSession)
            await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
        var video = session.MediaInfo.VideoStreams[0].StreamIndex;

        await session.SeekToKeyframeAsync(TimeSpan.FromSeconds(position), video);

        var first = await FirstPacketAsync(session, video);
        Assert.True(first.IsKeyFrame, $"The first packet, at {first.Pts}, is not a keyframe.");
        Assert.Equal(keyframe, first.Pts.TotalSeconds, 3);
    }

    private static async Task<DemuxPacket> FirstPacketAsync(DemuxSession session, int stream)
    {
        while (await session.ReadPacketAsync() is { } packet)
        {
            if (packet.StreamIndex == stream)
                return packet;
        }
        throw new InvalidOperationException("No packet of the stream after the seek.");
    }
}
