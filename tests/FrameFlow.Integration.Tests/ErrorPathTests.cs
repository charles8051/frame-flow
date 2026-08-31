using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Error/fault-path coverage of the
/// <see cref="FrameFlow.Playback.PlaybackController"/>.
/// Mirrors <see cref="ErrorPathTests"/> against the same
/// non-existent / corrupt-file inputs and asserts the substrate
/// surfaces the same Error-state contract (failed
/// <c>LoadAsync</c> result → <see cref="PlaybackState.Error"/> →
/// <see cref="IPlaybackController.ErrorOccurred"/> emission).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does not assert.</b> Recovery here means constructing a
/// fresh controller via <c>CreateController()</c>, which allocates fresh
/// sinks with it. Recovery onto <i>reused</i> sinks is therefore not
/// exercised. A DI-backed variant used to cover that; it was deleted with
/// the old controller and has no replacement, so that contract is currently
/// unpinned. Tracked in #114.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ErrorPathTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public ErrorPathTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task LoadNonExistentFile_TransitionsToErrorState()
    {
        const string rowId = "XR005";
        var (controller, _, _) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile("/nonexistent/path/fake.mp4");
            var playbackError = await AssertFailedLoadAndErrorAsync(rowId, controller, source);

            Assert.Equal(ErrorCategory.System, playbackError.Category);
            Assert.False(
                string.IsNullOrWhiteSpace(playbackError.Message),
                $"{rowId}: Expected a non-empty error message for non-existent file."
            );
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task LoadCorruptFile_TransitionsToErrorState()
    {
        const string rowId = "XR005";
        string? tempPath = null;
        try
        {
            tempPath = Path.GetTempFileName();
            var random = new Random(42);
            var garbage = new byte[1024];
            random.NextBytes(garbage);
            await File.WriteAllBytesAsync(tempPath, garbage);

            var (controller, _, _) = IntegrationTestHelper.CreateController();
            await using (controller)
            {
                var source = MediaSource.FromFile(tempPath);
                var playbackError = await AssertFailedLoadAndErrorAsync(rowId, controller, source);

                Assert.Equal(ErrorCategory.System, playbackError.Category);
                Assert.False(
                    string.IsNullOrWhiteSpace(playbackError.Message),
                    $"{rowId}: Expected a non-empty error message for corrupt file."
                );
            }
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    /// <summary>
    /// Recovery contract for the substrate: a load failure leaves
    /// the prior controller in Error, but a freshly constructed
    /// controller (with fresh sinks) can still load and play the
    /// happy-path corpus file. The old-controller test reuses the
    /// failed DI provider's sinks; the new path constructs a fresh
    /// controller from scratch, so this is a substrate-fresh-start
    /// proof rather than a sink-reuse proof.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task FreshController_AfterPriorLoadFailure_LoadsAndPlaysCleanly()
    {
        const string rowId = "XR005";
        var (failedController, _, _) = IntegrationTestHelper.CreateController();
        await using (failedController)
        {
            var bad = MediaSource.FromFile("/nonexistent/path/fake.mp4");
            var failResult = await failedController.LoadAsync(bad);
            Assert.False(
                failResult.IsSuccess,
                $"{rowId}: Expected initial bad-path load to fail."
            );
        }

        // Fresh controller + fresh sinks should be uncontaminated.
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var recoveryErrors = new List<PlaybackError>();
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(recoveryErrors.Add)
            );

            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var loadResult = await controller.LoadAsync(source);
            Assert.True(
                loadResult.IsSuccess,
                $"{rowId}: Fresh-controller recovery LoadAsync should have succeeded, but returned {FormatResult(loadResult)}."
            );

            var playResult = await controller.PlayAsync();
            Assert.True(
                playResult.IsSuccess,
                $"{rowId}: Fresh-controller recovery PlayAsync should have succeeded, but returned {FormatResult(playResult)}."
            );

            await WaitForConditionAsync(
                () => controller.State == PlaybackState.Playing && videoSink.FrameCount > 0,
                timeoutMilliseconds: 15000,
                failureMessage: $"{rowId}: Expected fresh controller to reach Playing with video frames; state={controller.State}, frames={videoSink.FrameCount}."
            );

            Assert.Empty(recoveryErrors);

            var stopResult = await controller.UnloadAsync();
            Assert.True(
                stopResult.IsSuccess,
                $"{rowId}: Expected recovery UnloadAsync to succeed, but returned {FormatResult(stopResult)}."
            );
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    private static async Task<PlaybackError> AssertFailedLoadAndErrorAsync(
        string rowId,
        IPlaybackController controller,
        IMediaSource source
    )
    {
        var errorStateTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var errorDetailsTcs = new TaskCompletionSource<PlaybackError>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        using var stateSub = controller.PlaybackStateChanged.Subscribe(
            new ActionObserver<StateTransition<PlaybackState>>(transition =>
            {
                if (transition.Current == PlaybackState.Error)
                {
                    errorStateTcs.TrySetResult();
                }
            })
        );

        using var errorSub = controller.ErrorOccurred.Subscribe(
            new ActionObserver<PlaybackError>(error =>
            {
                errorDetailsTcs.TrySetResult(error);
            })
        );

        var loadResult = await controller.LoadAsync(source);
        Assert.False(
            loadResult.IsSuccess,
            $"{rowId}: Expected LoadAsync to fail when initialization faults before completion, but got {FormatResult(loadResult)}."
        );
        Assert.Equal(ErrorCategory.System, loadResult.Error?.Category);

        using var stateCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await errorStateTcs.Task.WaitAsync(stateCts.Token);

        using var errorCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var playbackError = await errorDetailsTcs.Task.WaitAsync(errorCts.Token);

        Assert.Equal(PlaybackState.Error, controller.State);
        return playbackError;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        int timeoutMilliseconds,
        string failureMessage
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
                throw new TimeoutException(failureMessage);
            }
        }
    }

    private static string FormatResult(Result result) =>
        result.IsSuccess
            ? "success"
            : $"failure ({result.Error?.Category}: {result.Error?.Message})";
}
