// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// Why a CUDA Toolkit / cuDNN <c>bin</c> directory probe concluded what
/// it did. Carried on <see cref="CudaPathVerdict"/> so the diagnostic
/// is legible (and unit-testable) without re-deriving it from the
/// chosen path.
/// </summary>
internal enum CudaPathOutcome
{
    /// <summary>The sentinel DLL was found in the app-local
    /// <c>runtimes/{rid}/native/</c> directory (first-priority probe).</summary>
    FoundAppLocal,

    /// <summary>The sentinel DLL was found via the <c>%CUDA_PATH%</c>
    /// environment hint (CUDA Toolkit only).</summary>
    FoundEnvironment,

    /// <summary>The sentinel DLL was found by scanning the canonical
    /// install root for the newest matching version.</summary>
    FoundByScan,

    /// <summary>No probe located the sentinel DLL — the actionable
    /// "not installed" verdict.</summary>
    NotFound,

    /// <summary>A toolkit of the wrong major version is installed (e.g.
    /// CUDA 11 / 13 when ORT requires 12). CUDA Toolkit only; distinct
    /// from <see cref="NotFound"/> so the consumer-facing instruction can
    /// say "uninstall the wrong one" rather than "install one".</summary>
    WrongVersion,
}

/// <summary>
/// The verdict of a pure path-resolution decision: the chosen
/// <c>bin</c> directory (or <see langword="null"/>), why, and — for the
/// wrong-version case — the offending install path the diagnostic
/// should name.
/// </summary>
/// <param name="BinDir">
/// The resolved absolute <c>bin</c> directory, or <see langword="null"/>
/// when <see cref="Outcome"/> is <see cref="CudaPathOutcome.NotFound"/>
/// or <see cref="CudaPathOutcome.WrongVersion"/>.
/// </param>
/// <param name="Outcome">Why the decision concluded what it did.</param>
/// <param name="WrongVersionPath">
/// For <see cref="CudaPathOutcome.WrongVersion"/>, the path of the
/// non-matching toolkit install the warning should name; otherwise
/// <see langword="null"/>.
/// </param>
internal readonly record struct CudaPathVerdict(
    string? BinDir,
    CudaPathOutcome Outcome,
    string? WrongVersionPath = null
)
{
    /// <summary>A <see cref="CudaPathOutcome.NotFound"/> verdict.</summary>
    public static CudaPathVerdict NotFound { get; } = new(null, CudaPathOutcome.NotFound);
}

/// <summary>
/// Pure path-resolution decision for the CUDA Toolkit and cuDNN
/// <c>bin</c> directories. Holds the "which candidate root, in what
/// order, gated on which sentinel DLL" logic that <see cref="CudaDllResolver"/>
/// previously fused with live <see cref="Directory.Exists"/> /
/// <see cref="File.Exists"/> calls — splitting it out makes the
/// bootstrap verdict deterministically reproducible over a synthetic
/// file table (a fake <c>isPresent</c>), with no real CUDA / cuDNN
/// install required.
/// </summary>
/// <remarks>
/// <para>
/// <b>Functional core / imperative shell.</b> This type is the core:
/// total functions over immutable inputs, no IO. The shell
/// (<see cref="CudaDllResolver"/>) owns the messy edges — reading
/// <c>%CUDA_PATH%</c>, resolving <see cref="Environment.SpecialFolder.ProgramFiles"/>,
/// enumerating the install-root version directories, and probing the
/// real filesystem — then hands the gathered facts here. The directory
/// <em>enumeration</em> is live IO and stays in the shell; the
/// <em>selection</em> of which enumerated directory wins, and the
/// sentinel-presence gate on it, is the pure decision.
/// </para>
/// <para>
/// <b>The <c>isPresent</c> probe</b> answers "does this absolute path
/// exist as a file?" — in production it is <see cref="File.Exists"/>;
/// in tests it is a lookup against a synthetic set of present paths.
/// The decision never calls the filesystem directly, so the same logic
/// the consumer hits on a target runs verbatim in CI.
/// </para>
/// </remarks>
internal static class CudaPathResolution
{
    /// <summary>Sentinel DLL that marks a CUDA Toolkit 12.x <c>bin</c> directory.</summary>
    internal const string CudaToolkitSentinel = "cudart64_12.dll";

    /// <summary>Sentinel DLL that marks a cuDNN 9.x <c>x64</c> directory.</summary>
    internal const string CudnnSentinel = "cudnn64_9.dll";

