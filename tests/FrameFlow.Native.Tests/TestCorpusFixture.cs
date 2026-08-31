namespace FrameFlow.Native.Tests;

/// <summary>
/// Locates the test corpus directory and FFmpeg binaries for integration tests.
/// Tests that depend on generated corpus files or FFmpeg availability should
/// use <see cref="RequiresFfmpegFactAttribute"/> or <see cref="RequiresCorpusFactAttribute"/>.
/// </summary>
public static class TestEnvironment
{
    private static readonly Lazy<string?> CachedFfmpegPath = new(FindFfmpeg);
    private static readonly Lazy<bool> CachedHasFfmpegLibraries = new(DetectFfmpegSharedLibraries);
    private static readonly Lazy<string> CachedCorpusDir = new(FindCorpusDir);
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);

    /// <summary>Path to the ffmpeg executable, or null if not found.</summary>
    public static string? FfmpegPath => CachedFfmpegPath.Value;

    /// <summary>Path to tests/corpus/files/ directory.</summary>
    public static string CorpusDir => CachedCorpusDir.Value;

    /// <summary>Path to the repository root.</summary>
    public static string RepoRoot => CachedRepoRoot.Value;

    /// <summary>
    /// True if FFmpeg shared libraries (e.g. avutil-59.dll, libavutil.so.59) are
    /// loadable on this machine. This is a stronger check than <see cref="FfmpegPath"/>:
    /// a static ffmpeg executable does not imply that the shared libraries are present.
    /// Integration tests that require <see cref="NativeLibrary.Load"/> to succeed must
    /// check this flag rather than just <see cref="FfmpegPath"/>.
    /// </summary>
    public static bool HasFfmpegSharedLibraries => CachedHasFfmpegLibraries.Value;

    /// <summary>
    /// True if the ffmpeg executable is available (used for corpus generation tests).
    /// For tests that need shared-library loading, use <see cref="HasFfmpegSharedLibraries"/>.
    /// </summary>
    public static bool HasFfmpeg => FfmpegPath is not null;

    /// <summary>True if the corpus directory has at least one media file.</summary>
    public static bool HasCorpusFiles =>
        Directory.Exists(CorpusDir) && Directory.EnumerateFiles(CorpusDir).Any();

    /// <summary>
    /// Gets the path to the runtimes/{rid}/native directory for the current platform.
    /// </summary>
    public static string NativeDir
    {
        get
        {
            var rid = GetCurrentRid();
            return Path.Combine(RepoRoot, "runtimes", rid, "native");
        }
    }

    /// <summary>Returns the corpus file path, or null if it doesn't exist.</summary>
    public static string? GetCorpusFile(string fileName)
    {
        var path = Path.Combine(CorpusDir, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns the directory that contains the FFmpeg shared libraries,
    /// or null if no shared libraries can be found.
    /// </summary>
    public static string? FindFfmpegLibraryDirectory()
    {
        // Check runtimes/{rid}/native/ first.
        if (Directory.Exists(NativeDir))
        {
            var libName = GetAvutilSharedLibraryName();
            if (File.Exists(Path.Combine(NativeDir, libName)))
                return NativeDir;
        }

        // Check directories on PATH.
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var libName2 = GetAvutilSharedLibraryName();
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, libName2)))
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

        // Fallback: assume we're somewhere under the repo
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
        );
    }

    private static readonly Lazy<string?> CachedFfprobe = new(FindFfprobeCore);

    /// <summary>
    /// Path to ffprobe, or <see langword="null"/> when it is not available.
    /// Probing is an optional dependency: tests that need it should skip rather
    /// than fail, and rather than return early — an early return records a pass
    /// and hides the missing coverage from the runner's skip count.
    /// </summary>
    public static string? FfprobePath => CachedFfprobe.Value;

    public static bool HasFfprobe => FfprobePath is not null;

    private static string? FindFfprobeCore()
    {
        if (FfmpegPath is null)
            return null;

        var exeExt = OperatingSystem.IsWindows() ? ".exe" : "";

        // Prefer PATH first — system-installed ffprobe has its dependencies resolved.
        // The runtimes/{rid}/native/ copy may not find its shared libraries when run
        // from an arbitrary working directory.
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var d in pathDirs)
        {
            var candidate = Path.Combine(d, $"ffprobe{exeExt}");
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to ffprobe alongside ffmpeg
        var dir = Path.GetDirectoryName(FfmpegPath)!;
        var probe = Path.Combine(dir, $"ffprobe{exeExt}");
        if (File.Exists(probe))
            return probe;

        return null;
    }

    private static string FindCorpusDir() => Path.Combine(RepoRoot, "tests", "corpus", "files");

    private static string? FindFfmpeg()
    {
        // Check runtimes/{rid}/native/ first (fetched by scripts/fetch-ffmpeg.cs)
        var exeExt = OperatingSystem.IsWindows() ? ".exe" : "";
        var nativeCandidate = Path.Combine(NativeDir, $"ffmpeg{exeExt}");
        if (File.Exists(nativeCandidate))
            return nativeCandidate;

        // Check PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, $"ffmpeg{exeExt}");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool DetectFfmpegSharedLibraries()
    {
        return FindFfmpegLibraryDirectory() is not null;
    }

    private static string GetAvutilSharedLibraryName()
    {
        // Try both FFmpeg 7.x and 8.x avutil names.
        if (OperatingSystem.IsWindows())
            return "avutil-59.dll"; // FFmpeg 7.x; 8.x would be avutil-60.dll

        if (OperatingSystem.IsMacOS())
            return "libavutil.59.dylib";

        return "libavutil.so.59";
    }

    private static string GetCurrentRid()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var archLabel = arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        if (OperatingSystem.IsWindows())
            return $"win-{archLabel}";
        if (OperatingSystem.IsMacOS())
            return $"osx-{archLabel}";
        if (OperatingSystem.IsLinux())
            return $"linux-{archLabel}";

        return $"unknown-{archLabel}";
    }
}

