// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference;

/// <summary>
/// ONNX Runtime inference session on the <b>default CPU execution provider</b> — the
/// universally-available, device-loss-immune fallback (<see cref="ExecutionProvider.Cpu"/>).
/// Accepts <see cref="ICpuTensor"/> inputs/outputs; the host-memory binding pipeline lives in
/// <see cref="OrtInferenceSessionBase"/>, shared verbatim with the DirectML and CUDA sessions
/// (ADR-0049 §3). This class adds only the (empty) CPU session-options configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>No execution-provider append.</b> ORT runs on its built-in CPU EP when no GPU EP is
/// registered, so this configures nothing beyond default <see cref="SessionOptions"/>. Unlike
/// <c>DmlInferenceSession</c>, it does <b>not</b> lower the graph optimizer to BASIC or disable the
/// memory-pattern optimizer — those are DirectML requirements that would only slow CPU inference;
/// the CPU EP wants ORT's full default optimizations.
/// </para>
/// <para>
/// <b>No native package of its own.</b> Like the rest of <c>FrameFlow.Inference.Ort</c>, this
/// references only the managed ONNX Runtime assembly; the native <c>onnxruntime</c> comes from the
/// consuming app (e.g. the DirectML build, whose native library already carries the CPU EP). So a
/// consumer that has any ORT native present can construct this session as a fallback with no extra
/// dependency — which is exactly its purpose in a GPU device-loss recovery ladder.
/// </para>
/// <para><b>Threading.</b> A single session is safe for sequential Run calls; concurrent calls
/// against one session are not supported (same contract as the GPU sessions).</para>
/// </remarks>
public sealed class CpuInferenceSession : OrtInferenceSessionBase
{
    /// <summary>Loads a model from <paramref name="modelPath"/> on the CPU EP.</summary>
    public CpuInferenceSession(string modelPath)
        : this(modelPath, logger: null) { }

    /// <inheritdoc cref="CpuInferenceSession(string)" />
    // CA2000: the SessionOptions is owned by the base, which disposes it in
    // OrtInferenceSessionBase.Dispose(); the analyzer can't see the handoff across base().
#pragma warning disable CA2000
    public CpuInferenceSession(string modelPath, ILogger<CpuInferenceSession>? logger)
        : base(modelPath, BuildSessionOptions())
    {
        _ = logger;   // reserved for future CPU tuning (thread counts, etc.)
    }
#pragma warning restore CA2000

    /// <summary>Loads a model from <paramref name="modelBytes"/> on the CPU EP.</summary>
    public CpuInferenceSession(byte[] modelBytes)
        : this(modelBytes, logger: null) { }

    /// <inheritdoc cref="CpuInferenceSession(byte[])" />
#pragma warning disable CA2000
    public CpuInferenceSession(byte[] modelBytes, ILogger<CpuInferenceSession>? logger)
        : base(modelBytes, BuildSessionOptions())
    {
        _ = logger;
    }
#pragma warning restore CA2000

    // Default options => ORT's built-in CPU EP with full default graph optimization. Nothing here
    // can throw after allocation, so (unlike the DML variant) no dispose-on-failure guard is needed.
    private static SessionOptions BuildSessionOptions() => new();
}
