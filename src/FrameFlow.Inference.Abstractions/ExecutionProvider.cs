// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// ONNX Runtime execution provider, used by
/// <see cref="IInferenceSessionFactory"/> to select among available
/// EP-specific session implementations at construction time.
/// </summary>
/// <remarks>
/// The values' numeric order means nothing. The fallback order
/// <see cref="InferenceSessionFactoryBuilder"/> uses is its own, and puts CPU last.
/// </remarks>
public enum ExecutionProvider
{
    /// <summary>
    /// CPU execution provider — ORT's default. No GPU bootstrap, always
    /// available, slowest. Useful as the universally-available fallback.
    /// Every inference package runs it; <c>FrameFlow.Inference.Cpu</c> is the
    /// one to reference when nothing else is.
    /// </summary>
    Cpu,

    /// <summary>
    /// DirectML execution provider — <c>FrameFlow.Inference.Dml</c>, which carries
    /// its own DirectML.dll. Works on any DX12-capable adapter (Intel iGPU, AMD
    /// GPU, NVIDIA GPU).
    /// </summary>
    DirectML,

    /// <summary>
    /// CUDA execution provider — ORT-CUDA. Requires CUDA Toolkit
    /// installation (cudart, cublas) and cuDNN on PATH. NVIDIA-only;
    /// highest throughput for compatible models.
    /// </summary>
    Cuda,

    /// <summary>
    /// ONNX Runtime through Windows ML — <c>FrameFlow.Inference.WinML</c>. Reaches the vendor
    /// providers Windows installs (TensorRT-RTX, OpenVINO, QNN) on Windows 11 24H2 or later, and
    /// CPU and DirectML below that.
    /// </summary>
    WindowsML,

    /// <summary>
    /// ONNX Runtime's OpenVINO provider, from Intel's build — <c>FrameFlow.Inference.OpenVino</c>,
    /// Windows x64 only. Runs Intel GPUs and NPUs, and the CPU, below Windows ML's 24H2 floor too.
    /// </summary>
    OpenVino,
}
