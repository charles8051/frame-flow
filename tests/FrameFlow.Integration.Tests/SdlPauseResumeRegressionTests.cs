using System.Diagnostics;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.SDL;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// SDL pause/resume regression coverage
/// for <see cref="FrameFlow.Playback.PlaybackController"/>.
/// Mirrors <see cref="SdlPauseResumeRegressionTests"/> against the
/// substrate using the same headless <see cref="SdlVideoSink"/>
/// pump loop, proving the documented restart-from-zero failure
/// mode and the AV1 rapid-toggle crash both stay regressed on the
/// new gate-based pause path.
/// </summary>
/// <remarks>
/// SDL availability gate (the corpus + ffmpeg gate from
/// <see cref="RequiresFfmpegAndCorpusFactAttribute"/>) is sufficient
/// here — <see cref="SdlVideoSink.CreateHeadless"/> does not need a
/// real window/display, and the only platform-affinity work runs on
/// the test's dedicated pump task.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SdlPauseResumeRegressionTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public SdlPauseResumeRegressionTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task HeadlessSdl_HighFpsVideoOnly_PauseResume_DoesNotRestartPlayback()
    {
        const string corpusFile = "test-fps-60.mp4";
        var filePath = IntegrationTestEnvironment.GetCorpusFile(corpusFile);
        Assert.True(filePath is not null, $"Corpus file {corpusFile} not found.");

        var expectation = IntegrationTestHelper
            .LoadExpectations()
            .First(e => e.Filename == corpusFile);

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);
        var sdlSink = SdlVideoSink.CreateHeadless(framePool);

        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(() => SdlPumpLoopAsync(sdlSink, pumpCts.Token));

        try
        {
            var (controller, _) = IntegrationTestHelper.CreateController(sdlSink);
            await using (controller)
            {
                var terminalTcs = new TaskCompletionSource<PlaybackState>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                using var playbackSubscription = controller.PlaybackStateChanged.Subscribe(
                    new ActionObserver<StateTransition<PlaybackState>>(transition =>
                    {
                        if (
                            transition.Current
                            is PlaybackState.Ended
                                or PlaybackState.Error
                                or PlaybackState.Unloaded
                        )
                        {
                            terminalTcs.TrySetResult(transition.Current);
                        }
                    })
                );

                var source = MediaSource.FromFile(filePath!);
                var loadResult = await controller.LoadAsync(source);
                Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

                var playResult = await controller.PlayAsync();
                Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

                await WaitForConditionAsync(
                    () =>
                        controller.State == PlaybackState.Playing
                        && controller.Position >= TimeSpan.FromSeconds(1.5),
                    TimeSpan.FromSeconds(10),
                    () =>
                        $"Playback did not reach the pre-pause target. state={controller.State}, position={controller.Position}."
                );

                var stopwatch = Stopwatch.StartNew();
                var pausedPosition = controller.Position;

                var pauseResult = await controller.PauseAsync();
                Assert.True(pauseResult.IsSuccess, $"Pause failed: {pauseResult.Error}");
                Assert.Equal(PlaybackState.Paused, controller.State);

                var pauseDuration = TimeSpan.FromMilliseconds(400);
                await Task.Delay(pauseDuration);

                var resumeResult = await controller.PlayAsync();
                Assert.True(resumeResult.IsSuccess, $"Resume failed: {resumeResult.Error}");

                var restartObserved = false;
                var postResumeObservationDeadline = Stopwatch.StartNew();
                while (
                    postResumeObservationDeadline.Elapsed < TimeSpan.FromSeconds(3)
                    && controller.State
                        is not (
                            PlaybackState.Ended
                            or PlaybackState.Error
                            or PlaybackState.Unloaded
                        )
                )
                {
                    if (controller.Position + TimeSpan.FromMilliseconds(150) < pausedPosition)
                    {
                        restartObserved = true;
                        break;
                    }

                    await Task.Delay(25);
                }

                using var endCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var terminalState = await terminalTcs.Task.WaitAsync(endCts.Token);
                stopwatch.Stop();

                Assert.False(
                    restartObserved,
                    $"Observed playback position jump backwards after resume. paused={pausedPosition}, current={controller.Position}, state={controller.State}."
                );
                Assert.Equal(PlaybackState.Ended, terminalState);

                var remainingDuration =
                    TimeSpan.FromSeconds(expectation.DurationSeconds) - pausedPosition;
                if (remainingDuration < TimeSpan.Zero)
                {
                    remainingDuration = TimeSpan.Zero;
                }

                var lowerBound = remainingDuration + pauseDuration - TimeSpan.FromMilliseconds(350);
                var upperBound = remainingDuration + pauseDuration + TimeSpan.FromSeconds(1.5);

                Assert.True(
                    stopwatch.Elapsed >= lowerBound,
                    $"Playback finished too quickly after pause/resume. elapsed={stopwatch.Elapsed.TotalSeconds:F3}s, lowerBound={lowerBound.TotalSeconds:F3}s."
                );
                Assert.True(
                    stopwatch.Elapsed <= upperBound,
                    $"Playback took too long after pause/resume. elapsed={stopwatch.Elapsed.TotalSeconds:F3}s, upperBound={upperBound.TotalSeconds:F3}s."
                );
            }
        }
        finally
        {
            pumpCts.Cancel();
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }

            await sdlSink.DisposeAsync();
            framePool.Dispose();
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task HeadlessSdl_HardAv1_RapidPauseResume_DoesNotFaultVideoDecoder()
    {
        const string corpusFile = "test-video-av1-yuv444p-hard.mkv";
        var filePath = IntegrationTestEnvironment.GetCorpusFile(corpusFile);
        Assert.True(filePath is not null, $"Corpus file {corpusFile} not found.");

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);
        var sdlSink = SdlVideoSink.CreateHeadless(framePool);

        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(() => SdlPumpLoopAsync(sdlSink, pumpCts.Token));

        try
        {
            var (controller, _) = IntegrationTestHelper.CreateController(sdlSink);
            await using (controller)
            {
                var terminalTcs = new TaskCompletionSource<PlaybackState>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                var playbackErrors = new List<PlaybackError>();

                using var playbackSubscription = controller.PlaybackStateChanged.Subscribe(
                    new ActionObserver<StateTransition<PlaybackState>>(transition =>
                    {
                        if (
                            transition.Current
                            is PlaybackState.Ended
                                or PlaybackState.Error
                                or PlaybackState.Unloaded
                        )
                        {
                            terminalTcs.TrySetResult(transition.Current);
                        }
                    })
                );
                using var errorSubscription = controller.ErrorOccurred.Subscribe(
                    new ActionObserver<PlaybackError>(error => playbackErrors.Add(error))
                );

                var source = MediaSource.FromFile(filePath!);
                var loadResult = await controller.LoadAsync(source);
                Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

                var playResult = await controller.PlayAsync();
                Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

                await WaitForConditionAsync(
                    () =>
                        controller.State == PlaybackState.Playing
                        && controller.Position >= TimeSpan.FromMilliseconds(600),
                    TimeSpan.FromSeconds(20),
                    () =>
                        $"Playback did not reach the rapid-toggle target. state={controller.State}, position={controller.Position}."
                );

                for (var i = 0; i < 3; i++)
                {
                    var pauseResult = await controller.PauseAsync();
                    Assert.True(
                        pauseResult.IsSuccess,
                        $"Pause #{i + 1} failed: {pauseResult.Error}; state={controller.State}; errors=[{string.Join(" | ", playbackErrors.Select(error => $"{error.Category}:{error.Message}"))}]"
                    );
                    await WaitForConditionAsync(
                        () => controller.State == PlaybackState.Paused,
                        TimeSpan.FromSeconds(5),
                        () => $"Pause #{i + 1} did not settle to Paused. state={controller.State}."
                    );

                    await Task.Delay(60);

                    var resumeResult = await controller.PlayAsync();
                    Assert.True(
                        resumeResult.IsSuccess,
                        $"Resume #{i + 1} failed: {resumeResult.Error}"
                    );
                    await WaitForConditionAsync(
                        () => controller.State == PlaybackState.Playing,
                        TimeSpan.FromSeconds(5),
                        () =>
                            $"Resume #{i + 1} did not settle to Playing. state={controller.State}."
                    );

                    await Task.Delay(120);
                }

                using var endCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var terminalState = await terminalTcs.Task.WaitAsync(endCts.Token);
                await Task.Delay(100);

                Assert.True(
                    terminalState == PlaybackState.Ended,
                    $"Expected hard AV1 rapid pause/resume to finish cleanly, but terminalState={terminalState}, errors=[{string.Join(" | ", playbackErrors.Select(error => $"{error.Category}:{error.Message}"))}]."
                );
                Assert.Empty(playbackErrors);
            }
        }
        finally
        {
            pumpCts.Cancel();
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }

            await sdlSink.DisposeAsync();
            framePool.Dispose();
        }
    }

    private static async Task SdlPumpLoopAsync(
        SdlVideoSink sink,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
                sink.RenderPendingFrame();
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        Func<string> failureMessage
    )
    {
        using var cts = new CancellationTokenSource(timeout);

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
}
