// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// An <see cref="IInferenceSession"/> that can leave outputs for its execution provider to
/// allocate, so it runs a model whose output shape is known only once the run ends: a detector
/// that filters inside its graph returns as many boxes as it finds.
/// </summary>
/// <remarks>
/// <para>
/// Every session in FrameFlow's ONNX Runtime packages implements this. An
/// <see cref="IInferenceSessionFactory"/> returns <see cref="IInferenceSession"/>, so test for it
/// with <c>session is IAllocatingSession</c>.
/// </para>
/// <para>
/// It is a separate interface so that an <see cref="IInferenceSession"/> implemented elsewhere
/// keeps compiling, and a session that cannot allocate says so in its type rather than by
/// throwing.
/// </para>
/// </remarks>
public interface IAllocatingSession : IInferenceSession
{
    /// <summary>
    /// Runs the model on <paramref name="inputs"/>. Each output in <paramref name="outputs"/> is
    /// written in place, as
    /// <see cref="IInferenceSession.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
    /// writes it. Every other output of the model is allocated by the execution provider, in CPU
    /// memory at the shape the run produced, and returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned tensors belong to the caller: disposing the set releases them. They stay valid
    /// after the session is disposed. A tensor has a dimension of 0 where the run produced none,
    /// and a scalar output has the default <see cref="TensorShape"/>, of rank 0.
    /// </para>
    /// <para>
    /// Outputs bound in <paramref name="outputs"/> cost no allocation, as in <c>Run</c>; give the
    /// ones whose shape is fixed there and leave out only those whose shape depends on the input.
    /// </para>
    /// </remarks>
    /// <param name="inputs">Map of input name to input tensor.</param>
    /// <param name="outputs">Map of output name to pre-allocated output tensor, or null to allocate every output.</param>
    /// <returns>The outputs not in <paramref name="outputs"/>, by name.</returns>
    SessionOutputs<ICpuTensor> RunAllocating(
        IReadOnlyDictionary<string, ICpuTensor> inputs,
        IReadOnlyDictionary<string, ICpuTensor>? outputs = null);
}
