namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="DemuxSession.SeekAsync"/> over real containers (#483, #495): after it, the first
/// packet of the video stream is the last keyframe at or before the position. On the MPEG-TS
/// clip, the container's own seek to a mid-GOP position lands on a packet that is not a keyframe.
/// </summary>
public sealed class DemuxSeekTests : IClassFixture<FfmpegBootstrapFixture>
{
    public DemuxSeekTests(FfmpegBootstrapFixture _) { }

    /// <summary>MPEG-TS with a keyframe a second, at 0, 1 and 2 s in media time, at 24 fps.</summary>
    private const string Ts = "test-video-h264-start-offset.ts";

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(Ts, 1.52, 1.0)]
    [InlineData(Ts, 1.2, 1.0)]
    [InlineData(Ts, 1.0, 1.0)]
    [InlineData(Ts, 0.99, 0.0)]
    [InlineData(Ts, 0.0, 0.0)]
    [InlineData(Ts, 2.9, 2.0)]
    [InlineData(Ts, 10.0, 2.0)]
    [InlineData("test-video-h265-yuv420p.mp4", 2.8, 2.6666666)]
    [InlineData("test-video-h265-yuv420p.mp4", 2.0, 0.0)]
    [InlineData("test-av-h264-aac.mp4", 1.5, 0.0)]
    public async Task TheFirstVideoPacketAfterIt_IsTheLastKeyframeAtOrBeforeThePosition(
        string clip,
        double position,
        double keyframe
    )
    {
        await using var session = await OpenAsync(clip);
        var video = session.MediaInfo.VideoStreams[0].StreamIndex;

        await session.SeekAsync(TimeSpan.FromSeconds(position));

        var first = await FirstPacketAsync(session, video);
        Assert.True(first.IsKeyFrame, $"The first packet, at {first.Pts}, is not a keyframe.");
        Assert.Equal(keyframe, first.Pts.TotalSeconds, 3);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task EachCountsAsOneSeek_AndThePacketsItReadsAreNotCounted()
    {
        await using var session = await OpenAsync(Ts);

        // The second searches from past the end, so its probes read to the end of the source.
        await session.SeekAsync(TimeSpan.FromSeconds(1.52));
        await session.SeekAsync(TimeSpan.FromSeconds(10));

        var diagnostics = session.GetDiagnostics();
        Assert.Equal(2, diagnostics.SeeksPerformed);
        Assert.Equal(0, diagnostics.PacketsRead);
        Assert.False(diagnostics.EndOfStreamReached);
    }

    private static async Task<DemuxSession> OpenAsync(string clip)
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(file);
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
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
