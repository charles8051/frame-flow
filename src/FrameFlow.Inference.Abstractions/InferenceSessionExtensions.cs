// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.Core;

namespace FrameFlow.Inference;

/// <summary>Helpers over <see cref="IInferenceSession"/>.</summary>
public static class InferenceSessionExtensions
{
    /// <summary>
    /// Rents one tensor per model output from <paramref name="pool"/>, at the shape the model
    /// declares, for
    /// <see cref="IInferenceSession.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
    /// to write into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dimension the model leaves dynamic takes its size from <paramref name="dynamicSizes"/>, by
    /// the name the model gives it in <see cref="IInferenceSession.OutputDimensionNames"/>:
    /// <c>{ ["batch"] = 4 }</c> sizes every output dimension named <c>batch</c>. Entries that name no
    /// dynamic output dimension are ignored.
    /// </para>
    /// <para>
    /// Every output is rented as <typeparamref name="T"/>, and a <typeparamref name="T"/> that does
    /// not match an output fails in <c>Run</c>. An <see cref="IElementTypedSession"/> reports each
    /// output's type in <see cref="IElementTypedSession.OutputElementTypes"/>. Rent the outputs of a
    /// model whose outputs differ in element type one at a time with
    /// <see cref="CpuTensorPool.Rent{T}"/>.
    /// </para>
    /// <para>
    /// Every shape is resolved before anything is rented, and a rent that fails returns the tensors
    /// already rented, so a throw leaves nothing outstanding in <paramref name="pool"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The element type of every output.</typeparam>
    /// <param name="session">The session whose outputs to rent.</param>
    /// <param name="pool">The pool to rent from.</param>
    /// <param name="dynamicSizes">The size of each named dynamic dimension.</param>
    /// <returns>The tensors by output name. Dispose it to return them to <paramref name="pool"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A dynamic dimension's name has no size in <paramref name="dynamicSizes"/>, or a size less than
    /// 1. The message names the output, the dimension and its name.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// An output is a scalar, has a dynamic dimension the model does not name, or has a fixed
    /// dimension larger than <see cref="int.MaxValue"/>. The message names the output.
    /// </exception>
    public static SessionOutputs<CpuTensor<T>> RentOutputs<T>(
        this IInferenceSession session,
        CpuTensorPool pool,
        IReadOnlyDictionary<string, int>? dynamicSizes = null)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pool);

        var names = session.OutputNames;
        var declared = session.OutputShapes;
        var dimensionNames = session.OutputDimensionNames;
        var shapes = new TensorShape[names.Count];
        for (int i = 0; i < shapes.Length; i++)
            shapes[i] = DeclaredShape.Resolve(names[i], declared[i], dimensionNames[i], dynamicSizes);

        var rented = new List<KeyValuePair<string, CpuTensor<T>>>(shapes.Length);
        try
        {
            for (int i = 0; i < shapes.Length; i++)
                rented.Add(new(names[i], pool.Rent<T>(shapes[i])));
            return new SessionOutputs<CpuTensor<T>>(rented);
        }
        catch
        {
            foreach (var (_, tensor) in rented)
                tensor.Dispose();
            throw;
        }
    }
}
