namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Locates test corpus files and detects FFmpeg availability for decoding integration tests.
/// </summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> CachedHasFfmpegLibraries = new(DetectFfmpegSharedLibraries);

    /// <summary>Path to tests/corpus/files/.</summary>
    internal static string CorpusDir =>
        Path.Combine(CachedRepoRoot.Value, "tests", "corpus", "files");

    /// <summary>
    /// <see langword="true"/> when FFmpeg shared libraries are loadable in this environment.
    /// Integration tests that call FFmpeg P/Invoke functions must check this before running.
    /// </summary>
    internal static bool HasFfmpegSharedLibraries => CachedHasFfmpegLibraries.Value;

    /// <summary>
    /// <see langword="true"/> when the corpus directory contains at least one media file.
    /// </summary>
    internal static bool HasCorpusFiles =>
        Directory.Exists(CorpusDir) && Directory.EnumerateFiles(CorpusDir).Any();

    /// <summary>Returns the full path to a named corpus file, or null when absent.</summary>
    internal static string? GetCorpusFile(string name)
    {
        var path = Path.Combine(CorpusDir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns the directory that contains the FFmpeg shared libraries, or
    /// <see langword="null"/> when no library directory can be located.
    /// </summary>
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

    private static bool DetectFfmpegSharedLibraries()
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
            return true;

        // Also check PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, libName)))
                return true;
        }

        return false;
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

/// <summary>
/// XUnit fact that is skipped when FFmpeg shared libraries or corpus files are unavailable.
/// </summary>
internal sealed class RequiresFfmpegAndCorpusFactAttribute : FactAttribute
{
    public RequiresFfmpegAndCorpusFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!TestEnvironment.HasCorpusFiles)
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
    }
}

/// <summary>
/// As <see cref="RequiresFfmpegAndCorpusFactAttribute"/>, plus a named corpus file
/// that the default corpus does not contain.
/// </summary>
/// <remarks>
/// The point is that the run says so. The repo-wide pattern for a missing fixture is
/// <c>if (file is null) return;</c> inside the test body, which xunit reports as
/// <em>passed</em> — so a green suite reads as coverage whether or not the fixture
/// was there. For a fixture that is opt-in by design, that is the difference between
/// a test nobody runs and a test nobody knows nobody runs.
/// </remarks>
internal sealed class RequiresCorpusFileFactAttribute : FactAttribute
{
    public RequiresCorpusFileFactAttribute(string fileName, string howToGenerate)
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (TestEnvironment.GetCorpusFile(fileName) is null)
            Skip = $"Corpus file '{fileName}' not present. {howToGenerate}";
    }
}
