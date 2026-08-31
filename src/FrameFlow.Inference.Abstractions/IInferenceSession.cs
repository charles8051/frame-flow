// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Inference;

/// <summary>
/// EP-agnostic inference contract. Implementations wrap an ONNX Runtime
/// session configured with a particular execution provider (CUDA,
/// DirectML, TensorRT, future OpenVINO) and bind inputs / outputs via
/// host memory (<see cref="ICpuTensor"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why host-memory only at the abstraction level.</b> Per ADR-0049 §3,
/// every EP wrapper binds <see cref="ICpuTensor"/>. The EP handles its
/// own device staging internally (CUDA EP via <c>cudaMemcpyAsync</c>,
/// DirectML EP via D3D12 upload buffers, TensorRT EP via CUDA staging).
/// Consumers that want device-resident binding for a specific EP use
/// the EP-specific escape hatch on the concrete session type (e.g.,
/// <c>CudaInferenceSession.Run(IReadOnlyDictionary&lt;string, OrtValue&gt;)</c>);
/// the EP-agnostic surface stays uniform.
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential
/// <see cref="Run"/> calls. Concurrent calls against one session are
/// not supported in V1.
/// </para>
/// <para>
/// <b>Output allocation.</b> Callers pre-allocate output tensors with
/// the shape the model produces and pass them into <see cref="Run"/>.
/// The EP writes results into the caller-owned tensors. This puts
/// allocation cost on the caller's pool (visible via pool counters)
/// rather than hiding it inside the EP.
/// </para>
/// </remarks>
public interface IInferenceSession : IDisposable
{
    /// <summary>Names of the model's inputs, in declaration order.</summary>
    IReadOnlyList<string> InputNames { get; }

    /// <summary>Names of the model's outputs, in declaration order.</summary>
    IReadOnlyList<string> OutputNames { get; }

    /// <summary>
    /// Static shape of each input, in <see cref="InputNames"/> order. A
    /// dimension is <c>-1</c> when the model declares it dynamic. Exposed
    /// so shape-aware consumers (e.g. <c>YoloModelDescriptor.FromSession</c>,
    /// ADR-0050 §2) can self-configure from the loaded model instead of
    /// hardcoding a shape.
    /// </summary>
    IReadOnlyList<IReadOnlyList<long>> InputShapes { get; }

    /// <summary>
    /// Static shape of each output, in <see cref="OutputNames"/> order. A
    /// dimension is <c>-1</c> when the model declares it dynamic.
    /// </summary>
    IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; }

    /// <summary>
    /// Runs the model with the supplied inputs and writes outputs into
    /// the supplied output tensors. Both dictionaries are keyed by
    /// model input / output name.
    /// </summary>
    /// <param name="inputs">Map of input name → input tensor.</param>
    /// <param name="outputs">Map of output name → pre-allocated output tensor.</param>
    void Run(
        IReadOnlyDictionary<string, ICpuTensor> inputs,
        IReadOnlyDictionary<string, ICpuTensor> outputs);

    /// <summary>
    /// Convenience overload for single-input / single-output models.
    /// Default implementation wraps both tensors in single-entry
    /// dictionaries keyed by the model's <see cref="InputNames"/>[0]
    /// and <see cref="OutputNames"/>[0]; concrete implementations can
    /// override for hot-path performance if it ever matters.
    /// </summary>
    void Run(ICpuTensor input, ICpuTensor output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (InputNames.Count != 1 || OutputNames.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model has {InputNames.Count} input(s) and {OutputNames.Count} output(s); "
                    + "use the dictionary-form Run() for multi-input / multi-output models."
            );
        }
        Run(
            new Dictionary<string, ICpuTensor> { [InputNames[0]] = input },
            new Dictionary<string, ICpuTensor> { [OutputNames[0]] = output }
        );
    }
}
