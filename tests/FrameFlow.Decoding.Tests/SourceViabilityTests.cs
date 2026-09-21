using FrameFlow.Decoding;
using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins <see cref="SourceViability"/>, the guard that stops a container FFmpeg opened but
/// could not resolve from becoming a session that decodes nothing and ends at zero duration
/// (#340).
/// </summary>
/// <remarks>
/// The shape that motivated it is an animated WebP: <c>avformat_find_stream_info</c> logs
/// "Could not find codec parameters" at warning level, returns success, and leaves a video
/// stream at <c>0x0</c>. Every case here is a value, so none of it needs FFmpeg.
/// </remarks>
public sealed class SourceViabilityTests
{
    private static MediaInfo Info(
        IReadOnlyList<VideoStreamInfo>? video = null,
        IReadOnlyList<AudioStreamInfo>? audio = null
    ) =>
        new(
            "test",
            TimeSpan.Zero,
            video ?? Array.Empty<VideoStreamInfo>(),
            audio ?? Array.Empty<AudioStreamInfo>()
        );

    private static VideoStreamInfo Video(string codec, int width, int height) =>
        new(0, codec, width, height, 25.0);

    private static AudioStreamInfo Audio(string codec = "aac") => new(1, codec, 44100, 2);

    // -------------------------------------------------------------------------
    // Classify
    // -------------------------------------------------------------------------

    [Fact]
    public void AnimatedWebpShapeIsNotPlayable()
    {
        // What the bundled FFmpeg hands back for an animated .webp.
        var info = Info(video: [Video("webp", 0, 0)]);

        Assert.Equal(SourceViabilityKind.NoResolvedVideo, SourceViability.Classify(info));
    }

    [Fact]
    public void AStillImageIsPlayable()
    {
        // A still .webp or .png resolves real dimensions, so the guard must not touch it.
        var info = Info(video: [Video("webp", 320, 240)]);

        Assert.Equal(SourceViabilityKind.Playable, SourceViability.Classify(info));
    }

    [Fact]
    public void AudioOnlyIsPlayable()
    {
        // test-audio-only.mp4 has no video stream at all and must keep loading.
        var info = Info(audio: [Audio()]);

        Assert.Equal(SourceViabilityKind.Playable, SourceViability.Classify(info));
    }

    [Fact]
    public void AudioCarriesASourceWhoseVideoDidNotResolve()
    {
        // An unsupported video track inside an otherwise fine container. Failing the whole
        // load over it would be a regression, so audio alone is enough.
        var info = Info(video: [Video("webp", 0, 0)], audio: [Audio()]);

        Assert.Equal(SourceViabilityKind.Playable, SourceViability.Classify(info));
    }

    [Fact]
    public void OneResolvedVideoStreamCarriesTheRest()
    {
        var info = Info(video: [Video("webp", 0, 0), Video("h264", 1920, 1080)]);

        Assert.Equal(SourceViabilityKind.Playable, SourceViability.Classify(info));
    }

    [Fact]
    public void NoStreamsAtAllIsItsOwnVerdict()
    {
        // Distinct from NoResolvedVideo so the error message can say which happened.
        Assert.Equal(SourceViabilityKind.NoStreams, SourceViability.Classify(Info()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(320, 0)]
    [InlineData(0, 240)]
    [InlineData(-1, 240)]
    [InlineData(320, -1)]
    public void EitherDimensionMissingLeavesTheStreamUnresolved(int width, int height)
    {
        var info = Info(video: [Video("webp", width, height)]);

        Assert.Equal(SourceViabilityKind.NoResolvedVideo, SourceViability.Classify(info));
    }

    [Fact]
    public void ClassifyRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SourceViability.Classify(null!));
    }

    // -------------------------------------------------------------------------
    // HasUnusableVideo
    // -------------------------------------------------------------------------

    [Fact]
    public void UnusableVideoIsReportedAlongsidePlayableAudio()
    {
        var info = Info(video: [Video("webp", 0, 0)], audio: [Audio()]);

        Assert.True(SourceViability.HasUnusableVideo(info));
    }

    [Fact]
    public void AFullyResolvedSourceHasNoUnusableVideo()
    {
        var info = Info(video: [Video("h264", 1920, 1080)], audio: [Audio()]);

        Assert.False(SourceViability.HasUnusableVideo(info));
    }

    [Fact]
    public void NoVideoStreamsMeansNoUnusableVideo()
    {
        Assert.False(SourceViability.HasUnusableVideo(Info(audio: [Audio()])));
    }

    // -------------------------------------------------------------------------
    // DescribeVideoStreams
    // -------------------------------------------------------------------------

    [Fact]
    public void DescribeNamesTheCodecAndTheMissingDimensions()
    {
        var info = Info(video: [Video("webp", 0, 0)]);

        Assert.Equal("webp 0x0", SourceViability.DescribeVideoStreams(info));
    }

    [Fact]
    public void DescribeJoinsEveryVideoStream()
    {
        var info = Info(video: [Video("h264", 1920, 1080), Video("webp", 0, 0)]);

        Assert.Equal("h264 1920x1080, webp 0x0", SourceViability.DescribeVideoStreams(info));
    }

    [Fact]
    public void DescribeIsEmptyWithoutVideo()
    {
        Assert.Equal(string.Empty, SourceViability.DescribeVideoStreams(Info()));
    }
}
