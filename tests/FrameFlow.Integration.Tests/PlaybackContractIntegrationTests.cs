using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// M004/S01 contract coverage for the
/// controller
/// (<see cref="FrameFlow.Playback.PlaybackController"/>).
/// Mirrors the row-owned proofs in
/// <see cref="PlaybackContractIntegrationTests"/> so each committed
/// audit row remains pinned on both substrates as they coexist
/// during the Phase 3 / Phase 4 transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does not assert.</b> The XR001 frame-count comparison
/// against <c>CorpusExpectation.ExpectedVideoFrames</c> and the
/// decoded-audio-duration tolerance are not checked here, because the
/// pace+gate channels shift consumed/dropped counts by a few frames per
/// pass. What is pinned: state transitions, terminal Ended, EOS firing, and
/// no errors.
/// </para>
/// <para>
/// <b>Those two numeric comparisons are currently asserted nowhere.</b> This
/// doc previously said they stayed "on the old tests"; those tests were
/// deleted with the old controller, and <c>ExpectedVideoFrames</c> now has no
/// reader outside this comment. Re-establishing that coverage needs a
/// tolerance that survives the pacing split — tracked in #113.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaybackContractIntegrationTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public PlaybackContractIntegrationTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SK001_SeekDuringPlayback_AcceptsSeekCycle_AndReactivatesSinks()
    {
        const string rowId = "SK001";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync", controller.PlayAsync());
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );

            var activateBeforeSeek = audioSink.ActivateCount;
            var deactivateBeforeSeek = audioSink.DeactivateCount;

            var seekTask = controller.SeekAsync(TimeSpan.FromSeconds(1.5));
            await WaitForConditionAsync(
                rowId,
                () =>
                    probe.SeekTransitions.Any(static transition =>
                        transition.Current is SeekState.SeekPending or SeekState.SeekInProgress
                    ),
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected {rowId} seek transitions to include SeekPending or SeekInProgress, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
            );

            var seekResult = await seekTask;
            Assert.True(
                seekResult.IsSuccess,
                $"{rowId}: SeekAsync should have been accepted, but returned {FormatResult(seekResult)}."
            );

            await WaitForSeekStateAsync(rowId, controller, SeekState.NotSeeking);

            Assert.True(
                audioSink.ActivateCount > activateBeforeSeek,
                $"{rowId}: Expected seek to reactivate the audio sink, but activate count stayed at {audioSink.ActivateCount}."
            );
            Assert.True(
                audioSink.DeactivateCount > deactivateBeforeSeek,
                $"{rowId}: Expected seek to flush/deactivate the audio sink, but deactivate count stayed at {audioSink.DeactivateCount}."
            );
            Assert.Empty(probe.Errors);

            await AssertSuccessAsync(rowId, "UnloadAsync", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SK001_SeekWhilePaused_StaysPaused_UntilExplicitResumeOrStop()
    {
        const string rowId = "SK001";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync", controller.PlayAsync());
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );
            await AssertSuccessAsync(rowId, "PauseAsync", controller.PauseAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Paused);

            var playbackTransitionsBeforeSeek = probe.PlaybackTransitions.Count;
            var seekTask = controller.SeekAsync(TimeSpan.FromSeconds(1.0));
            await WaitForConditionAsync(
                rowId,
                () =>
                    probe.SeekTransitions.Any(static transition =>
                        transition.Current is SeekState.SeekPending or SeekState.SeekInProgress
                    ),
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected {rowId} paused seek transitions to include SeekPending or SeekInProgress, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
            );

            var seekResult = await seekTask;
            Assert.True(
                seekResult.IsSuccess,
                $"{rowId}: SeekAsync while paused should have been accepted, but returned {FormatResult(seekResult)}."
            );

            await WaitForSeekStateAsync(rowId, controller, SeekState.NotSeeking);
            Assert.Equal(PlaybackState.Paused, controller.State);

            var transitionsDuringSeek = probe
                .PlaybackTransitions.Skip(playbackTransitionsBeforeSeek)
                .ToArray();
            Assert.True(
                transitionsDuringSeek.All(transition =>
                    transition.Current != PlaybackState.Playing
                    && transition.Current != PlaybackState.Loading
                    && transition.Current != PlaybackState.Rebuffering
                    && transition.Current != PlaybackState.Ended
                    && transition.Current != PlaybackState.Error
                ),
                $"{rowId}: Expected paused seek to avoid playback-state motion before an explicit resume/stop, but observed [{RenderPlaybackTransitions(transitionsDuringSeek)}]."
            );
            Assert.Empty(probe.Errors);

            await AssertSuccessAsync(rowId, "UnloadAsync", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData("test-av-h264-aac.mp4")]
    [InlineData("test-video-h264-yuv420p.mp4")]
    [InlineData("test-audio-aac.m4a")]
    public async Task XR001_RepeatOff_NaturalCompletion_TransitionsToEnded_WithoutLoopRestart(
        string filename
    )
    {
        const string rowId = "XR001";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(filename)!);

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync", controller.PlayAsync());

            await WaitForConditionAsync(
                rowId,
                () =>
                    probe.PlaybackTransitions.Any(static transition =>
                        transition.Current == PlaybackState.Playing
                    ),
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected {rowId} to reach Playing for {filename}, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}]."
            );
            await WaitForPlaybackStateAsync(
                rowId,
                controller,
                PlaybackState.Ended,
                timeoutMilliseconds: 30000
            );

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(probe.Errors);
            Assert.Empty(probe.LoopRestarts);
            Assert.Contains(
                probe.PlaybackTransitions,
                transition =>
                    transition.Previous == PlaybackState.Playing
                    && transition.Current == PlaybackState.Ended
            );

            // Lifecycle-only pin: verify video frames flowed for video files.
            // The exact frame-count tolerance vs. CorpusExpectation is not
            // asserted here or anywhere else — see the class remarks and #113.
            var info = controller.MediaInfo;
            if (info?.VideoStreams.Count > 0)
            {
                await videoSink.WaitForDrainAsync();
                Assert.True(
                    videoSink.FrameCount > 0,
                    $"{rowId}: Expected video frames to be consumed for {filename}; got {videoSink.FrameCount}."
                );
            }

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task XR002_RepeatOne_LastFrameRendered_RestartsWithoutEndedState()
    {
        await AssertLoopRestartAsync("XR002", RepeatMode.One);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task XR005_LoadFailure_ReturnsFailureAlignedWithErrorObservation()
    {
        const string rowId = "XR005";
        var (controller, _, _) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile("/nonexistent/path/contract-missing.mp4");

            var loadResult = await controller.LoadAsync(source);
            Assert.False(
                loadResult.IsSuccess,
                $"{rowId}: Expected LoadAsync to fail when initialization faults before completion, but got {FormatResult(loadResult)}."
            );
            Assert.Equal(ErrorCategory.System, loadResult.Error?.Category);
            Assert.Equal(PlaybackState.Error, controller.State);

            await WaitForConditionAsync(
                rowId,
                () => probe.Errors.Count >= 1,
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected {rowId} to emit PlaybackError after the failed load, but state={controller.State}, errors={probe.Errors.Count}, playbackTransitions=[{RenderPlaybackTransitions(probe.PlaybackTransitions)}]."
            );

            Assert.Contains(
                probe.PlaybackTransitions,
                static transition => transition.Current == PlaybackState.Error
            );
            var error = Assert.Single(probe.Errors);
            Assert.Equal(ErrorCategory.System, error.Category);
            Assert.False(
                string.IsNullOrWhiteSpace(error.Message),
                $"{rowId}: Expected a non-empty error message for the load failure path."
            );
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task XR006_EndedSeek_RoutesThroughBufferingPath_AndSettlesPaused()
    {
        const string rowId = "XR006";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync", controller.PlayAsync());
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );
            await WaitForPlaybackStateAsync(
                rowId,
                controller,
                PlaybackState.Ended,
                timeoutMilliseconds: 30000
            );

            var playbackTransitionsBeforeSeek = probe.PlaybackTransitions.Count;
            var seekTransitionsBeforeSeek = probe.SeekTransitions.Count;

            var seekResult = await controller.SeekAsync(TimeSpan.FromSeconds(1.0));
            Assert.True(
                seekResult.IsSuccess,
                $"{rowId}: Ended-state seek should be accepted, but returned {FormatResult(seekResult)}."
            );
            Assert.Equal(PlaybackState.Paused, controller.State);

            await WaitForConditionAsync(
                rowId,
                () => probe.SeekTransitions.Count > seekTransitionsBeforeSeek,
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected {rowId} to emit seek transitions from Ended, but observed [{RenderSeekTransitions(probe.SeekTransitions)}]."
            );
            await WaitForSeekStateAsync(rowId, controller, SeekState.NotSeeking);

            var transitionsAfterSeek = probe
                .PlaybackTransitions.Skip(playbackTransitionsBeforeSeek)
                .ToArray();
            Assert.Contains(
                transitionsAfterSeek,
                transition =>
                    transition.Previous == PlaybackState.Ended
                    && transition.Current == PlaybackState.Loading
            );
            Assert.Contains(
                transitionsAfterSeek,
                transition =>
                    transition.Previous == PlaybackState.Loading
                    && transition.Current == PlaybackState.Paused
            );
            Assert.Equal(PlaybackState.Paused, controller.State);
            Assert.Empty(probe.Errors);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PB006_StopThenLoadPlay_ReusesQuiescedSinks()
    {
        const string rowId = "PB006";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync(initial)", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync(initial)", controller.PlayAsync());
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );

            var deactivateBeforeStop = audioSink.DeactivateCount;
            await AssertSuccessAsync(rowId, "UnloadAsync", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);

            Assert.False(
                audioSink.IsActive,
                $"{rowId}: Expected stop teardown to leave the reused audio sink inactive, but activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}."
            );
            Assert.True(
                audioSink.DeactivateCount > deactivateBeforeStop,
                $"{rowId}: Expected stop teardown to deactivate the reused audio sink, but deactivate count stayed at {audioSink.DeactivateCount}."
            );
            Assert.True(
                videoSink.IsDrained,
                $"{rowId}: Expected stop teardown to drain the reused video sink, but submitted={videoSink.SubmittedFrameCount}, processed={videoSink.ProcessedFrameCount}, pending={videoSink.HasPendingFrame}."
            );

            var playbackTransitionsBeforeReload = probe.PlaybackTransitions.Count;
            var activateBeforeReload = audioSink.ActivateCount;
            var processedBeforeReload = videoSink.ProcessedFrameCount;

            await AssertSuccessAsync(rowId, "LoadAsync(reload)", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync(reload)", controller.PlayAsync());
            await WaitForConditionAsync(
                rowId,
                () =>
                    controller.State == PlaybackState.Playing
                    && audioSink.ActivateCount > activateBeforeReload
                    && videoSink.ProcessedFrameCount > processedBeforeReload,
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected stop->load->play recovery to reactivate reused sinks, but state={controller.State}, activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}, processed={videoSink.ProcessedFrameCount}, baseline={processedBeforeReload}."
            );

            var transitionsAfterReload = probe
                .PlaybackTransitions.Skip(playbackTransitionsBeforeReload)
                .ToArray();
            Assert.Contains(
                transitionsAfterReload,
                transition =>
                    transition.Previous == PlaybackState.Unloaded
                    && transition.Current == PlaybackState.Loading
            );
            Assert.Contains(
                transitionsAfterReload,
                transition =>
                    transition.Previous == PlaybackState.Paused
                    && transition.Current == PlaybackState.Playing
            );
            Assert.Empty(probe.Errors);

            await AssertSuccessAsync(rowId, "UnloadAsync(cleanup)", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task XR010_EndedReplay_RebuildsRuntime_OnReusedQuiescedSinks()
    {
        const string rowId = "XR010";
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            await AssertSuccessAsync(rowId, "PlayAsync", controller.PlayAsync());
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(250),
                minimumVideoFrames: 2
            );
            await WaitForPlaybackStateAsync(
                rowId,
                controller,
                PlaybackState.Ended,
                timeoutMilliseconds: 30000
            );
            await videoSink.WaitForDrainAsync();

            var playbackTransitionsBeforeReplay = probe.PlaybackTransitions.Count;
            var activateBeforeReplay = audioSink.ActivateCount;
            var deactivateBeforeReplay = audioSink.DeactivateCount;
            var processedBeforeReplay = videoSink.ProcessedFrameCount;

            await AssertSuccessAsync(rowId, "PlayAsync(replay)", controller.PlayAsync());
            await WaitForConditionAsync(
                rowId,
                () =>
                    controller.State == PlaybackState.Playing
                    && audioSink.ActivateCount > activateBeforeReplay
                    && audioSink.DeactivateCount > deactivateBeforeReplay
                    && videoSink.ProcessedFrameCount > processedBeforeReplay,
                timeoutMilliseconds: 15000,
                failureMessage: () =>
                    $"Expected replay recovery to rebuild runtime ownership on reused sinks, but state={controller.State}, activate={audioSink.ActivateCount}, deactivate={audioSink.DeactivateCount}, processed={videoSink.ProcessedFrameCount}, baseline={processedBeforeReplay}."
            );

            var transitionsAfterReplay = probe
                .PlaybackTransitions.Skip(playbackTransitionsBeforeReplay)
                .ToArray();
            Assert.Contains(
                transitionsAfterReplay,
                transition =>
                    transition.Previous == PlaybackState.Ended
                    && transition.Current == PlaybackState.Unloaded
            );
            Assert.Contains(
                transitionsAfterReplay,
                transition =>
                    transition.Previous == PlaybackState.Unloaded
                    && transition.Current == PlaybackState.Loading
            );
            Assert.Contains(
                transitionsAfterReplay,
                transition =>
                    transition.Previous == PlaybackState.Paused
                    && transition.Current == PlaybackState.Playing
            );
            Assert.Empty(probe.Errors);

            await AssertSuccessAsync(rowId, "UnloadAsync(cleanup)", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    private static async Task AssertLoopRestartAsync(string rowId, RepeatMode repeatMode)
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            using var probe = new ContractProbe(controller);
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            await AssertSuccessAsync(rowId, "LoadAsync", controller.LoadAsync(source));
            await AssertSuccessAsync(
                rowId,
                $"SetRepeatModeAsync({repeatMode})",
                controller.SetRepeatModeAsync(repeatMode)
            );
            await AssertSuccessAsync(rowId, "PlayAsync", controller.PlayAsync());
            await WaitForPlaybackProgressAsync(
                rowId,
                controller,
                videoSink,
                minimumPosition: TimeSpan.FromMilliseconds(400),
                minimumVideoFrames: 4
            );

            var framesBeforeLoop = videoSink.FrameCount;
            await WaitForConditionAsync(
                rowId,
                () => probe.LoopRestarts.Count >= 1,
                timeoutMilliseconds: 20000,
                failureMessage: () =>
                    $"Expected {rowId} to emit LoopRestarted, but loop count stayed at {probe.LoopRestarts.Count} and playback transitions were [{RenderPlaybackTransitions(probe.PlaybackTransitions)}]."
            );

            var loop = probe.LoopRestarts[0];
            Assert.Equal(1, loop.LoopCount);
            Assert.True(
                loop.ItemDuration > TimeSpan.Zero,
                $"{rowId}: Expected LoopRestarted to report a positive item duration, but got {loop.ItemDuration}."
            );
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.True(
                probe.PlaybackTransitions.All(transition =>
                    transition.Current != PlaybackState.Ended
                ),
                $"{rowId}: Expected repeat-mode loop restart to avoid Ended before stop, but observed [{RenderPlaybackTransitions(probe.PlaybackTransitions)}]."
            );
            Assert.Empty(probe.Errors);
            Assert.True(
                audioSink.DeactivateCount >= 1,
                $"{rowId}: Expected at least one audio deactivation during loop restart, but got {audioSink.DeactivateCount}."
            );
            Assert.True(
                audioSink.ActivateCount >= 2,
                $"{rowId}: Expected loop restart to reactivate audio (initial + restart), but got {audioSink.ActivateCount}."
            );

            await WaitForConditionAsync(
                rowId,
                () => videoSink.FrameCount > framesBeforeLoop,
                timeoutMilliseconds: 5000,
                failureMessage: () =>
                    $"Expected {rowId} to continue presenting frames after loop restart, but frame count stayed at {videoSink.FrameCount} (baseline {framesBeforeLoop})."
            );

            await AssertSuccessAsync(rowId, "UnloadAsync", controller.UnloadAsync());
            await WaitForPlaybackStateAsync(rowId, controller, PlaybackState.Unloaded);
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
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
            transitions.Select(static transition => $"{transition.Previous}->{transition.Current}")
        );

    private static string RenderSeekTransitions(
        IEnumerable<StateTransition<SeekState>> transitions
    ) =>
        string.Join(
            ", ",
            transitions.Select(static transition => $"{transition.Previous}->{transition.Current}")
        );

    private static string FormatResult(Result result) =>
        result.IsSuccess
            ? "success"
            : $"failure ({result.Error?.Category}: {result.Error?.Message})";

    private sealed class ContractProbe : IDisposable
    {
        private readonly IDisposable _playbackSubscription;
        private readonly IDisposable _seekSubscription;
        private readonly IDisposable _loopSubscription;
        private readonly IDisposable _errorSubscription;

        public ContractProbe(IPlaybackController controller)
        {
            _playbackSubscription = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(PlaybackTransitions.Add)
            );
            _seekSubscription = controller.SeekStateChanged.Subscribe(
                new ActionObserver<StateTransition<SeekState>>(SeekTransitions.Add)
            );
            _loopSubscription = controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(LoopRestarts.Add)
            );
            _errorSubscription = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(Errors.Add)
            );
        }

        public List<StateTransition<PlaybackState>> PlaybackTransitions { get; } = [];

        public List<StateTransition<SeekState>> SeekTransitions { get; } = [];

        public List<LoopRestarted> LoopRestarts { get; } = [];

        public List<PlaybackError> Errors { get; } = [];

        public void Dispose()
        {
            _playbackSubscription.Dispose();
            _seekSubscription.Dispose();
            _loopSubscription.Dispose();
            _errorSubscription.Dispose();
        }
    }
}