    /// <summary>
    /// Decides the CUDA Toolkit <c>bin</c> directory from already-gathered
    /// candidate roots, applying the same priority order as the live
    /// resolver: app-local <c>runtimes/{rid}/native/</c> first, then the
    /// <c>%CUDA_PATH%</c> hint, then the newest <c>v12.*</c> directory
    /// under the canonical install root.
    /// </summary>
    /// <param name="appLocalNativeDir">
    /// The app-local <c>runtimes/{rid}/native/</c> directory to probe for
    /// the toolkit sentinel, or <see langword="null"/> when the host RID
    /// has no such path (today only <c>win-x64</c> is probed).
    /// </param>
    /// <param name="cudaPathBin">
    /// The <c>{%CUDA_PATH%}/bin</c> directory derived from the environment
    /// variable, or <see langword="null"/> when <c>%CUDA_PATH%</c> is unset
    /// / empty.
    /// </param>
    /// <param name="installRootExists">
    /// Whether the canonical install root
    /// (<c>%ProgramFiles%\NVIDIA GPU Computing Toolkit\CUDA</c>) exists.
    /// When <see langword="false"/>, scanning is skipped and the verdict is
    /// <see cref="CudaPathOutcome.NotFound"/>.
    /// </param>
    /// <param name="installRoot">
    /// The canonical install-root path (named only in diagnostics).
    /// </param>
    /// <param name="versionDirsDescending">
    /// All <c>v*.*</c> version directories enumerated under the install
    /// root, ordered newest-first (descending by name) by the shell. The
    /// decision picks the newest <c>v12.*</c> from this list, and falls
    /// back to the newest non-v12 entry to surface the wrong-version case.
    /// </param>
    /// <param name="isPresent">
    /// Probe answering "does this absolute file path exist?" — the
    /// sentinel-presence gate. In production <see cref="File.Exists"/>;
    /// in tests a synthetic lookup.
    /// </param>
    /// <returns>The resolution verdict.</returns>
    internal static CudaPathVerdict ResolveCudaToolkitBin(
        string? appLocalNativeDir,
        string? cudaPathBin,
        bool installRootExists,
        string installRoot,
        IReadOnlyList<string> versionDirsDescending,
        Func<string, bool> isPresent
    )
    {
        ArgumentNullException.ThrowIfNull(versionDirsDescending);
        ArgumentNullException.ThrowIfNull(isPresent);

        // 1. App-local runtimes/{rid}/native/ — first priority.
        if (
            appLocalNativeDir is not null
            && isPresent(Path.Combine(appLocalNativeDir, CudaToolkitSentinel))
        )
        {
            return new CudaPathVerdict(appLocalNativeDir, CudaPathOutcome.FoundAppLocal);
        }

        // 2. %CUDA_PATH%\bin hint.
        if (cudaPathBin is not null && isPresent(Path.Combine(cudaPathBin, CudaToolkitSentinel)))
        {
            return new CudaPathVerdict(cudaPathBin, CudaPathOutcome.FoundEnvironment);
        }

        // 3. Scan the canonical install root for the newest v12.x install.
        if (!installRootExists)
            return CudaPathVerdict.NotFound;

        var newestV12 = FirstWithPrefix(versionDirsDescending, "v12.");
        if (newestV12 is null)
        {
            // No v12 install — but a different major (v11 / v13 / future)
            // may be present. ORT 1.26.0 builds against CUDA 12; surface
            // the wrong-version case distinctly so the diagnostic is more
            // helpful on a fresh machine.
            var newestOther = versionDirsDescending.Count > 0 ? versionDirsDescending[0] : null;
            return newestOther is not null
                ? new CudaPathVerdict(null, CudaPathOutcome.WrongVersion, newestOther)
                : CudaPathVerdict.NotFound;
        }

        var fallbackBin = Path.Combine(newestV12, "bin");
        if (isPresent(Path.Combine(fallbackBin, CudaToolkitSentinel)))
            return new CudaPathVerdict(fallbackBin, CudaPathOutcome.FoundByScan);

        return CudaPathVerdict.NotFound;
    }

