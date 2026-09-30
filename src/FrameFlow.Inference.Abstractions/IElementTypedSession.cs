// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// An <see cref="IInferenceSession"/> that reports the element type its model declares for each
/// input and output, so a caller can bind tensors of that type: a model exported with fp16 inputs
/// and outputs takes and returns <see cref="Half"/> tensors (#520).
/// </summary>
/// <remarks>
/// <para>
/// Every session in FrameFlow's ONNX Runtime packages implements this. An
/// <see cref="IInferenceSessionFactory"/> returns <see cref="IInferenceSession"/>, so test for it
/// with <c>session is IElementTypedSession</c>. For a session that does not implement it, the types
/// are unknown, and FrameFlow's inference operator and detectors bind 32-bit floats.
/// </para>
/// <para>
/// It is a separate interface so that an <see cref="IInferenceSession"/> implemented elsewhere
/// keeps compiling, and a session that cannot say what its model declares says so in its type
/// rather than by reporting a type it does not know.
/// </para>
/// </remarks>
public interface IElementTypedSession : IInferenceSession
{
    /// <summary>The element type of each input, in <see cref="IInferenceSession.InputNames"/> order.</summary>
    /// <exception cref="NotSupportedException">
    /// An input's element type has no <see cref="DType"/>, such as a string. The message names the input.
    /// </exception>
    IReadOnlyList<DType> InputElementTypes { get; }

    /// <summary>The element type of each output, in <see cref="IInferenceSession.OutputNames"/> order.</summary>
    /// <exception cref="NotSupportedException">
    /// An output's element type has no <see cref="DType"/>, such as a string. The message names the output.
    /// </exception>
    IReadOnlyList<DType> OutputElementTypes { get; }
}
