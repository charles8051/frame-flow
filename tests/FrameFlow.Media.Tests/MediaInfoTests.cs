namespace FrameFlow.Media.Tests;

/// <summary>
/// Contract tests for <see cref="MediaInfo"/> and its constituent
/// stream-info records. Per-property `_StoresX` tests collapsed to
/// single round-trip tests per record; record equality is exercised
/// once across the trio (compiler-generated, same shape for all).
/// </summary>
public sealed class MediaInfoTests
{
    [Fact]
    public void MediaInfo_RoundTripsAllProperties()
    {
        var video = new VideoStreamInfo(0, "h264", 1920, 1080, 30.0);
        var audio = new AudioStreamInfo(1, "aac", 44100, 2);
        var duration = TimeSpan.FromMinutes(2.5);

        var info = new MediaInfo("matroska", duration, [video], [audio]);

        Assert.Equal("matroska", info.ContainerName);
        Assert.Equal(duration, info.Duration);
        Assert.Single(info.VideoStreams);
        Assert.Same(video, info.VideoStreams[0]);
        Assert.Single(info.AudioStreams);
        Assert.Same(audio, info.AudioStreams[0]);
    }

    [Fact]
    public void MediaInfo_EmptyStreamLists_AreValid()
    {
        var info = new MediaInfo("mp4", TimeSpan.Zero, [], []);
        Assert.Empty(info.VideoStreams);
        Assert.Empty(info.AudioStreams);
    }

    [Fact]
    public void VideoStreamInfo_RoundTripsAllProperties()
    {
        var v = new VideoStreamInfo(2, "hevc", 3840, 2160, 59.94);
        Assert.Equal(2, v.StreamIndex);
        Assert.Equal("hevc", v.CodecName);
        Assert.Equal(3840, v.Width);
        Assert.Equal(2160, v.Height);
        Assert.Equal(59.94, v.FrameRate);
    }

    [Fact]
    public void AudioStreamInfo_RoundTripsAllProperties()
    {
        var a = new AudioStreamInfo(3, "opus", 48000, 6);
        Assert.Equal(3, a.StreamIndex);
        Assert.Equal("opus", a.CodecName);
        Assert.Equal(48000, a.SampleRate);
        Assert.Equal(6, a.Channels);
    }

    /// <summary>
    /// Records produce value-equality via compiler-synthesised members.
    /// One representative test pins the contract — equal positional
    /// records compare equal, regardless of which record type they are.
    /// </summary>
    [Fact]
    public void StreamInfo_Records_HaveValueEquality()
    {
        var a = new VideoStreamInfo(0, "h264", 1920, 1080, 30.0);
        var b = new VideoStreamInfo(0, "h264", 1920, 1080, 30.0);
        Assert.Equal(a, b);
    }
}
