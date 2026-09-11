// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.ColdStart.Tests;

/// <summary>
/// Locates the corpus and the staged FFmpeg binaries without touching any FrameFlow type.
/// </summary>
/// <remarks>
/// Deliberately a copy of the parts of the other projects' <c>TestEnvironment</c> this project
/// needs, rather than a reference to one of them. Every type this assembly loads is a type that
/// has run before the cold-start tests do, and the point of the project is that none of them
/// have bootstrapped anything.
/// </remarks>
internal static class ColdStartEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);

    internal static string CorpusDir =>
        Path.Combine(CachedRepoRoot.Value, "tests", "corpus", "files");

    internal static bool HasCorpusFiles =>
        Directory.Exists(CorpusDir) && Directory.EnumerateFiles(CorpusDir).Any();

    internal static string? GetCorpusFile(string name)
    {
        var path = Path.Combine(CorpusDir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// <see langword="true"/> when a default bootstrap could find FFmpeg: either staged under
    /// the test binary's <c>runtimes/{rid}/native/</c>, or on PATH for the system probe.
    /// </summary>
    internal static bool HasFfmpegSharedLibraries
    {
        get
        {
            var libName =
                OperatingSystem.IsWindows() ? "avutil-59.dll"
                : OperatingSystem.IsMacOS() ? "libavutil.59.dylib"
                : "libavutil.so.59";

            var bundled = Path.Combine(
                AppContext.BaseDirectory,
                "runtimes",
                CurrentRid,
                "native",
                libName
            );

            if (File.Exists(bundled))
                return true;

            var pathDirs =
                Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

            return pathDirs.Any(dir => File.Exists(Path.Combine(dir, libName)));
        }
    }

    private static string CurrentRid
    {
        get
        {
            var archLabel =
                RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

            if (OperatingSystem.IsWindows())
                return $"win-{archLabel}";
            if (OperatingSystem.IsMacOS())
                return $"osx-{archLabel}";
            return $"linux-{archLabel}";
        }
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
}

/// <summary>
/// XUnit fact that is skipped when FFmpeg or the corpus is unavailable.
/// </summary>
internal sealed class RequiresFfmpegAndCorpusFactAttribute : FactAttribute
{
    public RequiresFfmpegAndCorpusFactAttribute()
    {
        if (!ColdStartEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available. Run scripts/fetch-ffmpeg.cs.";
            return;
        }

        if (!ColdStartEnvironment.HasCorpusFiles)
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
    }
}
