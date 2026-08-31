// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// Overall outcome of a <see cref="CudaBootstrapper.Probe"/> call.
/// </summary>
public enum CudaBootstrapStatus
{
    /// <summary>
    /// Every layer required for ORT CUDA-EP inference is loadable.
    /// </summary>
    Ready,

    /// <summary>
    /// One or more layers are missing or unloadable. See
    /// <see cref="CudaBootstrapResult.Missing"/> and
    /// <see cref="CudaBootstrapResult.Instructions"/> for detail and
    /// actionable remediation steps.
    /// </summary>
    MissingComponents,

    /// <summary>
    /// The current OS platform is not yet supported. The bootstrapper
    /// is Windows-first; Linux and macOS providers will land as those
    /// ports begin (see ADR-0011 §"Staging").
    /// </summary>
    UnsupportedPlatform,
}

/// <summary>
/// Flags identifying the layers of the CUDA + ONNX Runtime stack that
/// the bootstrapper could not verify. Multiple flags may be set; an
/// install-instruction provider produces one instruction per flag in
/// dependency order (driver → toolkit → cuDNN → ORT-EP).
/// </summary>
[Flags]
public enum MissingNativeComponent
{
    /// <summary>Everything probed successfully.</summary>
    None = 0,

    /// <summary>
    /// The NVIDIA GPU driver is not loaded — <c>nvcuda.dll</c> is
    /// missing, no CUDA-capable device is visible, or
    /// <see cref="Crossbar.Cuda.CudaProbe.IsAvailable"/> is otherwise
    /// false. Cannot be resolved by package installs; the user must
    /// install or update the GPU driver.
    /// </summary>
    GpuDriver = 1 << 0,

    /// <summary>
    /// CUDA Toolkit 12.x is not discoverable. Specifically,
    /// <c>cudart64_12.dll</c> was not found via <c>%CUDA_PATH%</c>
    /// or the canonical install root.
    /// </summary>
    CudaToolkit = 1 << 1,

    /// <summary>
    /// cuDNN 9.x is not discoverable. Specifically,
    /// <c>cudnn64_9.dll</c> was not found in any of the standard
    /// cuDNN install locations.
    /// </summary>
    CuDnn = 1 << 2,

    /// <summary>
    /// ONNX Runtime's CUDA execution provider failed to load. This is
    /// usually a cascade from a missing toolkit or cuDNN; if those are
    /// present and this flag is still set, it typically indicates an
    /// ORT / CUDA-major / cuDNN-major version mismatch.
    /// </summary>
    OrtCudaProvider = 1 << 3,
}

/// <summary>
/// Structured outcome of a <see cref="CudaBootstrapper.Probe"/> call.
/// Records both the high-level <see cref="Status"/> and granular
/// diagnostic detail (detected install paths, device count, ORT
/// version, failure exceptions) so consumers can render the result
/// however they like — CLI output, structured log, GUI dialog.
/// </summary>
/// <param name="Status">Overall outcome.</param>
/// <param name="Missing">
/// Flags identifying which layers could not be verified. Empty when
/// <see cref="Status"/> is <see cref="CudaBootstrapStatus.Ready"/>.
/// </param>
/// <param name="Instructions">
/// Ordered remediation steps, one per missing component. Empty when
/// no platform-specific instruction provider is available, or when
/// nothing is missing.
/// </param>
/// <param name="DetectedCudaToolkitBin">
/// Absolute path of the CUDA Toolkit <c>bin</c> directory that was
/// discovered and added to the process PATH, or <see langword="null"/>
/// if not found.
/// </param>
/// <param name="DetectedCudnnBin">
/// Absolute path of the cuDNN <c>x64</c> directory that was discovered
/// and added to the process PATH, or <see langword="null"/> if not
/// found.
/// </param>
/// <param name="OrtVersion">
/// ONNX Runtime version string reported by the library, or
/// <see langword="null"/> if ORT itself could not be loaded.
/// </param>
/// <param name="DetectedDeviceCount">
/// Number of CUDA-capable devices visible to the driver. Zero when the
/// driver is unavailable.
/// </param>
/// <param name="DriverFailure">
/// Exception raised by the CUDA driver probe, or
/// <see langword="null"/> if the driver loaded successfully.
/// </param>
/// <param name="OrtFailure">
/// Exception raised when appending the ORT CUDA execution provider, or
/// <see langword="null"/> if it succeeded.
/// </param>
public sealed record CudaBootstrapResult(
    CudaBootstrapStatus Status,
    MissingNativeComponent Missing,
    IReadOnlyList<CudaBootstrapInstruction> Instructions,
    string? DetectedCudaToolkitBin,
    string? DetectedCudnnBin,
    string? OrtVersion,
    int DetectedDeviceCount,
    Exception? DriverFailure,
    Exception? OrtFailure
)
{
    /// <summary>
    /// True when <see cref="Status"/> is
    /// <see cref="CudaBootstrapStatus.Ready"/> — the full stack is
    /// loadable.
    /// </summary>
    public bool IsReady => Status == CudaBootstrapStatus.Ready;
}

/// <summary>
/// One remediation step paired with a missing component. Produced by
/// an <see cref="ICudaInstallInstructionProvider"/> so the bootstrapper
/// itself stays platform-agnostic.
/// </summary>
/// <param name="Component">
/// The single <see cref="MissingNativeComponent"/> flag this
/// instruction addresses.
/// </param>
/// <param name="Description">
/// Human-readable description of what's missing. One sentence.
/// </param>
/// <param name="ActionableStep">
/// Concrete command or action the user can run. For Windows, typically
/// a <c>winget install ...</c> invocation; for driver issues, a link
/// to a download page.
/// </param>
/// <param name="DocumentationUri">
/// Optional canonical documentation URL for this component, suitable
/// for embedding in a help message or hyperlink.
/// </param>
public sealed record CudaBootstrapInstruction(
    MissingNativeComponent Component,
    string Description,
    string ActionableStep,
    Uri? DocumentationUri
);
