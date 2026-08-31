using System.Text.Json;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// Helper for integration tests that creates a fully-wired <see cref="IPlaybackController"/>
/// via DI, loads a corpus file, plays to completion, and returns the instrumented audio sink.
/// </summary>
internal static class IntegrationTestHelper
{
    /// <summary>
    /// Builds an <see cref="IPlaybackController"/> via
    /// <see cref="FrameFlow.Playback.PlaybackController.Create"/>, returning
    /// instrumented sinks alongside it so tests can assert on what the
    /// pipeline actually delivered.
    /// </summary>
    /// <remarks>
    /// Skips the DI container — the controller doesn't need it
    /// (the factory + session compose explicitly). Also skips
    /// <c>IPlaybackControllerFactory</c> + <c>FrameFlowBootstrapper</c>
    /// from DI; the test caller's <see cref="FfmpegBootstrapFixture"/>
    /// already initialized the FFmpeg native runtime.
    /// </remarks>
    internal static (
        IPlaybackController Controller,
        HarnessAudioSink AudioSink,
        HarnessVideoSink VideoSink
    ) CreateController()
    {
        var audioSink = new HarnessAudioSink();
        var videoSink = new HarnessVideoSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );
        return (controller, audioSink, videoSink);
    }

    /// <summary>
    /// Same shape as <see cref="CreateController()"/> but routes the
    /// <see cref="HardwareDecodeMode"/> through to
    /// <see cref="FrameFlow.Playback.PlaybackController.Create"/>.
    /// Used by the hwaccel integration tests which need to
    /// exercise the Auto / Disabled / Required selection paths against
    /// real FFmpeg decoders.
    /// </summary>
    internal static (
        IPlaybackController Controller,
        HarnessAudioSink AudioSink,
        HarnessVideoSink VideoSink
    ) CreateController(HardwareDecodeMode hardwareDecodeMode)
    {
        var audioSink = new HarnessAudioSink();
        var videoSink = new HarnessVideoSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode
        );
        return (controller, audioSink, videoSink);
    }

    /// <summary>
    /// Builds a <see cref="IPlaybackController"/> with a
    /// caller-provided <see cref="IAudioSink"/> and initial
    /// <see cref="RepeatMode"/>. Used by the audio-mastered loop-origin test:
    /// passing a <see cref="ClockMasteringReseatAudioSink"/> makes the audio sink
    /// master the pacing clock (the audio-attached signage panel path),
    /// and <see cref="RepeatMode.One"/> exercises the single-clip in-player
    /// loop-seek whose clock-origin seating B5 covers.
    /// </summary>
    internal static (
        IPlaybackController Controller,
        HarnessVideoSink VideoSink
    ) CreateController(IAudioSink audioSink, RepeatMode initialRepeatMode)
    {
        var videoSink = new HarnessVideoSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: initialRepeatMode
        );
        return (controller, videoSink);
    }

    /// <summary>
    /// Builds a <see cref="IPlaybackController"/> with a
    /// caller-provided <see cref="IVideoSink"/>. Mirrors
    /// <see cref="CreateController(IVideoSink)"/> on the old-substrate path
    /// for the SDL/Avalonia sink-specific integration tests.
    /// </summary>
    internal static (
        IPlaybackController Controller,
        HarnessAudioSink AudioSink
    ) CreateController(IVideoSink videoSink)
    {
        var audioSink = new HarnessAudioSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );
        return (controller, audioSink);
    }

    /// <summary>
    /// Loads a media source, plays to completion (waits for Ended or Unloaded state),
    /// and returns the <see cref="HarnessAudioSink"/> with decoded duration data.
    /// </summary>
    /// <param name="controller">The playback controller to drive.</param>
    /// <param name="source">The media source to load.</param>
    /// <param name="timeout">
    /// Maximum time to wait for playback completion. Defaults to 30 seconds.
    /// </param>
    /// <returns>The result of the Load and Play operations.</returns>
    internal static async Task<(Result LoadResult, Result PlayResult)> PlayToCompletionAsync(
        IPlaybackController controller,
        IMediaSource source,
        TimeSpan? timeout = null
    )
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var completionTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        using var subscription = controller.PlaybackStateChanged.Subscribe(
            new ActionObserver<StateTransition<PlaybackState>>(transition =>
            {
                if (
                    transition.Current
                    is PlaybackState.Ended
                        or PlaybackState.Unloaded
                        or PlaybackState.Error
                )
                {
                    completionTcs.TrySetResult();
                }
            })
        );

        var loadResult = await controller.LoadAsync(source);
        if (!loadResult.IsSuccess)
            return (
                loadResult,
                Result.Fail(ErrorCategory.InvalidOperation, "Load failed — Play not attempted.")
            );

        var playResult = await controller.PlayAsync();
        if (!playResult.IsSuccess)
            return (loadResult, playResult);

        // Wait for playback to finish or timeout.
        using var cts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await completionTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out — caller should check state.
        }

        return (loadResult, playResult);
    }

    /// <summary>
    /// Computes the wall-clock tolerance for a given expected duration using the D015 formula:
    /// ±350 ms per 3 s (proportional), with a 300 ms floor.
    /// </summary>
    /// <param name="expectedSeconds">The expected playback duration in seconds.</param>
    /// <returns>A <see cref="TimeSpan"/> representing the acceptable timing tolerance.</returns>
    internal static TimeSpan WallClockTolerance(double expectedSeconds)
    {
        var proportional = TimeSpan.FromMilliseconds(expectedSeconds / 3.0 * 350);
        var floor = TimeSpan.FromMilliseconds(300);
        return proportional > floor ? proportional : floor;
    }

    /// <summary>
    /// Tries to bring the controller to a quiescent state before disposal so native
    /// decoder/pipeline teardown does not race the next integration test.
    /// </summary>
    internal static async Task StabilizeForDisposeAsync(
        IPlaybackController controller,
        HarnessAudioSink? audioSink = null,
        HarnessVideoSink? videoSink = null,
        int timeoutMilliseconds = 5000
    )
    {
        var expectAudioDeactivation = audioSink?.IsActive == true;
        var deactivateBaseline = audioSink?.DeactivateCount ?? 0;

        if (
            controller.State
            is PlaybackState.Playing
                or PlaybackState.Paused
                or PlaybackState.Rebuffering
                or PlaybackState.Loading
                or PlaybackState.Ended
        )
        {
            var stopResult = await controller.UnloadAsync().ConfigureAwait(false);
            if (!stopResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"UnloadAsync failed during stabilization: {stopResult.Error?.Category}: {stopResult.Error?.Message}"
                );
            }

            await WaitForConditionAsync(
                    () => controller.State is PlaybackState.Unloaded or PlaybackState.Error,
                    timeoutMilliseconds,
                    () =>
                        $"Timed out waiting for controller stabilization. state={controller.State}."
                )
                .ConfigureAwait(false);
        }

        if (audioSink is not null && expectAudioDeactivation)
        {
            await WaitForConditionAsync(
                    () => !audioSink.IsActive && audioSink.DeactivateCount > deactivateBaseline,
                    timeoutMilliseconds,
                    () =>
                        $"Timed out waiting for HarnessAudioSink quiescence. active={audioSink.IsActive}, activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}, baselineDeactivate={deactivateBaseline}."
                )
                .ConfigureAwait(false);
        }

        if (videoSink is not null)
        {
            await videoSink.WaitForDrainAsync(timeoutMilliseconds).ConfigureAwait(false);
        }
    }

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        int timeoutMilliseconds,
        Func<string> failureMessage
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        while (!predicate())
        {
            try
            {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(failureMessage());
            }
        }
    }

    /// <summary>
    /// Loads corpus expectations from tests/corpus/test-expectations.json.
    /// </summary>
    internal static List<CorpusExpectation> LoadExpectations()
    {
        var json = File.ReadAllText(IntegrationTestEnvironment.ExpectationsPath);
        return JsonSerializer.Deserialize<List<CorpusExpectation>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? [];
    }
}

/// <summary>
/// Minimal <see cref="IObserver{T}"/> that delegates <see cref="OnNext"/> to an action.
/// </summary>
internal sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(T value) => onNext(value);
}

/// <summary>
/// Deserialized corpus file expectation from test-expectations.json.
/// </summary>
internal sealed class CorpusExpectation
{
    public string Filename { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public int DurationToleranceMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Fps { get; set; }
    public int? ExpectedVideoFrames { get; set; }
    public int? AudioSampleRate { get; set; }
    public int? AudioChannels { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
}
