// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// One-shot helper that makes ORT's CUDA execution provider loadable
/// on Windows by ensuring the directories containing <c>cudart</c>,
/// <c>cublas</c>, and <c>cuDNN</c> are on the process PATH at the time
/// <c>SessionOptions.AppendExecutionProvider_CUDA</c> is called.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The official cuDNN 9.x installer for
/// Windows places its DLLs under
/// <c>%ProgramFiles%\NVIDIA\CUDNN\v9.&lt;minor&gt;\bin\&lt;cuda-major&gt;.&lt;cuda-minor&gt;\x64\</c>
/// and — at least in 9.22 — does not add that directory to the system
/// PATH. The CUDA Toolkit installer DOES add <c>%CUDA_PATH%\bin</c> so
/// <c>cudart64_12.dll</c> and <c>cublas64_12.dll</c> resolve, but
/// <c>cudnn64_9.dll</c> doesn't, and ORT's CUDA provider load fails
/// with <c>Failed to load shared library</c>. Resolving this in-
/// process avoids forcing every consumer to manually edit system PATH.
/// </para>
/// <para>
/// <b>API shape.</b> The type exposes two orthogonal surfaces:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="EnsureLoadable"/> — the activation entry point.
///       Discovers the directories and PATH-prepends them. Idempotent.
///       The shape used by ORT-consuming code that just wants "make it
///       work."
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="TryFindCudaToolkitBin"/> /
///       <see cref="TryFindCudnnBin"/> — pure-query methods used by
///       <see cref="CudaBootstrapper"/> for diagnostics. They report
///       what would be discovered without mutating any environment.
///     </description>
///   </item>
/// </list>
/// <para>
/// Non-Windows platforms are a no-op for activation. Linux's
/// <c>ld.so</c> reads <c>LD_LIBRARY_PATH</c> only at process startup,
/// so mid-process PATH-style mutation does not work; the cross-
/// platform activation strategy described in ADR-0011 is
/// <c>NativeLibrary.Load</c> with absolute paths, which is a different
/// mechanism and lands when the Linux port begins.
/// </para>
/// </remarks>
public static partial class CudaDllResolver
{
    private static readonly object _gate = new();
    private static bool _hasRun;

    /// <summary>
    /// Ensures CUDA runtime + cuDNN are loadable from the current
    /// process. Safe to call repeatedly; only the first call does
    /// work. On non-Windows hosts this is a no-op.
    /// </summary>
    public static void EnsureLoadable(ILogger? logger = null)
    {
        lock (_gate)
        {
            if (_hasRun)
                return;
            _hasRun = true;
        }

        var log = logger ?? NullLogger.Instance;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            LogSkippedNonWindows(log);
            return;
        }

        // CUDA toolkit's bin is normally already on PATH (the toolkit
        // installer adds it), but probe and add defensively. cudart +
        // cublas live here.
        var cudaBin = TryFindCudaToolkitBin(log);
        if (cudaBin is not null)
            PrependToProcessPath(cudaBin, log);

