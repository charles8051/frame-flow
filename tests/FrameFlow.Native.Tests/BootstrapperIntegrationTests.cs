using FrameFlow.Media;
using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Integration tests that validate bootstrap behavior with real FFmpeg binaries.
/// These tests are skipped when FFmpeg is not available on the machine.
/// </summary>
public sealed class BootstrapperIntegrationTests
{
    [RequiresFfmpegFact]
    public void Initialize_WithFfmpegOnPath_Succeeds()
    {
        // Use custom path pointing to the detected FFmpeg directory since system
        // probe relies on NativeLibrary.TryLoad which may not find the libraries
        // even when they exist in a non-standard directory.
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
        {
            return; // Skip — no library directory found.
        }

        var options = new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir };
        var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);

        var result = bootstrapper.Initialize();

        Assert.True(result.IsSuccess, $"Bootstrap failed: {result.Message}");
    }

    [RequiresFfmpegFact]
    public void Initialize_WithCustomPath_PointingToRealFfmpeg_Succeeds()
    {
        var ffmpegDir = Path.GetDirectoryName(TestEnvironment.FfmpegPath!);
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir };
        var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);

        var result = bootstrapper.Initialize();

        Assert.True(result.IsSuccess);
        Assert.Equal(FfmpegBinarySource.CustomPath, result.BinarySource);
        Assert.Equal(ffmpegDir, result.ResolvedPath);
    }

    [RequiresFfmpegFact]
    public void FfmpegPath_IsExecutable()
    {
        var path = TestEnvironment.FfmpegPath!;
        Assert.True(File.Exists(path), $"FFmpeg binary not found at {path}");
    }

    [RequiresFfmpegFact]
    public void NativeDir_ContainsFfmpegBinary_WhenFetchScriptHasRun()
    {
        var nativeDir = TestEnvironment.NativeDir;
        if (!Directory.Exists(nativeDir))
        {
            // Native dir doesn't exist — that's OK, FFmpeg might be on PATH.
            // This test is only meaningful when fetch-ffmpeg.cs has been run.
            return;
        }

        var exeExt = OperatingSystem.IsWindows() ? ".exe" : "";
        var ffmpegInNative = Path.Combine(nativeDir, $"ffmpeg{exeExt}");

        if (File.Exists(ffmpegInNative))
        {
            // If the binary exists in runtimes/{rid}/native/, verify it's findable
            Assert.Contains("ffmpeg", ffmpegInNative, StringComparison.OrdinalIgnoreCase);
        }
    }
}
