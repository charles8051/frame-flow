namespace FrameFlow.Playback.Tests;

internal static class TestEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> CachedHasFfmpegLibraries = new(DetectFfmpegSharedLibraries);

    internal static string CorpusDir =>
        Path.Combine(CachedRepoRoot.Value, "tests", "corpus", "files");

    internal static bool HasFfmpegSharedLibraries => CachedHasFfmpegLibraries.Value;

    internal static bool HasCorpusFiles =>
        Directory.Exists(CorpusDir) && Directory.EnumerateFiles(CorpusDir).Any();

    internal static string? GetCorpusFile(string name)
    {
        var path = Path.Combine(CorpusDir, name);
        return File.Exists(path) ? path : null;
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

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, libName)))
                return true;
        }
        return false;
    }
}

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
