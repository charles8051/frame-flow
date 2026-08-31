// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

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
/// cuDNN from winget; ORT-EP from re-installing earlier layers in the
/// right combination).
/// </para>
/// <para>
/// ADR-0011 §"Stage 1 details" defines this surface; Stages 2 and 3
/// (bundled runtime, cuDNN downloader) are deferred.
/// </para>
/// <para>
/// The bootstrapper is safe to call repeatedly. It does no caching of
/// its own — each <see cref="Probe"/> re-queries the underlying probes
/// (<see cref="CudaProbe"/>, <see cref="OnnxProbe"/>) and the
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
    /// instruction provider for the current platform (Windows today;
    /// other platforms return <see langword="null"/> until their ports
    /// land).
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
    /// As a side effect, this invokes
    /// <see cref="CudaDllResolver.EnsureLoadable"/> on Windows, which
    /// prepends discovered CUDA Toolkit and cuDNN <c>bin</c>
    /// directories to the process PATH. Subsequent ORT EP loads in the
    /// same process will see those directories. The resolver is
    /// idempotent.
    /// </remarks>
    /// <returns>
    /// A <see cref="CudaBootstrapResult"/> describing what was found
    /// and, when applicable, what's missing and how to fix it.
    /// </returns>
    public CudaBootstrapResult Probe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new CudaBootstrapResult(
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
        }

        // Discover + activate. EnsureLoadable is idempotent — even if
        // a previous probe in this process already ran, the underlying
        // probes (CudaProbe, OnnxProbe) cached their results from that
        // first call, and re-running discovery here is cheap.
        CudaDllResolver.EnsureLoadable(_logger);
        var cudaToolkitBin = CudaDllResolver.TryFindCudaToolkitBin(_logger);
        var cudnnBin = CudaDllResolver.TryFindCudnnBin(cudaToolkitBin, _logger);

        var missing = MissingNativeComponent.None;

        // Driver-level probe deferred. The pre-fork architecture used
        // Crossbar.Cuda.CudaProbe (ManagedCuda-backed) for granular
        // "is the NVIDIA driver loadable" detection. After the
        // FrameFlow fork (ADR-0049), FrameFlow.Inference.Cuda does
        // not depend on Crossbar.Cuda — driver detection collapses
        // into the ORT-EP loadability check below. A future ADR can
        // re-add granular driver probing via P/Invoke to nvcuda.dll
        // if the diagnostic gap matters in practice.
        Exception? driverFailure = null;
        const int deviceCount = 0;
        // MissingNativeComponent.GpuDriver flag intentionally not set
        // — see comment above. ORT-EP failure surfaces driver issues
        // less specifically but still as actionable diagnostics.

        if (cudaToolkitBin is null)
            missing |= MissingNativeComponent.CudaToolkit;
        if (cudnnBin is null)
            missing |= MissingNativeComponent.CuDnn;

        var ortAvailable = OnnxProbe.CudaExecutionProviderAvailable;
        var ortFailure = OnnxProbe.Failure;
        var ortVersion = OnnxProbe.OrtVersion;
        if (!ortAvailable)
            missing |= MissingNativeComponent.OrtCudaProvider;

        var instructions = BuildInstructions(missing);

        var status =
            missing == MissingNativeComponent.None
                ? CudaBootstrapStatus.Ready
                : CudaBootstrapStatus.MissingComponents;

        return new CudaBootstrapResult(
            Status: status,
            Missing: missing,
            Instructions: instructions,
            DetectedCudaToolkitBin: cudaToolkitBin,
            DetectedCudnnBin: cudnnBin,
            OrtVersion: ortVersion,
            DetectedDeviceCount: deviceCount,
            DriverFailure: driverFailure,
            OrtFailure: ortFailure
        );
    }

    private IReadOnlyList<CudaBootstrapInstruction> BuildInstructions(
        MissingNativeComponent missing
    )
    {
        if (missing == MissingNativeComponent.None || _instructions is null)
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
                list.Add(_instructions.GetInstruction(component));
        }
        return list;
    }
}
