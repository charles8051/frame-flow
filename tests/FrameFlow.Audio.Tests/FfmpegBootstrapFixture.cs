using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Bootstraps FFmpeg once per test run so the audio resampler tests can
/// call into libswresample. Mirrors the equivalent fixture in the
/// decoding test project.
/// </summary>
public sealed class FfmpegBootstrapFixture : IDisposable
{
    // Process-singleton bootstrap. Each test class declares
    // IClassFixture<FfmpegBootstrapFixture> so xUnit creates one
    // instance per class, and with class-level parallelism enabled
    // those constructors race. FFmpeg's HardwareDecodeProbe calls
    // av_hwdevice_ctx_create which is NOT thread-safe — concurrent
    // invocations produced 0xC0000005 access violations that aborted
    // the test run. The gate makes the real work happen once per
    // process; subsequent fixtures read the cached result.
    private static readonly object _gate = new();
    private static bool? _cachedIsBootstrapped;

    public bool IsBootstrapped { get; }

    public FfmpegBootstrapFixture()
    {
        lock (_gate)
        {
            _cachedIsBootstrapped ??= TryBootstrap();
            IsBootstrapped = _cachedIsBootstrapped.Value;
        }
    }

    private static bool TryBootstrap()
    {
        var libraryDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (libraryDir is null)
            return false;

        try
        {
            var options = new FrameFlowNativeOptions { CustomFfmpegPath = libraryDir };
            var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);
            return bootstrapper.Initialize().IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() { }
}

internal static class TestEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> CachedHasFfmpegLibraries = new(DetectFfmpegSharedLibraries);

    internal static bool HasFfmpegSharedLibraries => CachedHasFfmpegLibraries.Value;

    internal static string? FindFfmpegLibraryDirectory()
    {
        var repoRoot = CachedRepoRoot.Value;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var archLabel = arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        string rid;
        if (OperatingSystem.IsWindows())
            rid = $"win-{archLabel}";
        else if (OperatingSystem.IsMacOS())
            rid = $"osx-{archLabel}";
        else
            rid = $"linux-{archLabel}";

        var nativeDir = Path.Combine(repoRoot, "runtimes", rid, "native");
        var libName =
            OperatingSystem.IsWindows() ? "avutil-59.dll"
            : OperatingSystem.IsMacOS() ? "libavutil.59.dylib"
            : "libavutil.so.59";

        if (File.Exists(Path.Combine(nativeDir, libName)))
            return nativeDir;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, libName)))
                return dir;
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
        );
    }

    private static bool DetectFfmpegSharedLibraries() => FindFfmpegLibraryDirectory() is not null;
}

internal sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
            Skip = "FFmpeg shared libraries not available. Run scripts/fetch-ffmpeg.cs first.";
    }
}

/// <summary>
/// Opt-in gate for tests that interact with a real audio device through
/// OpenAL Soft. Headless CI runners technically have OpenAL Soft
/// available but its device-paced playback can stall indefinitely (the
/// device-pacing path in v0.1.0 hung for 5+ min on
/// <c>ReActivation_DevicePacedPlaybackMatchesFirstIteration</c>), so
/// these tests skip unless <c>FRAMEFLOW_AUDIO_DEVICE_TESTS=1</c> is set
/// to opt in — matching the <c>VisualTestFact</c> pattern in
/// FrameFlow.Integration.Tests. Tracked in docs/DEFERRED_WORK.md as the
/// "<c>RequiresAudioDeviceFact</c>" follow-up.
/// </summary>
internal sealed class RequiresAudioDeviceFactAttribute : FactAttribute
{
    public RequiresAudioDeviceFactAttribute()
    {
        var envValue = Environment.GetEnvironmentVariable("FRAMEFLOW_AUDIO_DEVICE_TESTS");
        if (!IsTruthy(envValue))
            Skip =
                "Audio device tests disabled. Set FRAMEFLOW_AUDIO_DEVICE_TESTS=1 to enable.";
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (
            value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        );
}
