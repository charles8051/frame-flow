using FrameFlow.Inference.Cuda;
using Xunit;

namespace FrameFlow.Inference.Cuda.Tests;

/// <summary>
/// Unit tests for the pure CUDA Toolkit / cuDNN path-resolution decision
/// (<see cref="CudaPathResolution"/>). Every case drives a synthetic file
/// table (a <see cref="Func{String, Boolean}"/> over a set of "present"
/// absolute paths) instead of the real filesystem, so the bootstrap
/// verdict that consumers hit on a CUDA target is reproduced
/// deterministically in CI with no CUDA / cuDNN install and no GPU.
/// </summary>
public sealed class CudaPathResolutionTests
{
    // Canonical Windows-shaped roots used across the cases. These are just
    // strings to the pure decision — no path on disk is touched.
    private const string AppLocalNative =
        @"C:\app\bin\Debug\net10.0\runtimes\win-x64\native";
    private const string CudaPathBin = @"C:\CUDA_PATH\bin";
    private const string ToolkitRoot =
        @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";

    private const string ToolkitSentinel = "cudart64_12.dll";
    private const string CudnnSentinel = "cudnn64_9.dll";

    /// <summary>Builds an <c>isPresent</c> probe over a fixed, case-sensitive set of paths.</summary>
    private static Func<string, bool> FileTable(params string[] presentPaths)
    {
        var set = new HashSet<string>(presentPaths, StringComparer.Ordinal);
        return set.Contains;
    }

    // ── CUDA Toolkit: found-on-first-root (app-local) ────────────────────

