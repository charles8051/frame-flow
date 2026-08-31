namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Coverage for <see cref="HardwareDecodeMode.Required"/> failing loudly when no
/// hardware backend can be bound (<c>VideoDecoder.HwAccel.cs:124-126</c>).
/// </summary>
/// <remarks>
/// <para>
/// This path had no test at all. An empty stub in
/// <c>FrameFlow.Integration.Tests.HardwareDecodeIntegrationTests</c> was marked
/// <c>[Fact(Skip = ...)]</c> with a reason asserting that "the old-controller
/// test covers this path until the substrate exposes a capability override
/// hook." There is no old controller, and no such test existed anywhere — so
/// the gap read as covered while nothing exercised it.
/// </para>
/// <para>
/// The skip reason's premise was that the substrate composes the decoder
/// factory internally with no DI seam to inject empty capabilities. That is
/// true of the playback path and irrelevant here: the decision lives in
/// <see cref="VideoDecoder"/>, whose <c>Open</c> takes the options and
/// capabilities directly. The test belongs at the decoder layer, not behind the
/// whole playback stack.
/// </para>
/// <para>
/// Deliberately paired with <see cref="HardwareDecodeCapabilities.Empty"/>
/// rather than a real probe, so the outcome does not depend on what GPU the
/// machine has. With no candidate backends there is nothing to bind on any
/// hardware, which is exactly the condition <c>Required</c> is specified to
/// reject.
/// </para>
/// </remarks>
public sealed class HardwareDecodeRequiredTests : IClassFixture<FfmpegBootstrapFixture>
{
    // Any video stream works: the assertion is about capability negotiation, not
    // about the codec. VP9 is used because it is one of the corpus files the
    // generator reliably produces (see issue #105).
    private const string VideoFixture = "test-video-vp9-yuv420p.webm";

    /// <summary>
    /// Resolves the fixture, failing rather than returning when it is absent.
    /// </summary>
    /// <remarks>
    /// <see cref="RequiresFfmpegAndCorpusFactAttribute"/> gates on the corpus
    /// directory having <i>some</i> file, not on this one. Returning early on a
    /// null would make these tests pass while asserting nothing — the same
    /// false-green this file was written to remove. VP9 is encoded with libvpx,
    /// which every supported FFmpeg build has, so a primed corpus missing it is
    /// a real problem and should be reported as one.
    /// </remarks>
    private static string RequireFixture()
    {
        var file = TestEnvironment.GetCorpusFile(VideoFixture);
        Assert.True(
            file is not null,
            $"Corpus is present but {VideoFixture} is missing. Re-run "
                + "scripts/generate-test-corpus.cs; this fixture needs no GPL encoder."
        );
        return file!;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Required_WithEmptyCapabilities_Throws()
    {
        var file = RequireFixture();

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;
        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;

        var ex = Assert.Throws<HardwareDecodeUnavailableException>(() =>
            VideoDecoder.Open(
                demux.FormatContextPtr,
                videoIdx,
                new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required },
                HardwareDecodeCapabilities.Empty,
                loggerFactory: null
            )
        );

        // The exception carries diagnostics so a consumer can report *why* no
        // backend bound. With empty capabilities there are no candidates to
        // attempt, so the list is empty and the codec identity is what remains
        // useful.
        Assert.Equal("vp9", ex.CodecName);
        Assert.Empty(ex.Attempts);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Disabled_WithEmptyCapabilities_FallsBackToSoftware()
    {
        var file = RequireFixture();

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;
        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;

        // The contrast case: same empty capabilities, different mode. Required
        // throwing only means something if the other modes do not.
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIdx,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );

        Assert.Null(decoder.HardwareBackend);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Auto_WithEmptyCapabilities_FallsBackToSoftware()
    {
        var file = RequireFixture();

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;
        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;

        // Auto is the default mode, so this is the path most consumers take on a
        // machine with no usable backend. It must degrade, not throw.
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIdx,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Auto },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );

        Assert.Null(decoder.HardwareBackend);
    }
}
