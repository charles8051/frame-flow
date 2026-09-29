using FrameFlow.Decoding;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A clip's first video stream as the demuxer reports it: every frame's timestamp and the
/// keyframes', in media time and presentation order. What a pass delivers is checked against it.
/// </summary>
internal sealed record VideoTimeline(IReadOnlyList<TimeSpan> Frames, IReadOnlyList<TimeSpan> Keyframes)
{
    public static async Task<VideoTimeline> ReadAsync(string path)
    {
        await using var demux = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path));
        var video = demux.MediaInfo.VideoStreams[0].StreamIndex;

        var frames = new List<TimeSpan>();
        var keyframes = new List<TimeSpan>();
        while (await demux.ReadPacketAsync() is { } packet)
        {
            if (packet.StreamIndex != video)
                continue;
            frames.Add(packet.Pts);
            if (packet.IsKeyFrame)
                keyframes.Add(packet.Pts);
        }

        frames.Sort();
        keyframes.Sort();
        return new VideoTimeline(frames, keyframes);
    }

    /// <summary>
    /// Asserts <paramref name="delivered"/> is <paramref name="expected"/>, frame for frame. A
    /// decoded frame's timestamp is whole microseconds and a packet's is not, so they are compared
    /// to within a millisecond, far closer than two frames of any fixture.
    /// </summary>
    public static void AssertSame(IEnumerable<TimeSpan> expected, IEnumerable<TimeSpan> delivered) =>
        Assert.Equal(expected, delivered, (a, b) => (a - b).Duration() < TimeSpan.FromMilliseconds(1));
}
