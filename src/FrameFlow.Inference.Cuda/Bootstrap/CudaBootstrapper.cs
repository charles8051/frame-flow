// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// Diagnostic bootstrapper for the CUDA + ONNX Runtime native stack.
/// Probes each layer (driver, CUDA Toolkit, cuDNN, ORT CUDA EP),
/// identifies what's missing, and emits platform-shaped install
/// instructions so consumers can present a legible first-run
/// experience instead of a cryptic <c>LoadLibrary</c> failure deep
/// inside ORT.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on FrameFlow's <c>FrameFlowBootstrapper</c>: probe → result
/// record. The shape differs in two ways. First, the bootstrapper does
/// no <em>loading</em> itself — that's <see cref="CudaDllResolver"/>'s
/// job — it only diagnoses and reports. Second, the result is
/// structured per-layer with a flag enum, because each layer has its
/// own remediation path (driver from NVIDIA's installer; toolkit and
/// cuDNN from NVIDIA's installers on Windows or its apt repository on
/// Linux; ORT-EP from re-installing earlier layers in the right
/// combination).
/// </para>
/// <para>
/// ADR-0011 §"Stage 1 details" defines this surface; Stages 2 and 3
/// (bundled runtime, cuDNN downloader) are deferred.
/// </para>
/// <para>
/// The bootstrapper is safe to call repeatedly. It does no caching of
/// its own — each <see cref="Probe"/> re-queries the underlying probes
/// (<c>CudaProbe</c>, <see cref="OnnxProbe"/>) and the
/// <see cref="CudaDllResolver"/>. Those have their own internal
/// caching, so repeated calls are cheap.
/// </para>
/// </remarks>
public sealed class CudaBootstrapper
{
    private readonly ILogger _logger;
    private readonly ICudaInstallInstructionProvider? _instructions;