    /// <summary>
    /// Decides the cuDNN <c>x64</c> directory from already-gathered
    /// candidate roots, applying the same priority order as the live
    /// resolver: app-local <c>runtimes/{rid}/native/</c> first, then the
    /// newest <c>v9.*</c> install's highest <c>{cudaMajor}.*</c> subdir
    /// containing the cuDNN sentinel.
    /// </summary>
    /// <param name="appLocalNativeDir">
    /// The app-local <c>runtimes/{rid}/native/</c> directory to probe for
    /// the cuDNN sentinel, or <see langword="null"/>.
    /// </param>
    /// <param name="cudnnRootExists">
    /// Whether the cuDNN install root
    /// (<c>%ProgramFiles%\NVIDIA\CUDNN</c>) exists.
    /// </param>
    /// <param name="newestCudnnBinDirExists">
    /// Whether the newest <c>v9.*</c> install's <c>bin</c> directory
    /// exists. <see langword="false"/> (including "no v9.* install at
    /// all") yields <see cref="CudaPathOutcome.NotFound"/>.
    /// </param>
    /// <param name="cudaMajorSubdirsDescending">
    /// The <c>{cudaMajor}.*</c> subdirectories under the cuDNN <c>bin</c>
    /// directory whose <c>x64</c> child exists, ordered highest-version
    /// first by the shell. Each entry is the absolute <c>x64</c> path. The
    /// decision picks the first whose <c>x64</c> contains the cuDNN
    /// sentinel.
    /// </param>
    /// <param name="isPresent">
    /// Probe answering "does this absolute file path exist?".
    /// </param>
    /// <returns>The resolution verdict.</returns>
    internal static CudaPathVerdict ResolveCudnnBin(
        string? appLocalNativeDir,
        bool cudnnRootExists,
        bool newestCudnnBinDirExists,
        IReadOnlyList<string> cudaMajorSubdirsDescending,
        Func<string, bool> isPresent
    )
    {
        ArgumentNullException.ThrowIfNull(cudaMajorSubdirsDescending);
        ArgumentNullException.ThrowIfNull(isPresent);

        // 1. App-local runtimes/{rid}/native/ — first priority.
        if (
            appLocalNativeDir is not null
            && isPresent(Path.Combine(appLocalNativeDir, CudnnSentinel))
        )
        {
            return new CudaPathVerdict(appLocalNativeDir, CudaPathOutcome.FoundAppLocal);
        }

        // 2. Scan the cuDNN install root.
        if (!cudnnRootExists || !newestCudnnBinDirExists)
            return CudaPathVerdict.NotFound;

        // Pick the highest cuda-X.Y subdir whose x64 holds the sentinel.
        foreach (var x64Dir in cudaMajorSubdirsDescending)
        {
            if (isPresent(Path.Combine(x64Dir, CudnnSentinel)))
                return new CudaPathVerdict(x64Dir, CudaPathOutcome.FoundByScan);
        }

        return CudaPathVerdict.NotFound;
    }

    /// <summary>
    /// Parses the CUDA major version (e.g. <c>12</c>) from a CUDA Toolkit
    /// <c>bin</c> path of the canonical shape <c>…\CUDA\v12.8\bin</c>.
    /// Returns <paramref name="fallbackMajor"/> when the path does not
    /// match that shape. Pure — the cuDNN subdir layout is
    /// forward-compatible within a major, so the parsed major selects the
    /// matching <c>{major}.*</c> subdirectory.
    /// </summary>
    internal static int ParseCudaMajor(string? cudaToolkitBin, int fallbackMajor = 12)
    {
        if (cudaToolkitBin is null)
            return fallbackMajor;

        // …\CUDA\v12.8\bin → grab "12" from "v12.8", the segment before "bin".
        //
        // Split explicitly rather than using Path.GetDirectoryName /
        // Path.GetFileName. Those are *host*-relative: on a non-Windows host a
        // backslash is an ordinary character, so the whole string reads as one
        // segment and this silently returned fallbackMajor — 12 for every
        // input, including v11.8 and v13.0.
        //
        // The input is always a Windows path: this resolver is Windows-only,
        // gated in CudaBootstrapper and CudaDllResolver, with cudart64_*.dll /
        // cudnn64_*.dll sentinels. Parsing it is therefore a pure string
        // operation over a known shape, not a filesystem query, and must not
        // depend on where the parse happens to run.
        var segments = cudaToolkitBin.Split(
            new[] { '\\', '/' },
            StringSplitOptions.RemoveEmptyEntries
        );
        // Require the trailing segment to be "bin". Without it a path like
        // C:\CUDA\v11.8\lib parses as major 11, and the single caller
        // (CudaDllResolver) uses the major to pick a cuDNN subdirectory — so a
        // wrong answer here selects a cuDNN built against the wrong CUDA major.
        // The three real sources are safe: %CUDA_PATH%\bin and the scanned
        // {root}\v{N.N}\bin both end in "bin", and the app-local
        // runtimes/{rid}/native path falls through to the fallback because
        // "win-x64" has no "v" prefix. %CUDA_PATH% is user-controlled
        // environment, though, which is reason enough not to rely on shape.
        //
        // Ordinal-ignore-case: this is a Windows path, where "Bin" and "bin"
        // are the same directory.
        if (
            segments.Length >= 2
            && segments[^1].Equals("bin", StringComparison.OrdinalIgnoreCase)
        )
        {
            var verSegment = segments[^2];
            if (verSegment.StartsWith("v", StringComparison.Ordinal))
            {
                var dotIdx = verSegment.IndexOf('.');
                if (dotIdx > 1 && int.TryParse(verSegment.AsSpan(1, dotIdx - 1), out var parsed))
                    return parsed;
            }
        }
        return fallbackMajor;
    }

    private static string? FirstWithPrefix(IReadOnlyList<string> dirsDescending, string prefix)
    {
        // dirsDescending is already newest-first; the directory name (not
        // the full path) carries the version, so match on the leaf.
        foreach (var dir in dirsDescending)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return dir;
        }
        return null;
    }
}
