using System.Runtime.InteropServices;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Locates FFmpeg shared libraries and the bundled <c>ffprobe</c> tool for the
/// encoder round-trip tests. Mirrors the helper in
/// <c>FrameFlow.Decoding.Tests</c>.
/// </summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);

    /// <summary>Native runtime directory for the current RID (or null when absent).</summary>
    internal static string? NativeRuntimeDir => CachedNativeDir.Value;

    private static readonly Lazy<string?> CachedNativeDir = new(FindNativeRuntimeDir);

    /// <summary><see langword="true"/> when FFmpeg shared libraries are loadable.</summary>
    internal static bool HasFfmpegSharedLibraries => NativeRuntimeDir is not null;

    /// <summary>Path to the bundled <c>ffprobe</c> executable, or null when absent.</summary>
    internal static string? FfprobePath
    {
        get
        {
            var dir = NativeRuntimeDir;
            if (dir is null)
                return null;
            var exe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            var path = Path.Combine(dir, exe);
            return File.Exists(path) ? path : null;
        }
    }

    internal static string? FindFfmpegLibraryDirectory() => NativeRuntimeDir;

    private static string Rid()
    {
        var arch = RuntimeInformation.OSArchitecture;
        var archLabel = arch == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows())
            return $"win-{archLabel}";
        if (OperatingSystem.IsMacOS())
            return $"osx-{archLabel}";
        return $"linux-{archLabel}";
    }

    private static string LibName() =>
        OperatingSystem.IsWindows() ? "avutil-59.dll"
        : OperatingSystem.IsMacOS() ? "libavutil.59.dylib"
        : "libavutil.so.59";

    private static string? FindNativeRuntimeDir()
    {
        var nativeDir = Path.Combine(CachedRepoRoot.Value, "runtimes", Rid(), "native");
        if (File.Exists(Path.Combine(nativeDir, LibName())))
            return nativeDir;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, LibName())))
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
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}

/// <summary>
/// XUnit fact that is skipped when FFmpeg shared libraries are not available.
/// </summary>
internal sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
            Skip =
                "FFmpeg shared libraries not available. "
                + "Run scripts/fetch-ffmpeg.cs or install FFmpeg with shared libraries.";
    }
}