    /// <summary>
    /// Creates a bootstrapper using the auto-detected install
    /// instruction provider for the current platform (Windows or Linux;
    /// none elsewhere).
    /// </summary>
    /// <param name="logger">
    /// Optional logger for the underlying <see cref="CudaDllResolver"/>
    /// discovery events. Defaults to <see cref="NullLogger"/>.
    /// </param>
    /// <param name="installInstructionProvider">
    /// Optional override for the install-instruction source. When
    /// <see langword="null"/>, the bootstrapper auto-selects via
    /// <see cref="CudaInstallInstructionProviders.ForCurrentPlatform"/>.
    /// </param>
    public CudaBootstrapper(
        ILogger<CudaBootstrapper>? logger = null,
        ICudaInstallInstructionProvider? installInstructionProvider = null
    )
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _instructions =
            installInstructionProvider
            ?? CudaInstallInstructionProviders.ForCurrentPlatform();
    }

    /// <summary>
    /// Probes every layer of the CUDA + ORT stack and returns a
    /// structured diagnostic record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows, as a side effect, this invokes
    /// <see cref="CudaDllResolver.EnsureLoadable"/>, which prepends
    /// discovered CUDA Toolkit and cuDNN <c>bin</c> directories to the
    /// process PATH. Subsequent ORT EP loads in the same process will see
    /// those directories. The resolver is idempotent.
    /// </para>
    /// <para>
    /// On Linux x64 the probe is the load itself: <c>libcuda.so.1</c>,
    /// the CUDA 12 runtime libraries and cuDNN 9 are loaded into the
    /// process, from <c>runtimes/linux-x64/native</c> under the app's base
    /// directory when present there and by soname otherwise, so they stay
    /// loaded for ORT's CUDA provider. A missing driver sets
    /// <see cref="MissingNativeComponent.GpuDriver"/> and carries the
    /// loader's message in <see cref="CudaBootstrapResult.DriverFailure"/>.
    /// </para>
    /// <para>
    /// Other platforms return
    /// <see cref="CudaBootstrapStatus.UnsupportedPlatform"/>.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A <see cref="CudaBootstrapResult"/> describing what was found
    /// and, when applicable, what's missing and how to fix it.
    /// </returns>
    public CudaBootstrapResult Probe() =>
        CudaDllResolver.CurrentPlatform() switch
        {
            CudaBootstrapPlatform.Windows => ProbeWindows(),
            CudaBootstrapPlatform.LinuxX64 => ProbeLinux(),
            _ => UnsupportedPlatform,
        };

    private static CudaBootstrapResult UnsupportedPlatform { get; } =
        new(
            Status: CudaBootstrapStatus.UnsupportedPlatform,
            Missing: MissingNativeComponent.None,
            Instructions: Array.Empty<CudaBootstrapInstruction>(),
            DetectedCudaToolkitBin: null,
            DetectedCudnnBin: null,
            OrtVersion: null,
            DetectedDeviceCount: 0,
            DriverFailure: null,
            OrtFailure: null
        );

    private CudaBootstrapResult ProbeLinux()
    {
        // ── Shell: load what can be loaded (app-local first), then ask ORT ──
        var verdict = CudaDllResolver.ResolveLinux(_logger);

        // A library loaded by soname came from wherever the loader found
        // it; /proc/self/maps names that file.
        string? DirectoryOf(string soname) =>
            verdict.Find(soname) switch
            {
                { Source: CudaLibrarySource.AppLocal or CudaLibrarySource.AlreadyInProcess } load =>
                    Path.GetDirectoryName(load.LoadedFrom),
                { Source: CudaLibrarySource.LoaderSearchPath } => CudaDllResolver.LoadedLibraryDirectory(soname),
                _ => null,
            };

        // ── Core: the pure composition of the gathered facts ──
        return Compose(
            LinuxFacts(
                verdict,
                DirectoryOf(LinuxCudaResolution.CudaRuntimeSoname),
                DirectoryOf(LinuxCudaResolution.CudnnSoname),
                new OrtProbeFacts(
                    OnnxProbe.CudaExecutionProviderAvailable,
                    OnnxProbe.OrtVersion,
                    OnnxProbe.Failure
                )
            ),
            _instructions
        );
    }

    /// <summary>
    /// The facts a probe gathered, before they are composed into a
    /// <see cref="CudaBootstrapResult"/>.
    /// </summary>
    internal sealed record ProbeFacts(
        MissingNativeComponent MissingLibraries,
        string? CudaToolkitDir,
        string? CudnnDir,
        Exception? DriverFailure,
        OrtProbeFacts Ort
    );

    /// <summary>What <see cref="OnnxProbe"/> reported.</summary>
    internal readonly record struct OrtProbeFacts(bool CudaAvailable, string? Version, Exception? Failure);

    /// <summary>
    /// Turns a Linux library verdict into probe facts. A driver that did
    /// not load is reported with the loader's message, which says whether
    /// the file is absent or present but unusable.
    /// </summary>
    internal static ProbeFacts LinuxFacts(
        LinuxCudaVerdict verdict,
        string? cudaToolkitDir,
        string? cudnnDir,
        OrtProbeFacts ort
    )
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var driverError = verdict.FirstErrorFor(MissingNativeComponent.GpuDriver);
        return new ProbeFacts(
            verdict.Missing,
            (verdict.Missing & MissingNativeComponent.CudaToolkit) == 0 ? cudaToolkitDir : null,
            (verdict.Missing & MissingNativeComponent.CuDnn) == 0 ? cudnnDir : null,
            driverError is null ? null : new DllNotFoundException(driverError),
            ort
        );
    }

    /// <summary>
    /// Composes gathered probe facts into the result: adds the ORT
    /// provider flag, orders the instructions, and decides the status.
    /// Pure; the same composition serves every platform.
    /// </summary>
    internal static CudaBootstrapResult Compose(
        ProbeFacts facts,
        ICudaInstallInstructionProvider? instructions
    )
    {
        ArgumentNullException.ThrowIfNull(facts);

        var missing = facts.MissingLibraries;
        if (!facts.Ort.CudaAvailable)
            missing |= MissingNativeComponent.OrtCudaProvider;

        return new CudaBootstrapResult(
            Status: missing == MissingNativeComponent.None
                ? CudaBootstrapStatus.Ready
                : CudaBootstrapStatus.MissingComponents,
            Missing: missing,
            Instructions: BuildInstructions(missing, instructions),
            DetectedCudaToolkitBin: facts.CudaToolkitDir,
            DetectedCudnnBin: facts.CudnnDir,
            OrtVersion: facts.Ort.Version,
            DetectedDeviceCount: 0,
            DriverFailure: facts.DriverFailure,
            OrtFailure: facts.Ort.Failure
        );
    }

    private CudaBootstrapResult ProbeWindows()
    {
        // Discover + activate. EnsureLoadable is idempotent: only the first
        // call in a process prepends to PATH, and re-running discovery
        // here is cheap.
        CudaDllResolver.EnsureLoadable(_logger);
        var cudaToolkitBin = CudaDllResolver.TryFindCudaToolkitBin(_logger);
        var cudnnBin = CudaDllResolver.TryFindCudnnBin(cudaToolkitBin, _logger);

        var missing = MissingNativeComponent.None;

        // No driver-level probe on Windows. FrameFlow.Inference.Cuda does
        // not depend on a CUDA driver binding (ADR-0049), so driver
        // detection collapses into the ORT-EP loadability check, and the
        // GpuDriver flag is never set here. ORT-EP failure surfaces
        // driver issues less specifically but still as actionable
        // diagnostics. (Linux loads libcuda.so.1 and does set the flag.)
        if (cudaToolkitBin is null)
            missing |= MissingNativeComponent.CudaToolkit;
        if (cudnnBin is null)
            missing |= MissingNativeComponent.CuDnn;

        return Compose(
            new ProbeFacts(
                missing,
                cudaToolkitBin,
                cudnnBin,
                DriverFailure: null,
                new OrtProbeFacts(
                    OnnxProbe.CudaExecutionProviderAvailable,
                    OnnxProbe.OrtVersion,
                    OnnxProbe.Failure
                )
            ),
            _instructions
        );
    }

    private static IReadOnlyList<CudaBootstrapInstruction> BuildInstructions(
        MissingNativeComponent missing,
        ICudaInstallInstructionProvider? instructions
    )
    {
        if (missing == MissingNativeComponent.None || instructions is null)
            return Array.Empty<CudaBootstrapInstruction>();

        // Emit in dependency order: driver → toolkit → cuDNN → ORT-EP.
        // The ORT-EP failure is almost always a cascade from one of
        // the earlier layers; listing it last lets a reader fix the
        // root cause first.
        var order = new[]
        {
            MissingNativeComponent.GpuDriver,
            MissingNativeComponent.CudaToolkit,
            MissingNativeComponent.CuDnn,
            MissingNativeComponent.OrtCudaProvider,
        };

        var list = new List<CudaBootstrapInstruction>(order.Length);
        foreach (var component in order)
        {
            if ((missing & component) == component)
                list.Add(instructions.GetInstruction(component));
        }
        return list;
    }
}
