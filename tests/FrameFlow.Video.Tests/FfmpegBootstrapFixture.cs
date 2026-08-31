using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Video.Tests;

/// <summary>
/// Bootstraps FFmpeg once per test run so the video converter tests
/// can call into libswscale. Mirrors the equivalent fixture in
/// <c>FrameFlow.Audio.Tests</c>.
/// </summary>
public sealed class FfmpegBootstrapFixture : IDisposable
{
    // Process-singleton bootstrap. See FrameFlow.Audio.Tests'
    // FfmpegBootstrapFixture for the rationale (HardwareDecodeProbe
    // calls av_hwdevice_ctx_create which is not thread-safe).
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
