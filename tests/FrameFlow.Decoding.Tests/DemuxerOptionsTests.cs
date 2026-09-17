using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Coverage for the demuxer options and input-format hint on <see cref="IMediaSource"/> (#248).
/// </summary>
/// <remarks>
/// A still image is already a playable queue item, and was not a holdable one: it decodes to
/// one frame with no duration, so it ends as soon as that frame is presented. FFmpeg will pace
/// a still, through the image demuxer's <c>framerate</c>, but nothing in the public surface
/// could ask for it. These are the tests for asking.
/// </remarks>
public sealed class DemuxerOptionsTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Still = "test-still.png";
    private const string Video = "test-video-h264-yuv420p.mp4";

    private static string Require(string name)
    {
        var file = TestEnvironment.GetCorpusFile(name);
        Assert.True(
            file is not null,
            $"Corpus is present but {name} is missing. Re-run scripts/generate-test-corpus.cs."
        );
        return file!;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AStillOpenedWithNoOptions_HasNoDuration()
    {
        // The state the options exist to change, pinned here so the pair below reads as a
        // difference rather than an assertion about PNGs in general. Probing a single image
        // selects a *_pipe demuxer, which reports no duration whatever it is given.
        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(Require(Still)));

        Assert.Equal(TimeSpan.Zero, session.MediaInfo.Duration);
        Assert.Single(session.MediaInfo.VideoStreams);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AStillOpenedAsImage2WithAFramerate_IsAClipOfThatLength()
    {
        // framerate=1/10 makes the one image a stream of one frame whose interval is ten
        // seconds, so the still becomes an ordinary item of a known length. The format hint
        // is what makes the option reachable: on the probed *_pipe demuxer the same
        // framerate is accepted and the duration still comes back empty.
        var source = MediaSource.FromFile(Require(Still)) with
        {
            InputFormat = "image2",
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framerate"] = "1/10",
            },
        };

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(source);

        Assert.Equal(TimeSpan.FromSeconds(10), session.MediaInfo.Duration);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheFramerateSetsTheLength()
    {
        var source = MediaSource.FromFile(Require(Still)) with
        {
            InputFormat = "image2",
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framerate"] = "1/3",
            },
        };

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(source);

        Assert.Equal(TimeSpan.FromSeconds(3), session.MediaInfo.Duration);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnOptionTheDemuxerDoesNotRecognise_IsReportedRatherThanSwallowed()
    {
        // avformat_open_input hands back what it did not consume. Dropping that silently is
        // the failure this is here to prevent: the open succeeds, the caller's option did
        // nothing, and nothing says so.
        var source = MediaSource.FromFile(Require(Video)) with
        {
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["not_a_demuxer_option"] = "1",
            },
        };

        var factory = new DemuxSessionFactory();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await factory.OpenAsync(source)
        );

        Assert.Contains("not_a_demuxer_option", ex.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnOptionMeantForAnotherDemuxer_IsReported()
    {
        // The likelier shape of the same mistake: a real FFmpeg option, valid somewhere,
        // handed to a demuxer that has never heard of it.
        var source = MediaSource.FromFile(Require(Video)) with
        {
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framerate"] = "1/10",
            },
        };

        var factory = new DemuxSessionFactory();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await factory.OpenAsync(source)
        );

        Assert.Contains("framerate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("InputFormat", ex.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnInputFormatNoDemuxerHas_Throws()
    {
        var source = MediaSource.FromFile(Require(Video)) with
        {
            InputFormat = "not_a_demuxer",
        };

        var factory = new DemuxSessionFactory();
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await factory.OpenAsync(source)
        );

        Assert.Contains("not_a_demuxer", ex.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnInputFormatOnItsOwn_OpensAnOrdinaryFile()
    {
        // The hint is not only for stills. Naming the demuxer a probe would have chosen
        // changes nothing, which is what makes it safe to set on a source unconditionally.
        var source = MediaSource.FromFile(Require(Video)) with { InputFormat = "mp4" };

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(source);

        Assert.Equal(TimeSpan.FromSeconds(3), session.MediaInfo.Duration);
        Assert.Single(session.MediaInfo.VideoStreams);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task NoOptionsAndNoHint_OpensExactlyAsBefore()
    {
        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(Require(Video)));

        Assert.Equal(TimeSpan.FromSeconds(3), session.MediaInfo.Duration);
    }
}
