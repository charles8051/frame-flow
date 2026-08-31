// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// Windows implementation of <see cref="ICudaInstallInstructionProvider"/>.
/// Points consumers at NVIDIA's developer-portal installers for the CUDA
/// Toolkit, cuDNN, and the GPU driver. winget is intentionally not used:
/// as of 2026-05, the only <c>Nvidia.CUDA</c> winget package is CUDA 13.x
/// (incompatible with our pinned ORT 1.26.0, which builds against
/// CUDA 12), and cuDNN has no winget package at all.
/// </summary>
/// <remarks>
/// Instruction strings are intentionally simple: one URL per missing
/// component plus a short version-pin note. The consumer decides how to
/// surface them — log line, MessageBox, copy-to-clipboard button, etc.
/// </remarks>
public sealed class WindowsCudaInstallInstructionProvider : ICudaInstallInstructionProvider
{
    /// <inheritdoc />
    public bool IsSupportedOnCurrentPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public CudaBootstrapInstruction GetInstruction(MissingNativeComponent component) =>
        component switch
        {
            MissingNativeComponent.GpuDriver => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "No NVIDIA GPU driver detected. The driver provides nvcuda.dll "
                    + "and exposes CUDA-capable devices to the system.",
                ActionableStep:
                    "Install or update the NVIDIA driver for your GPU from "
                    + "https://www.nvidia.com/Download/index.aspx, then reboot.",
                DocumentationUri: new Uri("https://www.nvidia.com/Download/index.aspx")
            ),

            MissingNativeComponent.CudaToolkit => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "CUDA Toolkit 12.x is not installed. cudart64_12.dll was not "
                    + "found via %CUDA_PATH% or the canonical install root "
                    + "(%ProgramFiles%\\NVIDIA GPU Computing Toolkit\\CUDA\\v12.*).",
                ActionableStep:
                    "Download and run the CUDA Toolkit 12.x installer from "
                    + "https://developer.nvidia.com/cuda-toolkit-archive — pick the "
                    + "latest 12.x release, NOT 13.x (ORT 1.26.0 builds against "
                    + "CUDA 12). The winget 'Nvidia.CUDA' package currently ships "
                    + "13.x only, so do not use winget for this step.",
                DocumentationUri: new Uri("https://developer.nvidia.com/cuda-toolkit-archive")
            ),

            MissingNativeComponent.CuDnn => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "cuDNN 9.x is not installed. cudnn64_9.dll was not found under "
                    + "%ProgramFiles%\\NVIDIA\\CUDNN\\v9.*\\bin\\<cuda-version>\\x64.",
                ActionableStep:
                    "Download and run the cuDNN 9.x-for-CUDA-12 installer from "
                    + "https://developer.nvidia.com/cudnn-downloads (requires an "
                    + "NVIDIA developer login). cuDNN is not on winget.",
                DocumentationUri: new Uri("https://developer.nvidia.com/cudnn-downloads")
            ),

            MissingNativeComponent.OrtCudaProvider => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "ONNX Runtime's CUDA execution provider could not be appended. "
                    + "Usually caused by missing CUDA Toolkit or cuDNN; if those are "
                    + "present, this indicates an ORT / CUDA-major / cuDNN-major "
                    + "version mismatch (e.g. ORT 1.26 wants CUDA 12, but CUDA 13 "
                    + "is installed).",
                ActionableStep:
                    "First resolve any CudaToolkit / CuDnn instructions above. If "
                    + "this flag remains after both are installed, verify that the "
                    + "ORT-Gpu package version matches the installed CUDA major and "
                    + "cuDNN major (see the ORT CUDA provider compatibility matrix).",
                DocumentationUri: new Uri(
                    "https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html"
                )
            ),

            MissingNativeComponent.None => throw new ArgumentOutOfRangeException(
                nameof(component),
                component,
                "MissingNativeComponent.None has no associated instruction."
            ),

            _ => throw new ArgumentOutOfRangeException(
                nameof(component),
                component,
                "Unknown MissingNativeComponent flag — combine-multiple-flag values "
                    + "are not accepted; pass a single flag."
            ),
        };
}
