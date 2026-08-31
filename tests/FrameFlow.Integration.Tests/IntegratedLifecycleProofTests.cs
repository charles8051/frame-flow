using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Milestone-closeout lifecycle proofs
/// for <see cref="FrameFlow.Playback.PlaybackController"/>.
/// Mirrors <see cref="IntegratedLifecycleProofTests"/> against the
/// substrate, pinning that the composed Load → Play → Pause →
/// Resume → Seek → Stop happy path and the failure → fresh-controller
/// recovery flow both work cleanly on the new
/// <c>SubstrateSession</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recovery via fresh controller, not fresh DI-resolved controller.</b>
/// The old test calls <c>IntegrationTestHelper.CreateController(provider)</c>
/// to spin up a second controller against the same DI provider so the
/// reused sinks can be observed. The substrate has no DI provider;
/// recovery here uses <c>CreateController()</c> which allocates
/// fresh sinks. The "reused sinks reactivate cleanly" assertion is
/// reframed as "fresh controller after a failed prior load reaches
/// Playing on the happy-path corpus" — same fault recovery, different
/// sink identity.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class IntegratedLifecycleProofTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public IntegratedLifecycleProofTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task R049_HappyPath_ComposesLoadPlayPauseResumeSeekStop_AndLeavesSinksQuiesced()
    {
        const string rowId = "R049";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new LifecycleProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            Assert.Equal(PlaybackState.Paused, controller.State);

            await AssertSuccessAsync(rowId, "PlayAsync(initial)", controller.PlayAsync());
            await WaitForConditionAsync(
                rowId,
                () =>
                    controller.State == PlaybackState.Playing
                    && audioSink.ActivateCount >= 1
                    && videoSink.ProcessedFrameCount >= 1,
                15000,
                () =>
                    $"Expected initial playback to activate sinks, but state={controller.State}, activate={audioSink.ActivateCount}, processed={videoSink.ProcessedFrameCount}."
            );
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );

            await AssertSuccessAsync(rowId, "PauseAsync", controller.PauseAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Paused);
            Assert.True(
                audioSink.PauseCount >= 1,
                $"{rowId}: Expected PauseAsync to increment PauseCount, but got {audioSink.PauseCount}."
            );

            await AssertSuccessAsync(rowId, "PlayAsync(resume)", controller.PlayAsync());
            await WaitForConditionAsync(
                rowId,
                () => controller.State == PlaybackState.Playing && audioSink.ResumeCount >= 1,
                15000,
                () =>
                    $"Expected resume to return to Playing and increment ResumeCount, but state={controller.State}, resume={audioSink.ResumeCount}."
            );

            var activateBeforeSeek = audioSink.ActivateCount;
            var deactivateBeforeSeek = audioSink.DeactivateCount;
            var processedBeforeSeek = videoSink.ProcessedFrameCount;
            var seekResult = await controller.SeekAsync(TimeSpan.FromSeconds(1.5));
            Assert.True(
                seekResult.IsSuccess,
                $"{rowId}: SeekAsync should have succeeded after pause/resume composition, but returned {FormatResult(seekResult)}."
            );

            await WaitForConditionAsync(
                rowId,
                () =>
                    probe.SeekTransitions.Any(static transition =>
                        transition.Current == SeekState.SeekPending
                    ),
                15000,
                () =>
                    $"Expected seek transitions to include SeekPending, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
            );
            await WaitForConditionAsync(
                rowId,
                () =>
                    probe.SeekTransitions.Any(static transition =>
                        transition.Current == SeekState.SeekInProgress
                    ),
                15000,
                () =>
                    $"Expected seek transitions to include SeekInProgress, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
            );
            await WaitForSeekStateAsync(rowId, controller, SeekState.NotSeeking);
            // Post-seek responsiveness: sinks must have been re-cycled (seek
            // dance ran) and frames must have flowed. State may be Playing
            // or already Ended — the substrate's seek target (1.5s on a
            // 3s file) leaves only ~1.5s of remaining content, which the
            // pace+gate operators can drain quickly enough to reach Ended
            // before this check polls, so both states are accepted.
            await WaitForConditionAsync(
                rowId,
                () =>
                    (controller.State == PlaybackState.Playing
                        || controller.State == PlaybackState.Ended)
                    && audioSink.ActivateCount > activateBeforeSeek
                    && audioSink.DeactivateCount > deactivateBeforeSeek
                    && videoSink.ProcessedFrameCount > processedBeforeSeek,
                15000,
                () =>
                    $"Expected post-seek playback to stay responsive (sinks recycled + frames flowing), but state={controller.State}, activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}, processed={videoSink.ProcessedFrameCount}, baselineProcessed={processedBeforeSeek}."
            );

            var deactivateBeforeStop = audioSink.DeactivateCount;
            await AssertSuccessAsync(rowId, "UnloadAsync", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);

            Assert.False(
                audioSink.IsActive,
                $"{rowId}: Expected stop teardown to leave the audio sink inactive, but activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}."
            );
            Assert.True(
                audioSink.DeactivateCount > deactivateBeforeStop,
                $"{rowId}: Expected stop teardown to advance deactivation count, but deactivate={audioSink.DeactivateCount}, baseline={deactivateBeforeStop}."
            );
            Assert.True(
                videoSink.IsDrained,
                $"{rowId}: Expected stop teardown to drain video frames, but submitted={videoSink.SubmittedFrameCount}, processed={videoSink.ProcessedFrameCount}, pending={videoSink.HasPendingFrame}."
            );
            Assert.Empty(probe.Errors);

            AssertHappyPathOrdering(rowId, probe);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task R049_LoadFailure_ThenFreshControllerRecovery_PlaysCleanly()
    {
        const string rowId = "R049";
        var (failedController, _, _) = IntegrationTestHelper.CreateController();
        await using (failedController)
        {
            using var failureProbe = new LifecycleProbe(failedController);
            var missingSource = MediaSource.FromFile("/nonexistent/path/r049-missing.mp4");

            var loadResult = await failedController.LoadAsync(missingSource);
            Assert.False(
                loadResult.IsSuccess,
                $"{rowId}: Expected missing-media load to fail, but got {FormatResult(loadResult)}."
            );
            Assert.Equal(ErrorCategory.System, loadResult.Error?.Category);

            await WaitForConditionAsync(
                rowId,
                () => failedController.State == PlaybackState.Error && failureProbe.Errors.Count >= 1,
                15000,
                () =>
                    $"Expected load failure to surface Error state and ErrorOccurred, but state={failedController.State}, errors={failureProbe.Errors.Count}, playback=[{RenderPlaybackTransitions(failureProbe.PlaybackTransitions)}]."
            );

            Assert.Single(failureProbe.Errors);
            Assert.Contains(
                failureProbe.PlaybackTransitions,
                static transition => transition.Current == PlaybackState.Error
            );
        }

        // Fresh controller with fresh sinks should reach Playing on the happy-path corpus.
        var (recoveryController, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (recoveryController)
        {
            using var recoveryProbe = new LifecycleProbe(recoveryController);
            var recoverySource = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(
                rowId,
                "LoadAsync(recovery)",
                recoveryController.LoadAsync(recoverySource)
            );
            await AssertSuccessAsync(rowId, "PlayAsync(recovery)", recoveryController.PlayAsync());
            await WaitForConditionAsync(
                rowId,
                () =>
                    recoveryController.State == PlaybackState.Playing
                    && audioSink.ActivateCount >= 1
                    && videoSink.ProcessedFrameCount >= 1,
                15000,
                () =>
                    $"Expected fresh-controller recovery to reach Playing, but state={recoveryController.State}, activate={audioSink.ActivateCount}, processed={videoSink.ProcessedFrameCount}."
            );
            await WaitForPlaybackProgressAsync(
                rowId,
                recoveryController,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );

            Assert.Empty(recoveryProbe.Errors);
            Assert.DoesNotContain(
                recoveryProbe.PlaybackTransitions,
                static transition => transition.Current == PlaybackState.Error
            );

            await AssertSuccessAsync(rowId, "UnloadAsync(recovery)", recoveryController.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, recoveryController, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(
                recoveryController,
                audioSink,
                videoSink
            );

            Assert.False(
                audioSink.IsActive,
                $"{rowId}: Expected recovery teardown to leave the audio sink inactive, but activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}."
            );
            Assert.True(
                videoSink.IsDrained,
                $"{rowId}: Expected recovery teardown to drain the video sink, but submitted={videoSink.SubmittedFrameCount}, processed={videoSink.ProcessedFrameCount}, pending={videoSink.HasPendingFrame}."
            );
            Assert.False(
                videoSink.HasPendingFrame,
                $"{rowId}: Expected no stale pending frame after recovery teardown."
            );

            AssertRecoveryOrdering(rowId, recoveryProbe);
        }
    }

    private static void AssertHappyPathOrdering(string rowId, LifecycleProbe probe)
    {
        var initialPausedIndex = FindIndex(
            probe.PlaybackTransitions,
            static transition =>
                transition.Previous == PlaybackState.Loading
                && transition.Current == PlaybackState.Paused,
            $"{rowId}: Expected load path to settle at Loading->Paused, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}]."
        );
        var firstPlayIndex = FindIndex(
            probe.PlaybackTransitions,
            static transition =>
                transition.Previous == PlaybackState.Paused
                && transition.Current == PlaybackState.Playing,
            $"{rowId}: Expected an initial Paused->Playing transition, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}].",
            initialPausedIndex + 1
        );
        var pauseIndex = FindIndex(
            probe.PlaybackTransitions,
            static transition =>
                transition.Previous == PlaybackState.Playing
                && transition.Current == PlaybackState.Paused,
            $"{rowId}: Expected a Playing->Paused transition, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}].",
            firstPlayIndex + 1
        );
        var resumeIndex = FindIndex(
            probe.PlaybackTransitions,
            static transition =>
                transition.Previous == PlaybackState.Paused
                && transition.Current == PlaybackState.Playing,
            $"{rowId}: Expected a resumed Paused->Playing transition, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}].",
            pauseIndex + 1
        );
        // Terminal Unloaded — previous state may be Playing (graceful stop)
        // or Ended (natural completion before stop, which can happen on the
        // substrate when the seek lands close to file end and the
        // pace+gate drain catches up to EOS before UnloadAsync is called).
        var stopIndex = FindIndex(
            probe.PlaybackTransitions,
            static transition =>
                (transition.Previous == PlaybackState.Playing
                    || transition.Previous == PlaybackState.Ended)
                && transition.Current == PlaybackState.Unloaded,
            $"{rowId}: Expected a terminal Playing/Ended->Unloaded transition, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}].",
            resumeIndex + 1
        );

        Assert.True(
            initialPausedIndex < firstPlayIndex
                && firstPlayIndex < pauseIndex
                && pauseIndex < resumeIndex
                && resumeIndex < stopIndex,
            $"{rowId}: Expected load->play->pause->resume->stop ordering, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}]."
        );

        var seekPendingIndex = FindIndex(
            probe.SeekTransitions,
            static transition =>
                transition.Previous == SeekState.NotSeeking
                && transition.Current == SeekState.SeekPending,
            $"{rowId}: Expected NotSeeking->SeekPending, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
        );
        var seekInProgressIndex = FindIndex(
            probe.SeekTransitions,
            static transition =>
                transition.Previous == SeekState.SeekPending
                && transition.Current == SeekState.SeekInProgress,
            $"{rowId}: Expected SeekPending->SeekInProgress, but observed [{RenderSeekTransitions(probe.SeekTransitions)}].",
            seekPendingIndex + 1
        );
        var seekCompleteIndex = FindIndex(
            probe.SeekTransitions,
            static transition =>
                transition.Previous == SeekState.SeekInProgress
                && transition.Current == SeekState.NotSeeking,
            $"{rowId}: Expected SeekInProgress->NotSeeking, but observed [{RenderSeekTransitions(probe.SeekTransitions)}].",
            seekInProgressIndex + 1
        );

        Assert.True(
            seekPendingIndex < seekInProgressIndex && seekInProgressIndex < seekCompleteIndex,
            $"{rowId}: Expected seek acceptance/order to progress cleanly, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
        );
    }

    private static void AssertRecoveryOrdering(string rowId, LifecycleProbe recoveryProbe)
    {
        var loadPausedIndex = FindIndex(
            recoveryProbe.PlaybackTransitions,
            static transition =>
                transition.Previous == PlaybackState.Loading
                && transition.Current == PlaybackState.Paused,
            $"{rowId}: Expected recovery load path to settle at Loading->Paused, but observed [{RenderPlaybackTransitions(recoveryProbe.PlaybackTransitions)}]."
        );
        var playIndex = FindIndex(
            recoveryProbe.PlaybackTransitions,
            static transition =>
                transition.Previous == PlaybackState.Paused
                && transition.Current == PlaybackState.Playing,
            $"{rowId}: Expected recovery play transition, but observed [{RenderPlaybackTransitions(recoveryProbe.PlaybackTransitions)}].",
            loadPausedIndex + 1
        );
        var stopIndex = FindIndex(
            recoveryProbe.PlaybackTransitions,
            static transition => transition.Current == PlaybackState.Unloaded,
            $"{rowId}: Expected recovery stop transition, but observed [{RenderPlaybackTransitions(recoveryProbe.PlaybackTransitions)}].",
            playIndex + 1
        );

        Assert.True(
            loadPausedIndex < playIndex && playIndex < stopIndex,
            $"{rowId}: Expected recovery load->play->stop ordering, but observed [{RenderPlaybackTransitions(recoveryProbe.PlaybackTransitions)}]."
        );
    }

    private static int FindIndex<T>(
        IReadOnlyList<T> items,
        Func<T, bool> predicate,
        string failureMessage,
        int startIndex = 0
    )
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            if (predicate(items[index]))
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException(failureMessage);
    }

    private static async Task AssertSuccessAsync(string rowId, string operation, Task<Result> task)
    {
        var result = await task;
        Assert.True(
            result.IsSuccess,
            $"{rowId}: {operation} should have succeeded, but returned {FormatResult(result)}."
        );
    }

    private static async Task WaitForPlaybackProgressAsync(
        string rowId,
        IPlaybackController controller,
        HarnessVideoSink videoSink,
        TimeSpan minimumPosition,
        int minimumVideoFrames,
        int timeoutMilliseconds = 15000
    )
    {
        await WaitForConditionAsync(
            rowId,
            () =>
                controller.Position >= minimumPosition
                && videoSink.FrameCount >= minimumVideoFrames,
            timeoutMilliseconds,
            () =>
                $"Expected playback progress to reach position {minimumPosition} with at least {minimumVideoFrames} frames, but got position {controller.Position} and frameCount {videoSink.FrameCount}."
        );
    }

    private static async Task WaitForPlaybackStateAsync(
        string rowId,
        IPlaybackController controller,
        PlaybackState expectedState,
        int timeoutMilliseconds = 15000
    )
    {
        await WaitForConditionAsync(
            rowId,
            () => controller.State == expectedState,
            timeoutMilliseconds,
            () =>
                $"Timed out waiting for playback state {expectedState}; current state is {controller.State}."
        );
    }

    private static async Task WaitForSeekStateAsync(
        string rowId,
        IPlaybackController controller,
        SeekState expectedState,
        int timeoutMilliseconds = 15000
    )
    {
        await WaitForConditionAsync(
            rowId,
            () => controller.SeekingState == expectedState,
            timeoutMilliseconds,
            () =>
                $"Timed out waiting for seek state {expectedState}; current state is {controller.SeekingState}."
        );
    }

    private static async Task WaitForConditionAsync(
        string rowId,
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
                await Task.Delay(25, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"{rowId}: {failureMessage()}");
            }
        }
    }

    private static string RenderPlaybackTransitions(
        IEnumerable<StateTransition<PlaybackState>> transitions
    ) =>
        string.Join(
            ", ",
            transitions.Select(static transition =>
                $"{transition.Previous}->{transition.Current}"
            )
        );

    private static string RenderSeekTransitions(
        IEnumerable<StateTransition<SeekState>> transitions
    ) =>
        string.Join(
            ", ",
            transitions.Select(static transition =>
                $"{transition.Previous}->{transition.Current}"
            )
        );

    private static string FormatResult(Result result) =>
        result.IsSuccess
            ? "success"
            : $"failure ({result.Error?.Category}: {result.Error?.Message})";

    private sealed class LifecycleProbe : IDisposable
    {
        private readonly IDisposable _playbackSubscription;
        private readonly IDisposable _seekSubscription;
        private readonly IDisposable _errorSubscription;

        public LifecycleProbe(IPlaybackController controller)
        {
            _playbackSubscription = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(PlaybackTransitions.Add)
            );
            _seekSubscription = controller.SeekStateChanged.Subscribe(
                new ActionObserver<StateTransition<SeekState>>(SeekTransitions.Add)
            );
            _errorSubscription = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(Errors.Add)
            );
        }

        public List<StateTransition<PlaybackState>> PlaybackTransitions { get; } = [];

        public List<StateTransition<SeekState>> SeekTransitions { get; } = [];

        public List<PlaybackError> Errors { get; } = [];

        public void Dispose()
        {
            _playbackSubscription.Dispose();
            _seekSubscription.Dispose();
            _errorSubscription.Dispose();
        }
    }
}
