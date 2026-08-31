using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Content-level coverage of
/// <see cref="FrameFlow.Playback.PlaybackController"/>: proves it emits
/// decoded audio and video satisfying four invariants — no duplicate audio
/// segments, monotonic PTS, A/V sync within tolerance, and audio matching a
/// reference decode.
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
/// pace+gate operators with correct PTS and content, all four invariants
/// must hold.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Tier", "1")]
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
}
