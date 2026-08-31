// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.Cuda;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// ONNX Runtime inference session configured for the CUDA execution
/// provider, accepting <see cref="ICpuTensor"/> inputs and outputs
/// (per ADR-0049 §3 — FrameFlow binds host memory; the CUDA EP
/// handles device staging internally) and providing an escape hatch
/// for advanced consumers who bring their own device pointers (per
/// the 2026-05-22 NVDEC → resident-inference discussion).
/// </summary>
/// <remarks>
/// <para>
/// The host-memory binding pipeline (the dictionary-form
/// <see cref="OrtInferenceSessionBase.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>,
/// the single-IO convenience overload, and the tensor-binding helpers)
/// lives in <see cref="OrtInferenceSessionBase"/>, shared verbatim with
/// the DirectML EP per ADR-0049 §3. This class adds only the
/// CUDA-specific session-options bootstrap and the device-resident
/// escape hatch.
/// </para>
/// <para>
/// <b>Host-memory binding (default).</b> Most consumers call the
/// dictionary-form <see cref="OrtInferenceSessionBase.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
/// with <see cref="ICpuTensor"/> inputs and pre-allocated
/// <see cref="ICpuTensor"/> outputs. ORT's CUDA EP stages host→device
/// at the inference boundary. On a discrete GPU this costs one PCIe
/// round-trip per Run; the deployment-target tradeoff is documented
/// in ADR-0049 §"What this rules out."
/// </para>
/// <para>
/// <b>Device-resident escape hatch.</b> Consumers that already own a
/// CUDA <c>CUdeviceptr</c> (FFmpeg NVDEC output, custom CUDA kernel
/// output, foreign-library buffer) build a CUDA-memory-info-typed
/// <see cref="OrtValue"/> and pass it to
/// <see cref="Run(IReadOnlyDictionary{string, OrtValue}, IReadOnlyDictionary{string, OrtValue})"/>
/// directly. ORT-CUDA reads / writes the device pointer without
/// staging — zero PCIe traffic. Use <see cref="CreateCudaMemoryInfo"/>
/// to construct the CUDA <see cref="OrtMemoryInfo"/> required for
/// device-pointer binding.
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential Run
/// calls. Concurrent calls against one session are not supported in
/// V1 — ORT supports it but the binding lifecycle here is per-call
/// and not yet re-entrant. Multiple consumers hold their own sessions.
/// </para>
/// <para>
/// <b>Migration note (ADR-0049 §3).</b> This class is the successor
/// to the deleted <c>Crossbar.Onnx.OnnxInferenceSession</c>. The
/// pre-fork class bound <c>ICudaTensor</c>; this one binds
/// <see cref="ICpuTensor"/> by default, with the OrtValue escape
/// hatch preserving device-direct binding for callers who need it.
/// </para>
/// </remarks>
public sealed class CudaInferenceSession : OrtInferenceSessionBase
{
    private readonly int _deviceOrdinal;

    /// <summary>CUDA device ordinal this session is bound to.</summary>
    public int DeviceOrdinal => _deviceOrdinal;

    /// <summary>
    /// Loads a model from <paramref name="modelPath"/> and configures
    /// the CUDA execution provider on <paramref name="deviceOrdinal"/>.
    /// </summary>
    public CudaInferenceSession(string modelPath)
        : this(modelPath, 0, logger: null) { }

    /// <inheritdoc cref="CudaInferenceSession(string)" />
    public CudaInferenceSession(string modelPath, int deviceOrdinal)
        : this(modelPath, deviceOrdinal, logger: null) { }

    /// <inheritdoc cref="CudaInferenceSession(string)" />
    // CA2000: the SessionOptions built here is owned by the base, which
    // disposes it in OrtInferenceSessionBase.Dispose(). The analyzer
    // can't see the ownership handoff across the base initializer.
#pragma warning disable CA2000
    public CudaInferenceSession(
        string modelPath,
        int deviceOrdinal,
        ILogger<CudaInferenceSession>? logger
    )
        : base(modelPath, BuildSessionOptions(deviceOrdinal, logger))
    {
        _deviceOrdinal = deviceOrdinal;
    }
#pragma warning restore CA2000

