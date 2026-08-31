// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml;

/// <summary>
/// ONNX Runtime inference session configured for the DirectML execution
/// provider, accepting <see cref="ICpuTensor"/> inputs and outputs. The
/// DML EP handles host→device staging through D3D12 upload buffers.
/// Sibling to <see cref="FrameFlow.Inference.Cuda.CudaInferenceSession"/>
/// per ADR-0049 §3.
/// </summary>
/// <remarks>
/// <para>
/// The host-memory binding pipeline (the dictionary-form
/// <see cref="OrtInferenceSessionBase.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>,
/// the single-IO convenience overload, and the tensor-binding helpers)
/// lives in <see cref="OrtInferenceSessionBase"/>, shared verbatim with
/// the CUDA EP per ADR-0049 §3. This class adds only the DirectML
/// session-options configuration.
/// </para>
/// <para>
/// <b>Adapter selection.</b> V1 uses the default DirectML adapter. An
/// adapter-selecting overload (by LUID or device-id) can land
/// additively once a consumer asks for it; the surface stays open.
/// </para>
/// <para>
/// <b>No bootstrap.</b> DirectML.dll ships in-box on Windows 10 1903+
/// and Windows 11. No PATH manipulation, no cuDNN equivalents, no
/// system install gating — the EP is loadable on any DX12-capable
/// Windows host.
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential Run
/// calls. Concurrent calls against one session are not supported in
/// V1.
/// </para>
/// <para>
/// <b>GPU device loss is not recoverable in-process.</b> After a
/// Windows TDR, <c>AppendExecutionProvider_DML</c> fails permanently
/// for the lifetime of the process with <c>887A0006</c>
/// (<c>DXGI_ERROR_DEVICE_HUNG</c>). This is not something FrameFlow
/// can work around: ORT already builds a fresh <c>IDXGIFactory4</c>,
/// adapter, and <c>ID3D12Device</c> on every registration attempt, and
/// it is the underlying <c>D3D12CreateDevice</c> that fails. Owning the
/// device ourselves (via the C API's
/// <c>OrtSessionOptionsAppendExecutionProviderEx_DML</c>) would call
/// the same failing API one frame higher up. Recovery is a process
/// restart. See
/// <c>docs/investigations/2026-08-14-dml-in-process-tdr-recovery.md</c>
/// before re-opening this — the analysis is done and the answer is no.
/// </para>
/// <para>
/// <b>No device-resident escape hatch in V1.</b> ADR-0022 explicitly
/// defers D3D12-resource binding (the zero-copy iGPU path). On iGPU
/// the host-staging cost is effectively free (unified memory); on
/// dGPU it costs PCIe bandwidth per Run but is acceptable for the
/// deployment targets DML supports.
/// </para>
/// </remarks>
public sealed class DmlInferenceSession : OrtInferenceSessionBase
{
    /// <summary>Loads a model from <paramref name="modelPath"/> and configures the DirectML EP.</summary>
    public DmlInferenceSession(string modelPath)
        : this(modelPath, logger: null) { }

    /// <inheritdoc cref="DmlInferenceSession(string)" />
    // CA2000: the SessionOptions built here is owned by the base, which
    // disposes it in OrtInferenceSessionBase.Dispose(). The analyzer
    // can't see the ownership handoff across the base initializer.
#pragma warning disable CA2000
    public DmlInferenceSession(string modelPath, ILogger<DmlInferenceSession>? logger)
        : base(modelPath, BuildSessionOptions())
    {
        _ = logger;   // reserved for future DML adapter / fallback logging
    }
#pragma warning restore CA2000

    /// <summary>Loads a model from <paramref name="modelBytes"/> and configures the DirectML EP.</summary>
    public DmlInferenceSession(byte[] modelBytes)
        : this(modelBytes, logger: null) { }

    /// <inheritdoc cref="DmlInferenceSession(byte[])" />
    // CA2000: see the string-path overload above — the base owns and
    // disposes the SessionOptions.
#pragma warning disable CA2000
    public DmlInferenceSession(byte[] modelBytes, ILogger<DmlInferenceSession>? logger)
        : base(modelBytes, BuildSessionOptions())
    {
        _ = logger;
    }
#pragma warning restore CA2000

    private static SessionOptions BuildSessionOptions()
    {
        var options = new SessionOptions();
        try
        {
            // DirectML EP needs the graph optimizer set to BASIC; the
            // EP's pattern matcher requires conv/matmul/etc. to be in
            // their canonical shape before DML rewrites.
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
            options.EnableMemoryPattern = false;   // required by DML EP
            options.AppendExecutionProvider_DML();
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}