    [Fact]
    public void ToolkitBin_FoundOnFirstRoot_AppLocal_WinsOverEverything()
    {
        // App-local has the sentinel AND so does CUDA_PATH and a scanned
        // v12 dir; first-priority app-local must win.
        var v12Bin = Path.Combine(ToolkitRoot, "v12.6", "bin");
        var table = FileTable(
            Path.Combine(AppLocalNative, ToolkitSentinel),
            Path.Combine(CudaPathBin, ToolkitSentinel),
            Path.Combine(v12Bin, ToolkitSentinel)
        );

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: AppLocalNative,
            cudaPathBin: CudaPathBin,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: new[] { Path.Combine(ToolkitRoot, "v12.6") },
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundAppLocal, verdict.Outcome);
        Assert.Equal(AppLocalNative, verdict.BinDir);
        Assert.Null(verdict.WrongVersionPath);
    }

    // ── CUDA Toolkit: found on a later root (CUDA_PATH, then scan) ────────

    [Fact]
    public void ToolkitBin_FoundOnLaterRoot_CudaPath_WhenAppLocalAbsent()
    {
        // App-local dir is present but lacks the sentinel; CUDA_PATH has it.
        var table = FileTable(Path.Combine(CudaPathBin, ToolkitSentinel));

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: AppLocalNative,
            cudaPathBin: CudaPathBin,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: Array.Empty<string>(),
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundEnvironment, verdict.Outcome);
        Assert.Equal(CudaPathBin, verdict.BinDir);
    }

    [Fact]
    public void ToolkitBin_FoundOnLaterRoot_Scan_PicksNewestV12()
    {
        // No app-local, no CUDA_PATH; two v12 installs present. The shell
        // passes them newest-first; the decision picks the first whose bin
        // holds the sentinel. Give the sentinel only to the newest to prove
        // the ordering is honoured.
        var newest = Path.Combine(ToolkitRoot, "v12.8");
        var older = Path.Combine(ToolkitRoot, "v12.3");
        var table = FileTable(Path.Combine(newest, "bin", ToolkitSentinel));

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: null,
            cudaPathBin: null,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: new[] { newest, older }, // newest-first
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundByScan, verdict.Outcome);
        Assert.Equal(Path.Combine(newest, "bin"), verdict.BinDir);
    }

    [Fact]
    public void ToolkitBin_Scan_SkipsV12WhoseBinLacksSentinel()
    {
        // A v12 dir exists but its bin has no cudart — the scan must report
        // NotFound (matching the live resolver's "found dir, missing DLL"
        // fall-through), not return the sentinel-less bin.
        var v12 = Path.Combine(ToolkitRoot, "v12.4");
        var table = FileTable(/* nothing present */);

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: null,
            cudaPathBin: null,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: new[] { v12 },
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
    }

    // ── CUDA Toolkit: missing everywhere (actionable "not installed") ─────

    [Fact]
    public void ToolkitBin_MissingEverywhere_NoInstallRoot_IsNotFound()
    {
        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: AppLocalNative,
            cudaPathBin: CudaPathBin,
            installRootExists: false,
            installRoot: ToolkitRoot,
            versionDirsDescending: Array.Empty<string>(),
            isPresent: FileTable(/* empty table */)
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
        Assert.Null(verdict.WrongVersionPath);
    }

    [Fact]
    public void ToolkitBin_MissingEverywhere_EmptyInstallRoot_IsNotFound()
    {
        // Install root exists but holds no version dirs at all.
        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: null,
            cudaPathBin: null,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: Array.Empty<string>(),
            isPresent: FileTable()
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
    }

    // ── CUDA Toolkit: wrong-version diagnostic ───────────────────────────

    [Fact]
    public void ToolkitBin_OnlyNonV12Installed_ReportsWrongVersionWithPath()
    {
        // CUDA 13 present, no v12. The decision must surface WrongVersion
        // (distinct from NotFound) and name the offending install so the
        // consumer-facing instruction can say "uninstall the wrong one".
        var v13 = Path.Combine(ToolkitRoot, "v13.0");
        var v11 = Path.Combine(ToolkitRoot, "v11.8");

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: null,
            cudaPathBin: null,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: new[] { v13, v11 }, // newest-first
            isPresent: FileTable()
        );

        Assert.Equal(CudaPathOutcome.WrongVersion, verdict.Outcome);
        Assert.Null(verdict.BinDir);
        Assert.Equal(v13, verdict.WrongVersionPath); // newest non-v12 named
    }

    // ── CUDA Toolkit: RID-specific path shape ────────────────────────────

    [Fact]
    public void ToolkitBin_AppLocal_PreservesWinX64RidPathShape()
    {
        // The decision probes <appLocalNativeDir>/cudart64_12.dll and
        // returns the native dir verbatim — confirming the win-x64 RID
        // path shape the shell hands in is the path consumers receive.
        const string ridNative =
            @"D:\deploy\runtimes\win-x64\native";
        var table = FileTable(Path.Combine(ridNative, ToolkitSentinel));

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: ridNative,
            cudaPathBin: null,
            installRootExists: false,
            installRoot: ToolkitRoot,
            versionDirsDescending: Array.Empty<string>(),
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundAppLocal, verdict.Outcome);
        Assert.Equal(ridNative, verdict.BinDir);
        // A Windows-shaped literal, not Path.Combine. The input to this probe is
        // the verbatim string above and the resolver returns it unchanged, so
        // Path.Combine would build "runtimes/win-x64/native" on a non-Windows
        // host and fail against a value that is correct.
        Assert.EndsWith(@"runtimes\win-x64\native", verdict.BinDir!);
    }

    [Fact]
    public void ToolkitBin_NoAppLocalProbe_WhenNativeDirNull()
    {
        // A non-win-x64 host hands null for the native dir; the decision
        // must not probe it and falls through to the next root.
        var table = FileTable(Path.Combine(CudaPathBin, ToolkitSentinel));

        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir: null,
            cudaPathBin: CudaPathBin,
            installRootExists: true,
            installRoot: ToolkitRoot,
            versionDirsDescending: Array.Empty<string>(),
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundEnvironment, verdict.Outcome);
        Assert.Equal(CudaPathBin, verdict.BinDir);
    }

    // ── cuDNN: found-on-first-root (app-local) ───────────────────────────

    [Fact]
    public void CudnnBin_FoundOnFirstRoot_AppLocal_WinsOverScan()
    {
        var x64 =
            @"C:\Program Files\NVIDIA\CUDNN\v9.6\bin\12.6\x64";
        var table = FileTable(
            Path.Combine(AppLocalNative, CudnnSentinel),
            Path.Combine(x64, CudnnSentinel)
        );

        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: AppLocalNative,
            cudnnRootExists: true,
            newestCudnnBinDirExists: true,
            cudaMajorSubdirsDescending: new[] { x64 },
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundAppLocal, verdict.Outcome);
        Assert.Equal(AppLocalNative, verdict.BinDir);
    }

    // ── cuDNN: found on a later root (scan) ──────────────────────────────

    [Fact]
    public void CudnnBin_FoundByScan_PicksFirstCandidateWithSentinel()
    {
        // Contract of the pure decision in isolation: given ordered x64
        // candidates, return the first whose x64 holds the sentinel,
        // skipping a leading sentinel-less candidate. (The shell preserves
        // the live resolver's narrower behavior by handing this at most one
        // candidate — see CudaDllResolver.TryFindCudnnBin — but the pure
        // function's own contract is "first-with-sentinel descending", which
        // this exercises directly.)
        const string cudnnBin = @"C:\Program Files\NVIDIA\CUDNN\v9.6\bin";
        var higherX64 = Path.Combine(cudnnBin, "12.9", "x64");
        var lowerX64 = Path.Combine(cudnnBin, "12.4", "x64");
        var table = FileTable(Path.Combine(lowerX64, CudnnSentinel));

        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: null,
            cudnnRootExists: true,
            newestCudnnBinDirExists: true,
            cudaMajorSubdirsDescending: new[] { higherX64, lowerX64 }, // highest-first
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundByScan, verdict.Outcome);
        Assert.Equal(lowerX64, verdict.BinDir);
    }

    [Fact]
    public void CudnnBin_SingleHighestCandidate_LacksSentinel_IsNotFound()
    {
        // Mirrors how the shell actually calls the decision: it filters to
        // the single highest x64-existing subdir. If that one lacks the
        // sentinel, the verdict is NotFound — the live resolver does NOT
        // fall back to a lower version.
        var highestX64 = @"C:\Program Files\NVIDIA\CUDNN\v9.6\bin\12.9\x64";

        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: null,
            cudnnRootExists: true,
            newestCudnnBinDirExists: true,
            cudaMajorSubdirsDescending: new[] { highestX64 }, // shell hands exactly one
            isPresent: FileTable() // no sentinel anywhere
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
    }

    [Fact]
    public void CudnnBin_FoundByScan_RidSpecificX64PathShape()
    {
        const string x64 =
            @"C:\Program Files\NVIDIA\CUDNN\v9.22\bin\12.9\x64";
        var table = FileTable(Path.Combine(x64, CudnnSentinel));

        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: null,
            cudnnRootExists: true,
            newestCudnnBinDirExists: true,
            cudaMajorSubdirsDescending: new[] { x64 },
            isPresent: table
        );

        Assert.Equal(CudaPathOutcome.FoundByScan, verdict.Outcome);
        Assert.EndsWith("x64", verdict.BinDir!);
        Assert.Equal(x64, verdict.BinDir);
    }

    // ── cuDNN: missing everywhere ────────────────────────────────────────

    [Fact]
    public void CudnnBin_NoRoot_IsNotFound()
    {
        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: AppLocalNative,
            cudnnRootExists: false,
            newestCudnnBinDirExists: false,
            cudaMajorSubdirsDescending: Array.Empty<string>(),
            isPresent: FileTable() // app-local dir present but sentinel absent
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
    }

    [Fact]
    public void CudnnBin_RootButNoV9BinDir_IsNotFound()
    {
        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: null,
            cudnnRootExists: true,
            newestCudnnBinDirExists: false, // no v9.* install / no bin dir
            cudaMajorSubdirsDescending: Array.Empty<string>(),
            isPresent: FileTable()
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
    }

    [Fact]
    public void CudnnBin_SubdirsPresentButNoSentinel_IsNotFound()
    {
        var x64 = @"C:\Program Files\NVIDIA\CUDNN\v9.6\bin\12.6\x64";
        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir: null,
            cudnnRootExists: true,
            newestCudnnBinDirExists: true,
            cudaMajorSubdirsDescending: new[] { x64 },
            isPresent: FileTable() // x64 dir exists (shell filtered) but no DLL
        );

        Assert.Equal(CudaPathOutcome.NotFound, verdict.Outcome);
        Assert.Null(verdict.BinDir);
    }

    // ── ParseCudaMajor: pure version parsing ─────────────────────────────

    [Theory]
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin", 12)]
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8\bin", 11)]
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0\bin", 13)]
    public void ParseCudaMajor_ExtractsMajorFromCanonicalBinPath(string binPath, int expected)
    {
        Assert.Equal(expected, CudaPathResolution.ParseCudaMajor(binPath));
    }

    [Theory]
    // Not a bin directory: the version segment is present but the path does not
    // have the canonical shape, so the major must not be inferred from it.
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8\lib")]
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8\not-bin")]
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8")]
    public void ParseCudaMajor_NonBinTrailingSegment_ReturnsFallback(string binPath)
    {
        Assert.Equal(12, CudaPathResolution.ParseCudaMajor(binPath));
    }

    [Theory]
    // Casing of the trailing segment is not significant on Windows.
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8\Bin", 11)]
    [InlineData(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0\BIN", 13)]
    public void ParseCudaMajor_BinSegmentCasingIgnored(string binPath, int expected)
    {
        Assert.Equal(expected, CudaPathResolution.ParseCudaMajor(binPath));
    }

    [Fact]
    public void ParseCudaMajor_NullPath_ReturnsFallback()
    {
        Assert.Equal(12, CudaPathResolution.ParseCudaMajor(null));
        Assert.Equal(99, CudaPathResolution.ParseCudaMajor(null, fallbackMajor: 99));
    }

    [Fact]
    public void ParseCudaMajor_NonCanonicalPath_ReturnsFallback()
    {
        // App-local native dir (no vNN.M segment) → fall back to 12, which
        // is what the live resolver assumes when the toolkit bin is unknown.
        Assert.Equal(12, CudaPathResolution.ParseCudaMajor(AppLocalNative));
    }
}