/// <summary>
/// Marks a test that requires FFmpeg shared libraries to be installed and loadable.
/// Skips if FFmpeg shared libraries are not available.
/// </summary>
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
            Skip =
                "FFmpeg shared libraries not available. "
                + "Run scripts/fetch-ffmpeg.cs or install FFmpeg with shared libraries. "
                + $"Expected {(OperatingSystem.IsWindows() ? "avutil-59.dll" : "libavutil.so.59")} "
                + "on PATH or in runtimes/{rid}/native/.";
    }
}

/// <summary>
/// Marks a test that requires one <i>named</i> corpus fixture, and skips —
/// rather than passing — when that fixture is absent.
/// </summary>
/// <remarks>
/// <see cref="RequiresCorpusFactAttribute"/> gates on the corpus directory
/// holding some file, which is not the same question. A test that returns
/// early on a missing fixture is recorded as passed, so the aggregate skip
/// count cannot show that the coverage was lost. That matters most for the
/// fixtures an LGPL FFmpeg build cannot produce (4:4:4 chroma, B-frames): they
/// are exactly the coverage a reader would want flagged, and exactly the
/// coverage an early return hides.
/// </remarks>
public sealed class RequiresCorpusFileFactAttribute : FactAttribute
{
    public RequiresCorpusFileFactAttribute(string fileName, bool requiresFfprobe = false)
    {
        if (requiresFfprobe && !TestEnvironment.HasFfprobe)
        {
            Skip = "ffprobe not available; cannot verify stream properties.";
            return;
        }

        if (!TestEnvironment.HasCorpusFiles)
        {
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
            return;
        }

        if (TestEnvironment.GetCorpusFile(fileName) is null)
        {
            Skip =
                $"Corpus fixture {fileName} was not generated. If it needs a GPL "
                + "encoder (libx264 / libx265), the pinned LGPL runtime cannot "
                + "produce it. Re-run scripts/generate-test-corpus.cs to see which "
                + "fixtures are unavailable and why, or pass a GPL build with "
                + "--ffmpeg <path> to generate them.";
        }
    }
}

/// <summary>
/// Marks a test that requires generated corpus files. Skips if corpus is empty.
/// </summary>
public sealed class RequiresCorpusFactAttribute : FactAttribute
{
    public RequiresCorpusFactAttribute()
    {
        if (!TestEnvironment.HasCorpusFiles)
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
    }
}
