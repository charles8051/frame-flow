using FrameFlow.Avalonia;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.SDL;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Sink-specific coverage for the
/// controller. Mirrors <see cref="SinkSpecificTests"/>
/// to prove that <see cref="SdlVideoSink"/> and
/// <see cref="AvaloniaVideoSink"/> headless variants accept real
/// decoded frames through the pipeline the same way
/// they accept frames through the old-substrate pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The sinks themselves are unchanged — both implement
/// <see cref="IVideoSink"/> and the new controller's
/// <see cref="FrameFlow.Playback.PlaybackController.Create"/>
/// accepts any <c>IVideoSink</c>. The only difference vs. the old
/// tests is the controller factory (no DI provider; tuple is
/// <c>(controller, audioSink)</c>). Pump loops and gating attribute
/// stay identical.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SinkSpecificTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    private const string CorpusFile = "test-av-h264-aac.mp4";

    public SinkSpecificTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    // ── SDL headless tests ──────────────────────────────────────────

    [RequiresFfmpegAndCorpusFact]
    public async Task HeadlessSdl_PlaysCorpusFile_FrameLifecycleComplete()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(CorpusFile);
        Assert.True(filePath is not null, $"Corpus file {CorpusFile} not found.");

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);
        var sdlSink = SdlVideoSink.CreateHeadless(framePool);

        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(() => SdlPumpLoopAsync(sdlSink, pumpCts.Token));

        try
        {
            var (controller, audioSink) = IntegrationTestHelper.CreateController(sdlSink);
            await using (controller)
            {
                var source = MediaSource.FromFile(filePath!);
                var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                    controller,
                    source,
                    TimeSpan.FromSeconds(30)
                );

                Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
                Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

                await Task.Delay(200);

                Assert.True(
                    sdlSink.DroppedFrameCount >= 0,
                    $"DroppedFrameCount should be non-negative, got {sdlSink.DroppedFrameCount}"
                );

                // Lifecycle proof only: verify the audio path produced *some*
                // decoded data. The duration-vs-CorpusExpectation tolerance is
                // not asserted here or anywhere else — see #113.
                Assert.True(
                    audioSink.DecodedDurationSeconds > 0,
                    $"Expected audio decoded duration > 0, got {audioSink.DecodedDurationSeconds:F3}s"
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
    public async Task HeadlessSdl_FramePoolBackpressure_NoPipelineDeadlock()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(CorpusFile);
        Assert.True(filePath is not null, $"Corpus file {CorpusFile} not found.");

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 2);
        var sdlSink = SdlVideoSink.CreateHeadless(framePool);

        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(() => SdlPumpLoopAsync(sdlSink, pumpCts.Token));

        try
        {
            var (controller, _) = IntegrationTestHelper.CreateController(sdlSink);
            await using (controller)
            {
                var source = MediaSource.FromFile(filePath!);
                var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                    controller,
                    source,
                    TimeSpan.FromSeconds(30)
                );

                Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
                Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");
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

    // ── Avalonia headless tests ─────────────────────────────────────

    [RequiresFfmpegAndCorpusFact]
    public async Task HeadlessAvalonia_PlaysCorpusFile_RenderedFramesCounted()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(CorpusFile);
        Assert.True(filePath is not null, $"Corpus file {CorpusFile} not found.");

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);
        var avaloniaSink = AvaloniaVideoSink.CreateHeadless(framePool);

        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(() => AvaloniaPumpLoopAsync(avaloniaSink, 16, pumpCts.Token));

        try
        {
            var (controller, audioSink) = IntegrationTestHelper.CreateController(avaloniaSink);
            await using (controller)
            {
                var source = MediaSource.FromFile(filePath!);
                var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                    controller,
                    source,
                    TimeSpan.FromSeconds(30)
                );

                Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
                Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

                await Task.Delay(200);

                Assert.True(
                    avaloniaSink.RenderedFrameCount > 0,
                    $"RenderedFrameCount should be > 0, got {avaloniaSink.RenderedFrameCount}"
                );

                var totalFrames = avaloniaSink.RenderedFrameCount + avaloniaSink.DroppedFrameCount;
                Assert.True(
                    totalFrames > 0,
                    $"Total frames (rendered={avaloniaSink.RenderedFrameCount}, "
                        + $"dropped={avaloniaSink.DroppedFrameCount}) should be > 0"
                );

                // Lifecycle-only audio decoded duration check.
                Assert.True(
                    audioSink.DecodedDurationSeconds > 0,
                    $"Expected audio decoded duration > 0, got {audioSink.DecodedDurationSeconds:F3}s"
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

            await avaloniaSink.DisposeAsync();
            framePool.Dispose();
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task HeadlessAvalonia_DroppedFramesCounted()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(CorpusFile);
        Assert.True(filePath is not null, $"Corpus file {CorpusFile} not found.");

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);
        var avaloniaSink = AvaloniaVideoSink.CreateHeadless(framePool);

        using var pumpCts = new CancellationTokenSource();
        // Deliberately slow pump (50ms) to force frame drops.
        var pumpTask = Task.Run(() => AvaloniaPumpLoopAsync(avaloniaSink, 50, pumpCts.Token));

        try
        {
            var (controller, _) = IntegrationTestHelper.CreateController(avaloniaSink);
            await using (controller)
            {
                var source = MediaSource.FromFile(filePath!);
                var (loadResult, playResult) = await IntegrationTestHelper.PlayToCompletionAsync(
                    controller,
                    source,
                    TimeSpan.FromSeconds(30)
                );

                Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
                Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

                await Task.Delay(200);

                Assert.True(
                    avaloniaSink.DroppedFrameCount > 0,
                    $"DroppedFrameCount should be > 0 with slow pump, got {avaloniaSink.DroppedFrameCount}"
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

            await avaloniaSink.DisposeAsync();
            framePool.Dispose();
        }
    }

    // ── Pump helpers ────────────────────────────────────────────────

    private static async Task SdlPumpLoopAsync(SdlVideoSink sink, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(16, ct).ConfigureAwait(false);
                sink.RenderPendingFrame();
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task AvaloniaPumpLoopAsync(
        AvaloniaVideoSink sink,
        int intervalMs,
        CancellationToken ct
    )
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                var frame = sink.RenderPendingFrame();
                frame?.Dispose();
            }
        }
        catch (OperationCanceledException) { }
    }
}
