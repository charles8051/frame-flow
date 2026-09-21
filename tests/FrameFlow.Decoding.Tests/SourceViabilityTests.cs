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

    private static AudioStreamInfo Audio(
        string codec = "aac",
        int sampleRate = 44100,
        int channels = 2
    ) => new(1, codec, sampleRate, channels);

    // -------------------------------------------------------------------------
    // Classify
    // -------------------------------------------------------------------------

    [Fact]
    public void AnimatedWebpShapeIsNotPlayable()
    {
        // What the bundled FFmpeg hands back for an animated .webp.
        var info = Info(video: [Video("webp", 0, 0)]);

        Assert.Equal(SourceViabilityKind.NoResolvedStreams, SourceViability.Classify(info));
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
    public void ResolvedAudioCarriesASourceWhoseVideoDidNotResolve()
    {
        // An unsupported video track inside an otherwise fine container. Failing the whole
        // load over it would be a regression, so audio alone is enough.
        var info = Info(video: [Video("webp", 0, 0)], audio: [Audio()]);

        Assert.Equal(SourceViabilityKind.Playable, SourceViability.Classify(info));
    }

    [Fact]
    public void ResolvedVideoCarriesASourceWhoseAudioDidNotResolve()
    {
        var info = Info(video: [Video("h264", 1920, 1080)], audio: [Audio(sampleRate: 0)]);

        Assert.Equal(SourceViabilityKind.Playable, SourceViability.Classify(info));
    }

    [Fact]
    public void DeclaredAudioIsNotPlayableWhenItsParametersDidNotResolve()
    {
        // The symmetric case of the animated-WebP shape: streams declared, nothing resolved.
        // Declaration alone is not proof of playability on either side.
        var info = Info(video: [Video("webp", 0, 0)], audio: [Audio(sampleRate: 0, channels: 0)]);

        Assert.Equal(SourceViabilityKind.NoResolvedStreams, SourceViability.Classify(info));
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
        // Distinct from NoResolvedStreams so the error message can say which happened.
        Assert.Equal(SourceViabilityKind.NoStreams, SourceViability.Classify(Info()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(320, 0)]
    [InlineData(0, 240)]
    [InlineData(-1, 240)]
    [InlineData(320, -1)]
    public void EitherVideoDimensionMissingLeavesTheStreamUnresolved(int width, int height)
    {
        var info = Info(video: [Video("webp", width, height)]);

        Assert.Equal(SourceViabilityKind.NoResolvedStreams, SourceViability.Classify(info));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(44100, 0)]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(44100, -1)]
    public void EitherAudioParameterMissingLeavesTheStreamUnresolved(int sampleRate, int channels)
    {
        var info = Info(audio: [Audio(sampleRate: sampleRate, channels: channels)]);

        Assert.Equal(SourceViabilityKind.NoResolvedStreams, SourceViability.Classify(info));
    }

    [Fact]
    public void ClassifyRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SourceViability.Classify(null!));
    }

    // -------------------------------------------------------------------------
    // DescribeStreams / DescribeUnusableStreams
    // -------------------------------------------------------------------------

    [Fact]
    public void DescribeNamesTheCodecAndTheMissingParameters()
    {
        var info = Info(video: [Video("webp", 0, 0)]);

        Assert.Equal("video webp 0x0", SourceViability.DescribeStreams(info));
    }

    [Fact]
    public void DescribeJoinsVideoThenAudio()
    {
        var info = Info(video: [Video("h264", 1920, 1080)], audio: [Audio()]);

        Assert.Equal(
            "video h264 1920x1080, audio aac 44100Hz 2ch",
            SourceViability.DescribeStreams(info)
        );
    }

    [Fact]
    public void DescribeIsEmptyWithoutStreams()
    {
        Assert.Equal(string.Empty, SourceViability.DescribeStreams(Info()));
    }

    [Fact]
    public void OnlyTheUnresolvedStreamsAreWarnedAbout()
    {
        var info = Info(
            video: [Video("h264", 1920, 1080), Video("webp", 0, 0)],
            audio: [Audio(), Audio(codec: "mp3", sampleRate: 0, channels: 0)]
        );

        Assert.Equal(
            "video webp 0x0, audio mp3 0Hz 0ch",
            SourceViability.DescribeUnusableStreams(info)
        );
    }

    [Fact]
    public void AFullyResolvedSourceHasNothingToWarnAbout()
    {
        var info = Info(video: [Video("h264", 1920, 1080)], audio: [Audio()]);

        Assert.Equal(string.Empty, SourceViability.DescribeUnusableStreams(info));
    }

    [Fact]
    public void DescribeUnusableStreamsRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SourceViability.DescribeUnusableStreams(null!));
    }
}