    /// <summary>
    /// Loads a model from <paramref name="modelBytes"/> and configures
    /// the CUDA execution provider on <paramref name="deviceOrdinal"/>.
    /// </summary>
    public CudaInferenceSession(byte[] modelBytes)
        : this(modelBytes, 0, logger: null) { }

    /// <inheritdoc cref="CudaInferenceSession(byte[])" />
    public CudaInferenceSession(byte[] modelBytes, int deviceOrdinal)
        : this(modelBytes, deviceOrdinal, logger: null) { }

    /// <inheritdoc cref="CudaInferenceSession(byte[])" />
    // CA2000: see the string-path overload above — the base owns and
    // disposes the SessionOptions.
#pragma warning disable CA2000
    public CudaInferenceSession(
        byte[] modelBytes,
        int deviceOrdinal,
        ILogger<CudaInferenceSession>? logger
    )
        : base(modelBytes, BuildSessionOptions(deviceOrdinal, logger))
    {
        _deviceOrdinal = deviceOrdinal;
    }
#pragma warning restore CA2000

    /// <summary>
    /// Constructs an <see cref="OrtMemoryInfo"/> describing CUDA device
    /// memory on this session's device. Use this when building
    /// <see cref="OrtValue"/>s for the device-resident escape hatch
    /// <see cref="Run(IReadOnlyDictionary{string, OrtValue}, IReadOnlyDictionary{string, OrtValue})"/>.
    /// </summary>
    public OrtMemoryInfo CreateCudaMemoryInfo() =>
        new OrtMemoryInfo(
            "Cuda",
            OrtAllocatorType.DeviceAllocator,
            _deviceOrdinal,
            OrtMemType.Default);

    /// <summary>
    /// Device-resident escape hatch. Runs the model with caller-built
    /// <see cref="OrtValue"/>s. The caller is responsible for the
    /// <see cref="OrtValue"/>s' lifetimes; this method does not
    /// dispose them.
    /// </summary>
    /// <remarks>
    /// Use when binding CUDA device pointers (FFmpeg NVDEC frames,
    /// custom kernel outputs) directly without host staging.
    /// Construct the input / output <see cref="OrtValue"/>s with
    /// <see cref="OrtValue.CreateTensorValueWithData"/> against the
    /// <see cref="OrtMemoryInfo"/> returned by
    /// <see cref="CreateCudaMemoryInfo"/>.
    /// </remarks>
    public void Run(
        IReadOnlyDictionary<string, OrtValue> inputs,
        IReadOnlyDictionary<string, OrtValue> outputs
    )
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        ValidateNames(inputs.Keys, InputNames, "input");
        ValidateNames(outputs.Keys, OutputNames, "output");

        using var binding = Session.CreateIoBinding();

        foreach (var (name, value) in inputs)
            binding.BindInput(name, value);
        foreach (var (name, value) in outputs)
            binding.BindOutput(name, value);

        Session.RunWithBinding(RunOptions, binding);
    }

    // Preserves the pre-refactor CUDA dispose behavior verbatim: the
    // original CudaInferenceSession.Dispose() called _cpuMemoryInfo.Dispose().
    // Note CpuMemoryInfo is OrtMemoryInfo.DefaultInstance (a shared ORT
    // singleton), which the DML EP deliberately does NOT dispose — see
    // OrtInferenceSessionBase.DisposeCpuMemoryInfo. The divergence is a
    // known latent smell carried over unchanged; reconciling it is out of
    // scope for the staging-hoist (behavior must stay identical).
    protected override void DisposeCpuMemoryInfo() => CpuMemoryInfo.Dispose();

    private static SessionOptions BuildSessionOptions(int deviceOrdinal, ILogger? logger)
    {
        // ORT's CUDA EP depends on cudart / cublas / cuDNN being
        // loadable. The CUDA Toolkit installer adds %CUDA_PATH%\bin to
        // PATH automatically but the cuDNN installer typically does
        // not. Resolve both before touching ORT — first call wins;
        // subsequent calls are no-ops.
        CudaDllResolver.EnsureLoadable(logger);

        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_CUDA(deviceOrdinal);
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}