        // cuDNN bin is the load-bearing one — the cuDNN 9.x installer
        // typically does NOT add this to PATH.
        var cudnnBin = TryFindCudnnBin(cudaBin, log);
        if (cudnnBin is not null)
            PrependToProcessPath(cudnnBin, log);
    }

    /// <summary>
    /// Discovers the CUDA Toolkit 12.x <c>bin</c> directory containing
    /// <c>cudart64_12.dll</c> without modifying the process
    /// environment. Used by <see cref="CudaBootstrapper"/> for
    /// diagnostics.
    /// </summary>
    /// <param name="logger">
    /// Optional logger for discovery events. Defaults to
    /// <see cref="NullLogger"/>.
    /// </param>
    /// <returns>
    /// The absolute path of the toolkit's <c>bin</c> directory, or
    /// <see langword="null"/> if the toolkit is not installed or this
    /// is not a Windows host.
    /// </returns>
    public static string? TryFindCudaToolkitBin(ILogger? logger = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var log = logger ?? NullLogger.Instance;

        // ── Shell: gather the candidate roots from the live environment ──
        //
        // First-priority probe: the app's own `runtimes/{rid}/native/`
        // directory. Two scenarios use this path:
        //
        //  (a) The dev-time fetch-cuda.cs script downloaded the
        //      redistributable Toolkit DLLs into the repo's
        //      `runtimes/win-x64/native/` folder, which the repo-root
        //      Directory.Build.targets copies into test/example
        //      output dirs at build time.
        //  (b) Future Stage-2 NuGet package (ADR-0011) will ship the
        //      same DLLs in the same per-RID layout, and they'll land
        //      in `AppContext.BaseDirectory/runtimes/{rid}/native/`
        //      via the standard NuGet runtimes resolution.
        //
        // Both produce the exact same on-disk layout, so a single
        // probe covers both stories.
        var appLocalNativeDir = AppLocalRuntimesNativeDir();

        // %CUDA_PATH%\bin hint — gathered only when the directory exists
        // (the live Directory.Exists guard preserved verbatim; the sentinel
        // File.Exists check is the decision's isPresent probe).
        string? cudaPathBin = null;
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath))
        {
            var bin = Path.Combine(cudaPath, "bin");
            if (Directory.Exists(bin))
                cudaPathBin = bin;
        }

        // Canonical install root + its newest-first v*.* version dirs.
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA GPU Computing Toolkit",
            "CUDA"
        );
        var installRootExists = Directory.Exists(root);
        var versionDirsDescending = installRootExists
            ? Directory.EnumerateDirectories(root, "v*.*").OrderByDescending(d => d).ToList()
            : [];

        // ── Core: the pure decision over the gathered facts ─────────────
        var verdict = CudaPathResolution.ResolveCudaToolkitBin(
            appLocalNativeDir,
            cudaPathBin,
            installRootExists,
            root,
            versionDirsDescending,
            File.Exists
        );

        // ── Shell: emit the verdict-matching diagnostics ────────────────
        switch (verdict.Outcome)
        {
            case CudaPathOutcome.FoundAppLocal:
                LogCudaBinFromAppLocal(log, verdict.BinDir!);
                break;
            case CudaPathOutcome.FoundEnvironment:
                LogCudaBinFromEnv(log, verdict.BinDir!);
                break;
            case CudaPathOutcome.FoundByScan:
                LogCudaBinFromFallback(log, verdict.BinDir!);
                break;
            case CudaPathOutcome.WrongVersion:
                LogCudaToolkitWrongVersion(log, verdict.WrongVersionPath!);
                break;
            default:
                LogCudaToolkitNotFound(log, root);
                break;
        }

        return verdict.BinDir;
    }

    /// <summary>
    /// Discovers the cuDNN 9.x <c>x64</c> directory containing
    /// <c>cudnn64_9.dll</c> without modifying the process environment.
    /// Used by <see cref="CudaBootstrapper"/> for diagnostics.
    /// </summary>
    /// <param name="cudaToolkitBin">
    /// Optional CUDA Toolkit <c>bin</c> path (from
    /// <see cref="TryFindCudaToolkitBin"/>). Used to pick the matching
    /// cuDNN subdirectory layout. Pass <see langword="null"/> to assume
    /// CUDA major 12.
    /// </param>
    /// <param name="logger">
    /// Optional logger for discovery events. Defaults to
    /// <see cref="NullLogger"/>.
    /// </param>
    /// <returns>
    /// The absolute path of the cuDNN <c>x64</c> directory, or
    /// <see langword="null"/> if cuDNN is not installed or this is not
    /// a Windows host.
    /// </returns>
    public static string? TryFindCudnnBin(string? cudaToolkitBin, ILogger? logger = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var log = logger ?? NullLogger.Instance;

        // ── Shell: gather the candidate roots from the live environment ──
        //
        // First-priority probe: app-local runtimes/{rid}/native/. The
        // dev-time fetch-cuda.cs script lands cudnn64_9.dll here
        // alongside cudart64_12.dll. Same path covers the future
        // Stage-2 NuGet package layout (ADR-0011). See the matching
        // probe in TryFindCudaToolkitBin for the full rationale.
        var appLocalNativeDir = AppLocalRuntimesNativeDir();

        // Determine the CUDA major so we can pick the matching cuDNN
        // subdirectory. cuDNN 9.22's layout is:
        //   C:\Program Files\NVIDIA\CUDNN\v9.<minor>\bin\<cuda-major>.<cuda-minor>\x64\
        // The cuda-version subdir is forward-compatible within a major:
        // cuDNN built against 12.9 works with CUDA 12.x toolkits.
        var cudaMajor = CudaPathResolution.ParseCudaMajor(cudaToolkitBin);

        var cudnnRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA",
            "CUDNN"
        );
        var cudnnRootExists = Directory.Exists(cudnnRoot);

        // Newest v9.x install, its bin dir, and the single highest-version
        // {cudaMajor}.* subdir whose x64 child exists. Enumeration +
        // x64-existence filtering is live IO; the sentinel-presence gate on
        // the selected x64 is the pure decision. NOTE the original probed
        // the sentinel on ONLY the highest x64-existing subdir (it did not
        // fall back to a lower version whose x64 also exists), so the shell
        // hands the decision at most that one candidate to preserve that
        // exact behavior.
        string? cudnnBinRoot = null;
        var newestCudnnBinDirExists = false;
        IReadOnlyList<string> cudaMajorX64DirsDescending = [];
        if (cudnnRootExists)
        {
            var newestCudnn = Directory
                .EnumerateDirectories(cudnnRoot, "v9.*")
                .OrderByDescending(d => d)
                .FirstOrDefault();
            if (newestCudnn is not null)
            {
                cudnnBinRoot = Path.Combine(newestCudnn, "bin");
                newestCudnnBinDirExists = Directory.Exists(cudnnBinRoot);
                if (newestCudnnBinDirExists)
                {
                    var highestX64 = Directory
                        .EnumerateDirectories(cudnnBinRoot, $"{cudaMajor}.*")
                        .Select(d => new { Path = d, Name = Path.GetFileName(d) })
                        .OrderByDescending(x => x.Name)
                        .Select(x => Path.Combine(x.Path, "x64"))
                        .FirstOrDefault(Directory.Exists);
                    cudaMajorX64DirsDescending =
                        highestX64 is null ? [] : [highestX64];
                }
            }
        }

        // ── Core: the pure decision over the gathered facts ─────────────
        var verdict = CudaPathResolution.ResolveCudnnBin(
            appLocalNativeDir,
            cudnnRootExists,
            newestCudnnBinDirExists,
            cudaMajorX64DirsDescending,
            File.Exists
        );

        // ── Shell: emit the verdict-matching diagnostics ────────────────
        // The not-found diagnostics name *different* roots depending on
        // how far the scan got (cuDNN root missing / v9 bin missing /
        // sentinel missing), so the staged path args are reconstructed
        // here from the gathered facts the decision consumed.
        switch (verdict.Outcome)
        {
            case CudaPathOutcome.FoundAppLocal:
                LogCudnnBinFromAppLocal(log, verdict.BinDir!);
                break;
            case CudaPathOutcome.FoundByScan:
                LogCudnnBinFound(log, verdict.BinDir!);
                break;
            default:
                // NotFound: distinguish which stage failed for the diagnostic.
                if (!cudnnRootExists)
                    LogCudnnRootNotFound(log, cudnnRoot);
                else if (cudnnBinRoot is null)
                    LogCudnnRootNotFound(log, cudnnRoot); // no v9.* install
                else if (!newestCudnnBinDirExists)
                    LogCudnnRootNotFound(log, cudnnBinRoot);
                else
                    LogCudnnDllNotFound(log, cudnnBinRoot, cudaMajor);
                break;
        }

        return verdict.BinDir;
    }

    /// <summary>
    /// Computes the app-local <c>AppContext.BaseDirectory/runtimes/{rid}/native/</c>
    /// directory to probe for the bootstrap sentinel DLLs, or
    /// <see langword="null"/> when <c>BaseDirectory</c> is empty or the
    /// directory does not exist. The RID is fixed to <c>win-x64</c>
    /// today — this is the only path the dev-time fetch script targets,
    /// and the only path the future Stage-2 NuGet package will produce on
    /// Windows. When Linux support arrives, this method grows a
    /// platform-keyed RID. The sentinel-presence check is the pure
    /// decision's <c>isPresent</c> probe (see
    /// <see cref="CudaPathResolution"/>); this shell helper only resolves
    /// the directory.
    /// </summary>
    private static string? AppLocalRuntimesNativeDir()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
            return null;
        var candidate = Path.Combine(baseDir, "runtimes", "win-x64", "native");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static void PrependToProcessPath(string directory, ILogger log)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = Path.PathSeparator;
        // Don't add twice.
        var parts = path.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Any(p =>
                string.Equals(
                    p.TrimEnd('\\'),
                    directory.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            LogPathAlreadyPresent(log, directory);
            return;
        }

        Environment.SetEnvironmentVariable("PATH", $"{directory}{sep}{path}");
        LogPathPrepended(log, directory);
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "CudaDllResolver: skipping (non-Windows host)"
    )]
    private static partial void LogSkippedNonWindows(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CudaDllResolver: CUDA toolkit bin from app-local runtimes/: {BinDir}"
    )]
    private static partial void LogCudaBinFromAppLocal(ILogger logger, string binDir);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CudaDllResolver: cuDNN bin from app-local runtimes/: {BinDir}"
    )]
    private static partial void LogCudnnBinFromAppLocal(ILogger logger, string binDir);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "CudaDllResolver: CUDA toolkit bin from %CUDA_PATH%: {BinDir}"
    )]
    private static partial void LogCudaBinFromEnv(ILogger logger, string binDir);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "CudaDllResolver: CUDA toolkit bin discovered by scan: {BinDir}"
    )]
    private static partial void LogCudaBinFromFallback(ILogger logger, string binDir);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CudaDllResolver: CUDA Toolkit 12.x not found under {Root}; ORT CUDA EP will fail unless the toolkit is on PATH"
    )]
    private static partial void LogCudaToolkitNotFound(ILogger logger, string root);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CudaDllResolver: a non-v12 CUDA Toolkit is installed at {WrongVersionPath}, but ORT 1.26.0 requires CUDA 12. Uninstall the non-v12 toolkit and install the latest 12.x release from https://developer.nvidia.com/cuda-toolkit-archive."
    )]
    private static partial void LogCudaToolkitWrongVersion(ILogger logger, string wrongVersionPath);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CudaDllResolver: cuDNN bin found and added to PATH: {BinDir}"
    )]
    private static partial void LogCudnnBinFound(ILogger logger, string binDir);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CudaDllResolver: cuDNN install root not found under {Root}; ORT CUDA EP will fail unless cuDNN is on PATH"
    )]
    private static partial void LogCudnnRootNotFound(ILogger logger, string root);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CudaDllResolver: cudnn64_9.dll not found under {Root} for CUDA major {CudaMajor}; ORT CUDA EP will fail. Install cuDNN 9.x for CUDA 12 and ensure the corresponding bin directory contains cudnn64_9.dll."
    )]
    private static partial void LogCudnnDllNotFound(ILogger logger, string root, int cudaMajor);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "CudaDllResolver: directory already on PATH: {Directory}"
    )]
    private static partial void LogPathAlreadyPresent(ILogger logger, string directory);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CudaDllResolver: prepended to process PATH: {Directory}"
    )]
    private static partial void LogPathPrepended(ILogger logger, string directory);
}
