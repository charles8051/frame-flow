using System.Diagnostics;
using FrameFlow.Audio.TestKit;
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
/// Plays a corpus file through the <b>real</b> <c>OpenAlAudioSink</c>, pointed at
/// a <see cref="FakeOpenAlDevice"/> instead of a sound card, and returns what that
/// device was actually handed.
/// </summary>
/// <remarks>
/// <para>
/// Every other mode here substitutes a capturing sink for the production one, so
/// the captures record what the pipeline handed the sink. What the sink then did
/// with it — the buffer queue, the recycle, the pre-buffer gate, the clock it
/// publishes back as the pacing master — was covered nowhere in this suite. The
/// sink was the one component in the audio path with no content coverage (#146).
/// </para>
/// <para>
/// <see cref="ContentCapturingClockMasterAudioSink"/> exists because the real sink
/// is simultaneously the content destination and the master clock, and it had to
/// mirror the sink's clock arithmetic to stand in for it. This mode needs no
/// mirror: the sink under test is the sink that ships.
/// </para>
/// <para>
/// Playback runs at 1x because <see cref="FakeDevicePump"/> cannot usefully run
/// faster, so this is for short clips rather than the whole corpus.
/// </para>
/// </remarks>
internal static class OpenAlPlaybackHarness
{
    public static async Task<OpenAlPlaybackResult> PlayCorpusFileAsync(
        string corpusFilename,
        TimeSpan? timeout = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Disabled
    )
    {
        var device = new FakeOpenAlDevice();
        var audioSink = FakeOpenAlSink.Create(device);
        var videoSink = new CapturingVideoSink();

        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode
        );

        var pump = new FakeDevicePump(device);
        var pumpStopped = false;

        try
        {
            var path = PlaybackHarness.ResolveCorpusPath(corpusFilename);

            var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                MediaSource.FromFile(path),
                timeout
            );

            // Let the device finish the audio still queued at EOF. The pipeline
            // reports Ended once the last buffer has been handed over, which is
            // earlier than the moment the device has played it.
            await DrainAsync(device).ConfigureAwait(false);

            // Stop the pump before reading the device, so the snapshot is of a
            // device nothing is still advancing. Reading it live raced the pump:
            // a lull long enough to satisfy the drain would be followed by the
            // pump playing more during teardown, and the captured audio would be
            // short by whatever landed after the read.
            await pump.DisposeAsync().ConfigureAwait(false);
            pumpStopped = true;

            return new OpenAlPlaybackResult(
                Device: device,
                Sink: audioSink,
                PlayedAudio: ToCapture(device),
                Video: videoSink.Captures,
                FinalState: controller.State,
                LoadResult: loadResult,
                PlayResult: playResult
            );
        }
        finally
        {
            if (!pumpStopped)
                await pump.DisposeAsync().ConfigureAwait(false);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller).ConfigureAwait(false);
            await controller.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the device has played everything queued on it, so the
    /// comparison runs against everything it played rather than everything it had
    /// played by the moment the controller said Ended.
    /// </summary>
    /// <remarks>
    /// The exit condition is the queue, not a quiet period. A sample count that
    /// stopped moving is ambiguous — the device may be genuinely finished, or the
    /// pump thread may simply have been descheduled — and treating a lull as
    /// completion returns a short capture. An empty source queue is not ambiguous.
    /// The elapsed cap is a backstop for a sink that has wedged, which is a
    /// failure the caller's own assertions should report rather than a hang here.
    /// </remarks>
    private static async Task DrainAsync(FakeOpenAlDevice device)
    {
        var deadline = Stopwatch.StartNew();
        var cap = TimeSpan.FromSeconds(10);

        while (deadline.Elapsed < cap)
        {
            if (device.AllQueuesEmpty)
                return;
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wraps the device's played stream as a single capture block so the existing
    /// content invariants can run against it unchanged.
    /// </summary>
    /// <remarks>
    /// One block rather than many because OpenAL has no notion of presentation
    /// time: a buffer carries samples and a rate, and nothing else.
    /// <see cref="PlaybackInvariants.AudioPcmMatchesReference"/> flattens both
    /// sides before comparing, so the blocking is not material to it.
    /// </remarks>
    private static IReadOnlyList<AudioCapture> ToCapture(FakeOpenAlDevice device)
    {
        var samples = device.PlayedSamples;
        if (samples.Count == 0)
            return [];

        return
        [
            new AudioCapture(
                Pts: TimeSpan.Zero,
                InterleavedSamples: [.. samples],
                SampleRate: device.PlayedSampleRate,
                Channels: device.PlayedChannels
            ),
        ];
    }
}

/// <summary>
/// What <see cref="OpenAlPlaybackHarness.PlayCorpusFileAsync"/> hands back. The
/// device is included so a test can assert over the call log as well as the PCM,
/// and the sink so a test can compare its reported counters against what the
/// device actually did — the comparison #140 is about.
/// </summary>
internal sealed record OpenAlPlaybackResult(
    FakeOpenAlDevice Device,
    FrameFlow.Audio.OpenAL.OpenAlAudioSink Sink,
    IReadOnlyList<AudioCapture> PlayedAudio,
    IReadOnlyList<VideoCapture> Video,
    PlaybackState FinalState,
    Result LoadResult,
    Result PlayResult
);

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
