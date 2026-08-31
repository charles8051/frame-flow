using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// §7.3 — runs the same content-level invariants as <see cref="ContentCaptureTests"/>,
/// but with the audio sink <b>mastering the pacing clock</b>. The
/// <see cref="ContentCapturingClockMasterAudioSink"/> implements
/// <see cref="FrameFlow.Graph.IClockSource"/>, so for an A/V item <c>SubstrateSession</c>
/// selects it as the master (the audio-mastered path the real <c>OpenAlAudioSink</c> takes),
/// and the content assertions therefore exercise the audio sample-counter clock that paces
/// video in production — closing the gap where <see cref="ContentCaptureTests"/> verifies
/// content only on the wallclock-mastered path (its <see cref="CapturingAudioSink"/> is
/// deliberately not an <see cref="FrameFlow.Graph.IClockSource"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What's pinned.</b> All four sink-mode invariants — no runtime-introduced audio
/// duplication, strictly-monotonic PTS on both streams, A/V sync within tolerance, and audio
/// PCM matching a clean reference decode — run identically here. The only difference from
/// <see cref="ContentCaptureTests"/> is which clock paces the run, so a divergence
/// between the two classes localizes a bug to the audio-master pacing path specifically.
/// </para>
/// <para>
/// <b>Runs only with FFmpeg + corpus.</b> Like its sibling, each test is gated behind
/// <see cref="RequiresFfmpegAndCorpusFact"/>; on a worktree without the native FFmpeg
/// runtime and the corpus file it skips. The parent runs it at integration where the natives
/// exist.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Tier", "1")]
public sealed class ContentCaptureAudioMasterTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Corpus = "test-av-h264-aac.mp4";

    private readonly FfmpegBootstrapFixture _fixture;

    public ContentCaptureAudioMasterTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AudioMastered_PlayingAvFile_DoesNotProduceDuplicatedAudioSegments()
    {
        var result = await PlaybackHarness.PlayCorpusFileNextWithAudioMasterAsync(Corpus);

        Assert.True(result.LoadResult.IsSuccess, $"Load failed: {result.LoadResult.Error?.Message}");
        Assert.True(result.PlayResult.IsSuccess, $"Play failed: {result.PlayResult.Error?.Message}");
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var reference = await ReferenceDecoder.DecodeAsync(Corpus);

        PlaybackInvariants.NoDuplicateAudioSegments(
            result.Audio,
            reference: reference.Audio,
            windowSize: TimeSpan.FromMilliseconds(250),
            correlationThreshold: 0.95
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AudioMastered_PlayingAvFile_HasStrictlyMonotonicPts()
    {
        var result = await PlaybackHarness.PlayCorpusFileNextWithAudioMasterAsync(Corpus);
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        PlaybackInvariants.PtsStrictlyMonotonic(result.Audio, a => a.Pts, "audio");
        PlaybackInvariants.PtsStrictlyMonotonic(result.Video, v => v.Pts, "video");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AudioMastered_PlayingAvFile_AvSyncStaysWithinTolerance()
    {
        var result = await PlaybackHarness.PlayCorpusFileNextWithAudioMasterAsync(Corpus);
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        PlaybackInvariants.AvSyncWithinTolerance(
            result.Audio,
            result.Video,
            maxDrift: TimeSpan.FromMilliseconds(100)
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AudioMastered_PlayingAvFile_AudioMatchesReferenceDecode()
    {
        var result = await PlaybackHarness.PlayCorpusFileNextWithAudioMasterAsync(Corpus);
        Assert.Equal(PlaybackState.Ended, result.FinalState);

        var reference = await ReferenceDecoder.DecodeAsync(Corpus);

        PlaybackInvariants.AudioPcmMatchesReference(
            result.Audio,
            reference.Audio,
            maxRmsErrorPerSample: 4.0
        );
    }
}
