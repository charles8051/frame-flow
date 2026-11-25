// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// Linux implementation of <see cref="ICudaInstallInstructionProvider"/>.
/// Names the packages in NVIDIA's CUDA apt repository that supply each
/// missing layer, and the ways to put libraries that are already on disk
/// where the loader finds them.
/// </summary>
/// <remarks>
/// <para>
/// The pinned <c>Microsoft.ML.OnnxRuntime.Gpu</c> 1.26 line ships
/// <c>libonnxruntime_providers_cuda.so</c> in its
/// <c>Microsoft.ML.OnnxRuntime.Gpu.Linux</c> dependency, but not the CUDA
/// or cuDNN libraries that provider links against. Those come from the
/// system or from the app's own <c>runtimes/linux-x64/native</c>
/// directory.
/// </para>
/// <para>
/// Package names follow NVIDIA's repository for Ubuntu and Debian; the
/// versioned suffix (<c>-12-9</c>) can be any CUDA 12 minor. Other
/// distributions get the same packages from NVIDIA's repository for that
/// distribution, under the same names.
/// </para>
/// </remarks>
public sealed class LinuxCudaInstallInstructionProvider : ICudaInstallInstructionProvider
{
    private const string LoaderPathNote =
        "If the libraries are already on disk, make their directory visible to the loader: "
        + "list it in a file under /etc/ld.so.conf.d and run 'sudo ldconfig', or set "
        + "LD_LIBRARY_PATH before the process starts (the loader reads it only at startup, "
        + "so setting it from inside the process has no effect), or copy the libraries into "
        + "runtimes/linux-x64/native under the application's base directory.";

    /// <inheritdoc />
    public bool IsSupportedOnCurrentPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <inheritdoc />
    public CudaBootstrapInstruction GetInstruction(MissingNativeComponent component) =>
        component switch
        {
            MissingNativeComponent.GpuDriver => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "No NVIDIA GPU driver detected: libcuda.so.1 could not be loaded. "
                    + "It is installed with the NVIDIA driver, not with the CUDA Toolkit.",
                ActionableStep:
                    "Install the NVIDIA driver for your GPU and reboot. On Ubuntu: "
                    + "'sudo ubuntu-drivers install'. Elsewhere: the distribution's NVIDIA "
                    + "driver package, or NVIDIA's driver downloads. In a container, start it "
                    + "with the NVIDIA Container Toolkit (for example 'docker run --gpus all') "
                    + "so the host driver's libcuda.so.1 is mounted in; the driver library "
                    + "cannot be bundled with the application.",
                DocumentationUri: new Uri("https://www.nvidia.com/Download/index.aspx")
            ),

            MissingNativeComponent.CudaToolkit => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "The CUDA 12 runtime libraries could not all be loaded: "
                    + "libcudart.so.12, libcublas.so.12, libcublasLt.so.12, libcufft.so.11 "
                    + "and libcurand.so.10.",
                ActionableStep:
                    "Add NVIDIA's CUDA apt repository (install the cuda-keyring package from "
                    + "developer.download.nvidia.com/compute/cuda/repos), then "
                    + "'sudo apt install cuda-cudart-12-9 libcublas-12-9 libcufft-12-9 "
                    + "libcurand-12-9'. Any CUDA 12 minor works; CUDA 13 does not, because "
                    + "ONNX Runtime 1.26 is built against CUDA 12. "
                    + LoaderPathNote
                    + " A CUDA Toolkit install keeps them in /usr/local/cuda-12.*/lib64.",
                DocumentationUri: new Uri(
                    "https://docs.nvidia.com/cuda/cuda-installation-guide-linux/"
                )
            ),

            MissingNativeComponent.CuDnn => new CudaBootstrapInstruction(
                Component: component,
                Description: "cuDNN 9 could not be loaded: libcudnn.so.9 was not found.",
                ActionableStep:
                    "From NVIDIA's CUDA apt repository: 'sudo apt install libcudnn9-cuda-12' "
                    + "(the CUDA 12 build; not libcudnn9-cuda-13). Or download the cuDNN 9 "
                    + "for CUDA 12 archive from https://developer.nvidia.com/cudnn-downloads "
                    + "and use its lib directory. "
                    + LoaderPathNote,
                DocumentationUri: new Uri("https://developer.nvidia.com/cudnn-downloads")
            ),

            MissingNativeComponent.OrtCudaProvider => new CudaBootstrapInstruction(
                Component: component,
                Description:
                    "ONNX Runtime's CUDA execution provider could not be appended. "
                    + "Usually caused by a missing driver, CUDA runtime or cuDNN; if those "
                    + "loaded, it indicates a CUDA-major or cuDNN-major mismatch, or a "
                    + "missing libonnxruntime_providers_cuda.so.",
                ActionableStep:
                    "First resolve any GpuDriver / CudaToolkit / CuDnn instructions above. "
                    + "If this flag remains, run 'ldd' on libonnxruntime_providers_cuda.so "
                    + "in the application's runtimes/linux-x64/native directory: every "
                    + "'not found' line names a library the loader cannot see. Check that "
                    + "the installed CUDA major (12) and cuDNN major (9) match what the "
                    + "ONNX Runtime GPU package was built against.",
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
