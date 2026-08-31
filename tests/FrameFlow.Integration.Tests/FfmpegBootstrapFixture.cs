using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// XUnit collection fixture that bootstraps FFmpeg before integration tests run.
/// Follows the same pattern as the decoding test bootstrap fixture.
/// </summary>
public sealed class FfmpegBootstrapFixture : IDisposable
{
    // Process-singleton bootstrap. Each test class declares
    // IClassFixture<FfmpegBootstrapFixture>, which means xUnit creates
    // ONE fixture instance per class — and with class-level parallelism
    // enabled (the default once we dropped [Collection]) those
    // constructors race. The underlying FFmpeg HardwareDecodeProbe
    // calls av_hwdevice_ctx_create, which is NOT thread-safe — running
    // it from multiple fixtures concurrently produced an access
    // violation (0xC0000005) that aborted the whole test run.
    //
    // The gate below makes the actual bootstrap work happen exactly
    // once per process regardless of how many fixture instances spin
    // up. Subsequent fixtures block briefly, read the cached result,
    // and return. The FFmpeg loader itself already caches at the
    // process level (DllImportResolver registered once, libraries
    // loaded once); this gate just synchronizes the HW-probe path
    // that runs as part of FrameFlowBootstrapper.Initialize().
    private static readonly object _gate = new();
    private static bool? _cachedIsBootstrapped;

    /// <summary>
    /// <see langword="true"/> when FFmpeg was successfully bootstrapped.
    /// Integration tests should skip when this is <see langword="false"/>.
    /// </summary>
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
        var libraryDir = IntegrationTestEnvironment.FindFfmpegLibraryDirectory();
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

/// <summary>
/// Locates test corpus files and detects FFmpeg availability for integration tests.
/// </summary>
internal static class IntegrationTestEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> CachedHasFfmpegLibraries = new(DetectFfmpegSharedLibraries);

    /// <summary>Path to tests/corpus/files/.</summary>
    internal static string CorpusDir =>
        Path.Combine(CachedRepoRoot.Value, "tests", "corpus", "files");

    /// <summary>Path to tests/corpus/test-expectations.json.</summary>
    internal static string ExpectationsPath =>
        Path.Combine(CachedRepoRoot.Value, "tests", "corpus", "test-expectations.json");

    /// <summary>
    /// <see langword="true"/> when FFmpeg shared libraries are loadable.
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
        return FindFfmpegLibraryDirectory() is not null;
    }
}

/// <summary>
/// XUnit fact that is skipped when FFmpeg shared libraries or corpus files are unavailable.
/// Integration tests use this attribute instead of bare [Fact] so they skip cleanly
/// in environments without FFmpeg or corpus data.
/// </summary>
internal sealed class RequiresFfmpegAndCorpusFactAttribute : FactAttribute
{
    public RequiresFfmpegAndCorpusFactAttribute()
    {
        if (!IntegrationTestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!IntegrationTestEnvironment.HasCorpusFiles)
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
    }
}

/// <summary>
/// Theory-shaped sibling of <see cref="RequiresFfmpegAndCorpusFactAttribute"/>
/// for parameterised integration tests (e.g. XR001 driven by
/// <c>[InlineData]</c> across multiple corpus files). xUnit v2's
/// <see cref="FactAttribute"/> doesn't apply to <see cref="TheoryAttribute"/>
/// — they're distinct attribute hierarchies — so the Skip plumbing
/// has to be duplicated rather than reused.
/// </summary>
internal sealed class RequiresFfmpegAndCorpusTheoryAttribute : TheoryAttribute
{
    public RequiresFfmpegAndCorpusTheoryAttribute()
    {
        if (!IntegrationTestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!IntegrationTestEnvironment.HasCorpusFiles)
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
    }
}

/// <summary>
/// XUnit fact attribute that skips the test when the <c>FRAMEFLOW_VISUAL_TESTS</c>
/// environment variable is not set to a truthy value (<c>1</c> or <c>true</c>),
/// or when FFmpeg / corpus prerequisites are not met.
/// </summary>
/// <remarks>
/// Visual tests open real OS windows and render frames to screen. They are useful
/// for local validation but must not run in headless CI. The environment gate
/// ensures they only execute when explicitly opted-in.
/// </remarks>
internal sealed class VisualTestFactAttribute : FactAttribute
{
    public VisualTestFactAttribute()
    {
        var envValue = Environment.GetEnvironmentVariable("FRAMEFLOW_VISUAL_TESTS");
        if (!IsTruthy(envValue))
        {
            Skip = "Visual tests disabled. Set FRAMEFLOW_VISUAL_TESTS=1 to enable.";
            return;
        }

        if (!IntegrationTestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!IntegrationTestEnvironment.HasCorpusFiles)
        {
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (
            value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        );
}
