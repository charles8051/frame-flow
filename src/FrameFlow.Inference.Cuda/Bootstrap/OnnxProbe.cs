// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// One-shot probe verifying ONNX Runtime is loadable and the CUDA
/// execution provider can be appended to a fresh
/// <see cref="SessionOptions"/>. A process-cached check that
/// <see cref="CudaBootstrapper"/> reports as
/// <see cref="MissingNativeComponent.OrtCudaProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// ORT-Gpu does <em>not</em> bundle the CUDA or cuDNN libraries. Its
/// native packages carry ONNX Runtime and the CUDA provider library
/// (<c>onnxruntime_providers_cuda.dll</c> on Windows,
/// <c>libonnxruntime_providers_cuda.so</c> from
/// <c>Microsoft.ML.OnnxRuntime.Gpu.Linux</c> on Linux), and that provider
/// links against the CUDA 12 runtime, cuBLAS, cuFFT, cuRAND and cuDNN 9,
/// which have to come from a system install or the app-local
/// <c>runtimes/{rid}/native</c> directory, plus the NVIDIA driver.
/// Appending the provider loads it, so this probe fails when any of those
/// is missing. Call <see cref="CudaDllResolver.EnsureLoadable"/> first, or
/// read <see cref="CudaBootstrapper.Probe"/>, which does, so that the
/// libraries the resolver can find are in place before the result is
/// cached.
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
