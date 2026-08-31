using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.SDL;
using Microsoft.Extensions.Logging.Abstractions;
using SilkSdl = Silk.NET.SDL.Sdl;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Visual SDL coverage for the
/// controller against <see cref="FrameFlow.Playback.PlaybackController"/>:
/// opens a real SDL window, plays a corpus file through the new
/// substrate, asserts frames are rendered. Gated by the same
/// <c>FRAMEFLOW_VISUAL_TESTS</c> environment variable as the
/// old test.
/// </summary>
/// <remarks>
/// The entire SDL lifecycle runs on a dedicated thread per SDL's
/// thread-affinity requirement. The only difference vs. the old
/// visual test is the controller factory and the resulting tuple
/// shape (no DI provider).
/// </remarks>
[Trait("Category", "Visual")]
public sealed class VisualSdlTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string CorpusFile = "test-av-h264-aac.mp4";

    [VisualTestFact]
    public async Task VisualSdl_PlaysCorpusFile_RendersToWindow()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(CorpusFile);
        Assert.True(filePath is not null, $"Corpus file {CorpusFile} not found.");

        var renderedFrameCount = 0;
        Exception? sdlThreadException = null;
        var sdlThreadDone = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var sdlThread = new System.Threading.Thread(() =>
        {
            try
            {
                RunSdlVisualTest(filePath!, out renderedFrameCount);
            }
            catch (DllNotFoundException ex)
            {
                sdlThreadException = ex;
            }
            catch (Exception ex)
            {
                sdlThreadException = ex;
            }
            finally
            {
                sdlThreadDone.TrySetResult();
            }
        })
        {
            Name = "FrameFlow-VisualSdlNextTest",
            IsBackground = true,
        };

        sdlThread.Start();

        var completed = await Task.WhenAny(
            sdlThreadDone.Task,
            Task.Delay(TimeSpan.FromSeconds(45))
        );

        Assert.True(
            completed == sdlThreadDone.Task,
            "Visual SDL test timed out after 45 seconds."
        );

        if (sdlThreadException is DllNotFoundException)
            return;

        if (sdlThreadException is not null)
            throw new AggregateException(
                "Visual SDL test failed on the SDL thread.",
                sdlThreadException
            );

        Assert.True(
            renderedFrameCount > 0,
            $"RenderedFrameCount should be > 0 in windowed mode, got {renderedFrameCount}"
        );
    }

    private static void RunSdlVisualTest(string filePath, out int renderedFrameCount)
    {
        renderedFrameCount = 0;

        var sdl = SilkSdl.GetApi();
        try
        {
            var initResult = sdl.Init(SilkSdl.InitVideo);
            if (initResult < 0)
                throw new SdlException("SDL_Init", sdl.GetErrorS());

            var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);
            var sdlSink = new SdlVideoSink(sdl, framePool, "FrameFlow Visual Next Test", 640, 480);

            try
            {
                var (controller, _) = IntegrationTestHelper.CreateController(sdlSink);

                try
                {
                    var source = MediaSource.FromFile(filePath);
                    var playTask = IntegrationTestHelper.PlayToCompletionAsync(
                        controller,
                        source,
                        TimeSpan.FromSeconds(30)
                    );

                    while (!playTask.IsCompleted)
                    {
                        sdlSink.RenderPendingFrame();
                        System.Threading.Thread.Sleep(8);
                    }

                    for (var i = 0; i < 25; i++)
                    {
                        sdlSink.RenderPendingFrame();
                        System.Threading.Thread.Sleep(8);
                    }

                    var (loadResult, playResult) = playTask.GetAwaiter().GetResult();

                    Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
                    Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

                    renderedFrameCount = sdlSink.RenderedFrameCount;
                }
                finally
                {
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            finally
            {
                sdlSink.DestroyResources();
                sdlSink.DisposeAsync().AsTask().GetAwaiter().GetResult();
                framePool.Dispose();
            }
        }
        finally
        {
            sdl.Quit();
            sdl.Dispose();
        }
    }
}
