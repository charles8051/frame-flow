using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Content-level coverage of
/// <see cref="FrameFlow.Playback.PlaybackController"/>: proves it emits
/// decoded audio and video satisfying five invariants — no duplicate audio
/// segments, monotonic PTS, A/V sync within tolerance, audio matching a
/// reference decode, and video frames matching a reference decode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Sink-mode only. There is no pull-mode counterpart: the
/// controller's <c>VideoFrames</c> / <c>AudioBuffers</c> channels were
/// deleted once every production consumer went sink-based, so there is no
/// pull surface left to test.
/// </para>
/// <para>
/// <b>Nothing is deliberately skipped here.</b> If frames flow through the
/// pace+gate operators with correct PTS and content, all five invariants
/// must hold.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Tier", "1")]
[Collection(ContentCaptureCollection.Name)]
public sealed class ContentCaptureTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public ContentCaptureTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_DoesNotProduceDuplicatedAudioSegments()
    {
        var result = await PlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");

        Assert.True(
            result.LoadResult.IsSuccess,
            $"Load failed: {result.LoadResult.Error?.Message}"
        );
        Assert.True(
            result.PlayResult.IsSuccess,
            $"Play failed: {result.PlayResult.Error?.Message}"
        );
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var reference = await ReferenceDecoder.DecodeAsync("test-av-h264-aac.mp4");

        PlaybackInvariants.NoDuplicateAudioSegments(
            result.Audio,
            reference: reference.Audio,
            windowSize: TimeSpan.FromMilliseconds(250),
            correlationThreshold: 0.95
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_HasStrictlyMonotonicPts()
    {
        var result = await PlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        PlaybackInvariants.PtsStrictlyMonotonic(result.Audio, a => a.Pts, "audio");
        PlaybackInvariants.PtsStrictlyMonotonic(result.Video, v => v.Pts, "video");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_AvSyncStaysWithinTolerance()
    {
        var result = await PlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        PlaybackInvariants.AvSyncWithinTolerance(
            result.Audio,
            result.Video,
            maxDrift: TimeSpan.FromMilliseconds(100)
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayingAvFile_AudioMatchesReferenceDecode()
    {
        var result = await PlaybackHarness.PlayCorpusFileAsync("test-av-h264-aac.mp4");
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var reference = await ReferenceDecoder.DecodeAsync("test-av-h264-aac.mp4");

        PlaybackInvariants.AudioPcmMatchesReference(
            result.Audio,
            reference.Audio,
            maxRmsErrorPerSample: 4.0
        );
    }

    /// <summary>
    /// Every video-bearing corpus file the default generator produces.
    /// </summary>
    /// <remarks>
    /// Deliberately broader than the single <c>test-av-h264-aac.mp4</c> the
    /// audio invariants use. A corruption that depends on codec, on frame
    /// reordering, or on decode cost per unit of wall time will not show up
    /// on one 320x240 H.264 clip; #134 reproduced on neither the codec nor
    /// the resolution this suite had.
    ///
    /// <para>
    /// <c>test-pts-b-frames.mp4</c> is absent on purpose. It is one of the two
    /// fixtures the pinned LGPL FFmpeg cannot encode (libx264 is disabled as
    /// GPL), so the default corpus has no B-frame clip at all. Frame
    /// reordering is the case most likely to expose a reference-chain
    /// regression, which makes its absence worth recording rather than
    /// papering over.
    /// </para>
    /// </remarks>
    public static TheoryData<string> VideoBearingCorpusFiles =>
        new()
        {
            "test-av-h264-aac.mp4",
            "test-av-h264-aac.mkv",
            "test-video-h264-yuv420p.mp4",
            "test-video-h265-yuv420p.mp4",
            "test-video-vp9-yuv420p.webm",
            "test-video-av1-yuv420p.mkv",
            "test-1080p-h264-aac.mp4",
        };

    /// <summary>
    /// Asserts every frame the playback runtime presented is byte-identical
    /// to the frame a bare decode of the same file produces at that PTS, and
    /// that every frame it did not present is one it counted as dropped late.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the video half of ADR-0031's premise, which
    /// <see cref="PlaybackInvariants.VideoFramePixelsMatchReference"/> was
    /// written for and which nothing called until now. The audio half has
    /// been asserted since the harness landed; the video half had the
    /// capture path, the reference decoder and the comparison all in place
    /// and no test invoking them.
    /// </para>
    /// <para>
    /// Both sides decode in software (<see cref="HardwareDecodeMode.Disabled"/>
    /// is the harness default and <see cref="ReferenceDecoder"/> drives the
    /// decoders directly), so byte-exact equality is the right expectation.
    /// </para>
    /// <para>
    /// <b>Not every frame has to be presented (#299).</b> The run plays in
    /// real time against the session's wallclock, and the pacer presents the
    /// newest frame that is due, dropping any older one the clock has also
    /// passed. A runner descheduled for longer than a frame interval therefore
    /// drops a frame on purpose and counts it. Demanding zero loss made this a
    /// test of the runner: it failed on a shared CI host with every delivered
    /// frame matching. So the loss is accounted rather than budgeted: each
    /// reference frame was presented or counted as dropped late, with nothing
    /// lost uncounted. How many frames land on each side varies with load; the
    /// sum does not. Zero late drops is not asserted anywhere, for the reason
    /// <c>NominalRunHealthTests</c> gives.
    /// </para>
    /// <para>
    /// The account has no term for a shed before decode, and none is needed
    /// here. Each clip has at most 90 video packets and the video packet queue
    /// holds at least <see cref="FrameFlow.Decoding.ReadAheadCapacity.MinCapacity"/>,
    /// so drop-newest cannot fire at any speed. A clip chosen to overload decode
    /// belongs in its own test, with its shed accounted for there.
    /// </para>
    /// </remarks>
    [RequiresFfmpegAndCorpusTheory]
    [MemberData(nameof(VideoBearingCorpusFiles))]
    public async Task PlayingCorpusFile_VideoFramesMatchReferenceDecode(string corpusFilename)
    {
        var result = await PlaybackHarness.PlayCorpusFileAsync(corpusFilename);

        Assert.True(
            result.LoadResult.IsSuccess,
            $"Load failed for {corpusFilename}: {result.LoadResult.Error?.Message}"
        );
        Assert.True(
            result.PlayResult.IsSuccess,
            $"Play failed for {corpusFilename}: {result.PlayResult.Error?.Message}"
        );
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var decoder = result.Diagnostics.Pipeline.Stream.VideoDecoder;
        Assert.True(
            decoder.PacketsDroppedForBackpressure == 0,
            $"{corpusFilename}: the decoder shed {decoder.PacketsDroppedForBackpressure} packets, "
                + "which a clip this short cannot do at any speed, and which the frame account "
                + "below has no term for."
        );

        var reference = await ReferenceDecoder.DecodeAsync(corpusFilename);

        PlaybackInvariants.VideoFramePixelsMatchReference(
            result.Video,
            reference.Video,
            droppedLate: result.Diagnostics.Pipeline.VideoFramesDroppedForSync
        );
    }
}
