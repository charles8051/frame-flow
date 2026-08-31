// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// One-shot probe verifying ONNX Runtime is loadable and the CUDA
/// execution provider can be appended to a fresh
/// <see cref="SessionOptions"/>. Mirrors the role of
/// <c>Crossbar.Cuda.CudaProbe</c>: a process-cached check that the
/// test harness uses to skip GPU-dependent tests on machines without
/// the right native dependencies.
/// </summary>
/// <remarks>
/// <para>
/// ORT-Gpu bundles its own CUDA runtime libraries — this probe doesn't
/// require the toolkit to be installed system-wide. It does require the
/// NVIDIA driver to be present (which is what
/// <c>Crossbar.Cuda.CudaProbe</c> verifies separately). For
/// inference-running tests, both probes must succeed.
/// </para>
/// <para>
/// The probe is intentionally narrow: it constructs a
/// <see cref="SessionOptions"/>, calls
/// <see cref="SessionOptions.AppendExecutionProvider_CUDA(int)"/>, and
/// reports whether that succeeds. It does <em>not</em> load a model —
/// model-load failures are model-specific and shouldn't gate the
/// "is the runtime usable" question.
/// </para>
/// </remarks>
public static class OnnxProbe
{
    private static readonly Lazy<ProbeResult> _result = new(Run, isThreadSafe: true);

    /// <summary>True when the CUDA execution provider can be appended.</summary>
    public static bool CudaExecutionProviderAvailable => _result.Value.CudaAvailable;

    /// <summary>The ONNX Runtime version string reported by the library.</summary>
    public static string OrtVersion => _result.Value.OrtVersion;

    /// <summary>The exception raised by the probe, or <see langword="null"/> if it succeeded.</summary>
    public static Exception? Failure => _result.Value.Failure;

    private static ProbeResult Run()
    {
        string ortVersion;
        try
        {
            ortVersion = OrtEnv.Instance().GetVersionString();
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, "unknown", ex);
        }

        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_CUDA(deviceId: 0);
            return new ProbeResult(true, ortVersion, null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, ortVersion, ex);
        }
    }

    private readonly record struct ProbeResult(
        bool CudaAvailable,
        string OrtVersion,
        Exception? Failure
    );
}
