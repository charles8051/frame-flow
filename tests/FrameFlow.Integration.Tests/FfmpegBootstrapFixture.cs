using FrameFlow.Decoding;
using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
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
    private static HardwareDecodeCapabilities _cachedCapabilities =
        HardwareDecodeCapabilities.Empty;

    /// <summary>
    /// <see langword="true"/> when FFmpeg was successfully bootstrapped.
    /// Integration tests should skip when this is <see langword="false"/>.
    /// </summary>
    public bool IsBootstrapped { get; }

    /// <summary>
    /// What the hardware-decode probe found during that bootstrap, or
    /// <see cref="HardwareDecodeCapabilities.Empty"/> when it did not run.
    /// </summary>
    /// <remarks>
    /// The probe already runs inside <c>Initialize()</c>; this only keeps its answer
    /// rather than discarding it. Reading it a second time is not an option:
    /// <c>av_hwdevice_ctx_create</c> is not thread-safe, which is what the gate above
    /// exists for.
    /// </remarks>
    public HardwareDecodeCapabilities Capabilities => ReadCapabilities();

    public FfmpegBootstrapFixture()
    {
        lock (_gate)
        {
            _cachedIsBootstrapped ??= TryBootstrap();
            IsBootstrapped = _cachedIsBootstrapped.Value;
        }
    }

    /// <summary>
    /// Bootstraps if nothing has yet, and returns what the probe found. Shared by the
    /// fixture and by <see cref="RequiresHardwareDecodeFactAttribute"/>, so the two
    /// cannot race the probe against each other.
    /// </summary>
    internal static HardwareDecodeCapabilities ReadCapabilities()
    {
        lock (_gate)
        {
            _cachedIsBootstrapped ??= TryBootstrap();
            return _cachedCapabilities;
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
            var result = bootstrapper.Initialize();
            if (result.IsSuccess)
                _cachedCapabilities = result.Capabilities;
            return result.IsSuccess;
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
            OperatingSystem.IsWindows() ? "avutil-61.dll"
            : OperatingSystem.IsMacOS() ? "libavutil.61.dylib"
            : "libavutil.so.61";

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
/// Skips unless a named codec can actually use hardware decode on this machine.
/// </summary>
/// <remarks>
/// <para>
/// For assertions about <i>which</i> decoder was selected, which are only meaningful where
/// hardware is available for the codec under test. A GPU-less runner skips; so does a
/// machine whose device opens but whose decoder for that codec cannot bind it. A skip and
/// not an early return, per the convention on
/// <see cref="RequiresCorpusFileFactAttribute"/>: an early return records a pass and hides
/// the missing coverage from the skip count.
/// </para>
/// <para>
/// <b>Per codec, not per device.</b> Gating on
/// <see cref="HardwareDecodeBackend.Initialized"/> alone would be coarser than the
/// assertion it guards: a device opening says nothing about whether a given codec can use
/// it. On one machine H.264, HEVC and VP9 bind D3D11VA while AV1 falls back to software
/// against the same initialised device, so the device-level question would let an AV1
/// assertion run where it cannot be answered.
/// </para>
/// <para>
/// <c>VideoDecoder.HasHardwareCandidate</c> asks the same question the decode path asks,
/// so the gate and the assertion cannot disagree about what "available" means. It reports
/// what the codec advertises rather than what the driver will manage for a particular
/// stream, which is the stronger claim <see cref="TryBindSingle"/> settles and this gate
/// deliberately does not.
/// </para>
/// </remarks>
internal sealed class RequiresHardwareDecodeFactAttribute : FactAttribute
{
    /// <param name="codecId">
    /// The FFmpeg <c>AVCodecID</c> the gated test decodes. <c>27</c> is
    /// <c>AV_CODEC_ID_H264</c>.
    /// </param>
    /// <param name="fixedPool">
    /// Also skip where every initialised backend's pool grows (VideoToolbox), which has no budget
    /// to test.
    /// </param>
    public RequiresHardwareDecodeFactAttribute(int codecId, bool fixedPool = false)
    {
        if (!IntegrationTestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!IntegrationTestEnvironment.HasCorpusFiles)
        {
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
            return;
        }

        var capabilities = FfmpegBootstrapFixture.ReadCapabilities();

        if (!capabilities.Available.Any(b => b.Initialized))
        {
            Skip = "No hardware decode backend initialised on this machine.";
            return;
        }

        if (!VideoDecoder.HasHardwareCandidate(codecId, capabilities))
        {
            Skip =
                $"A hardware backend initialised, but no decoder for codec {codecId} "
                + "advertises a hardware config for it on this machine.";
            return;
        }

        if (
            fixedPool
            && capabilities
                .Available.Where(b => b.Initialized)
                .All(b => DecodePoolGuard.SpareSurfaces(b.Kind) is null)
        )
        {
            Skip = "Every hardware backend here has a growable pool, which has no budget.";
        }
    }
}

/// <summary>
/// Skips when one specific corpus file is absent, rather than only when the corpus as a whole
/// is. For fixtures that the generator can legitimately decline to produce.
/// </summary>
/// <remarks>
/// <see cref="RequiresFfmpegAndCorpusFactAttribute"/> skips on an empty corpus and nothing
/// finer, so a test for a fixture that one runtime cannot encode fails there instead of
/// skipping. The generator reports such a fixture as <c>UNAVL</c> and still records its
/// expectation, so the expectation being present says nothing about the file. Same shape as the
/// attribute of this name in <c>FrameFlow.Decoding.Tests</c>.
/// </remarks>
internal sealed class RequiresCorpusFileFactAttribute : FactAttribute
{
    public RequiresCorpusFileFactAttribute(string fileName, string howToGenerate)
    {
        if (!IntegrationTestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (IntegrationTestEnvironment.GetCorpusFile(fileName) is null)
            Skip = $"Corpus file '{fileName}' not present. {howToGenerate}";
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

/// <summary>
/// Opt-in gate for tests that play through a real audio device with OpenAL Soft. Skips
/// unless <c>FRAMEFLOW_AUDIO_DEVICE_TESTS</c> is <c>1</c> or <c>true</c>.
/// </summary>
/// <remarks>
/// The same gate as <c>RequiresAudioDeviceFact</c> in <c>FrameFlow.Audio.Tests</c>, with the
/// same variable. Headless runners can load OpenAL Soft but have no device that plays in real
/// time, so these tests do not run in CI. The two suites keep their own copies, as they do
/// for the FFmpeg bootstrap.
/// </remarks>
internal sealed class RequiresAudioDeviceFactAttribute : FactAttribute
{
    public RequiresAudioDeviceFactAttribute()
    {
        var envValue = Environment.GetEnvironmentVariable("FRAMEFLOW_AUDIO_DEVICE_TESTS");
        if (!IsTruthy(envValue))
            Skip = "Audio device tests disabled. Set FRAMEFLOW_AUDIO_DEVICE_TESTS=1 to enable.";
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (
            value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        );
}
