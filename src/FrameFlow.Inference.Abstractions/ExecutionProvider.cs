// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// ONNX Runtime execution provider, used by
/// <see cref="IInferenceSessionFactory"/> to select among available
/// EP-specific session implementations at construction time.
/// </summary>
/// <remarks>
/// Enum values are ordered by **fallback safety**, broadest first: CPU
/// is always available, DirectML works on any DX12-capable Windows
/// machine, CUDA requires a specific NVIDIA + CUDA Toolkit setup. The
/// default fallback chain follows this order so the most likely-to-work
/// EP is the last resort.
/// </remarks>
public enum ExecutionProvider
{
    /// <summary>
    /// CPU execution provider — ORT's default. No GPU bootstrap, always
    /// available, slowest. Useful as the universally-available fallback.
    /// </summary>
    Cpu,

    /// <summary>
    /// DirectML execution provider — ORT-DML. DML.dll ships in-box on
    /// Windows 10 1903+ and Windows 11; no separate install. Works on
    /// any DX12-capable adapter (Intel iGPU, AMD GPU, NVIDIA GPU).
    /// </summary>
    DirectML,

    /// <summary>
    /// CUDA execution provider — ORT-CUDA. Requires CUDA Toolkit
    /// installation (cudart, cublas) and cuDNN on PATH. NVIDIA-only;
    /// highest throughput for compatible models.
    /// </summary>
    Cuda,
}
