using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// Test-side helper that wires the playback runtime with
/// <see cref="CapturingAudioSink"/> and <see cref="CapturingVideoSink"/>
/// in place of the production sinks, loads a corpus file, plays it
/// to natural EOF, and returns the captures plus terminal state.
/// </summary>
/// <remarks>
/// The DI-based <c>PlayCorpusFileAsync</c>
/// and pull-mode <c>PlayCorpusFilePullModeAsync</c> are gone (the old
/// substrate's pull pipeline + DI factory have been deleted). The
/// substrate's <see cref="FrameFlow.Playback.PlaybackController.Create"/>
/// is the sole construction path.
/// </remarks>
internal static class PlaybackHarness
{
    internal static string ResolveCorpusPath(string corpusFilename) =>
        IntegrationTestEnvironment.GetCorpusFile(corpusFilename)
        ?? throw new FileNotFoundException(
            $"Corpus file '{corpusFilename}' not found in {IntegrationTestEnvironment.CorpusDir}. "
                + "Run scripts/generate-test-corpus.cs to regenerate.",
            corpusFilename
        );

    /// <summary>
    /// Playback harness: wires the controller via
    /// <see cref="FrameFlow.Playback.PlaybackController.Create"/>
    /// (no DI provider) with <see cref="CapturingAudioSink"/> +
    /// <see cref="CapturingVideoSink"/> so tests can assert content
    /// invariants over the captured audio/video.
    /// </summary>
    public static async Task<PlaybackCaptureResult> PlayCorpusFileAsync(
        string corpusFilename,
        TimeSpan? timeout = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Disabled
    )
    {
        var audioSink = new CapturingAudioSink();
        var videoSink = new CapturingVideoSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode
        );

        try
        {
            var path = ResolveCorpusPath(corpusFilename);

            var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                MediaSource.FromFile(path),
                timeout
            );

            return new PlaybackCaptureResult(
                Audio: audioSink.Captures,
                Video: videoSink.Captures,
                FinalState: controller.State,
                LoadResult: loadResult,
                PlayResult: playResult
            );
        }
        finally
        {
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller).ConfigureAwait(false);
            await controller.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// §7.3: same as <see cref="PlayCorpusFileAsync"/> but wires a
    /// <see cref="ContentCapturingClockMasterAudioSink"/> — a content-capturing sink that
    /// <b>also masters the pacing clock</b> (it implements <see cref="IClockSource"/>). With
    /// an audio-bearing item, <c>SubstrateSession</c> therefore selects the audio sink as
    /// master (the audio-mastered path the real OpenAL sink takes), so the content invariants
    /// run against the audio sample-counter clock rather than the session's wallclock master.
    /// </summary>
    /// <remarks>
    /// Closes the coverage gap where <see cref="PlayCorpusFileAsync"/>'s
    /// <see cref="CapturingAudioSink"/> — deliberately not an <see cref="IClockSource"/> —
    /// leaves the wallclock as master, so content was only ever verified on the
    /// wallclock-mastered path.
    /// </remarks>
    public static async Task<PlaybackCaptureResult> PlayCorpusFileNextWithAudioMasterAsync(
        string corpusFilename,
        TimeSpan? timeout = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Disabled
    )
    {
        var audioSink = new ContentCapturingClockMasterAudioSink();
        var videoSink = new CapturingVideoSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode
        );

        try
        {
            var path = ResolveCorpusPath(corpusFilename);

            var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                MediaSource.FromFile(path),
                timeout
            );

            return new PlaybackCaptureResult(
                Audio: audioSink.Captures,
                Video: videoSink.Captures,
                FinalState: controller.State,
                LoadResult: loadResult,
                PlayResult: playResult
            );
        }
        finally
        {
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller).ConfigureAwait(false);
            await controller.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// What <see cref="PlaybackHarness.PlayCorpusFileAsync"/> hands back:
/// the captured audio + video, the terminal state, and the load/play
/// result codes so tests can fail fast on harness-side errors before
/// running content invariants.
/// </summary>
internal sealed record PlaybackCaptureResult(
    IReadOnlyList<AudioCapture> Audio,
    IReadOnlyList<VideoCapture> Video,
    PlaybackState FinalState,
    Result LoadResult,
    Result PlayResult
);
