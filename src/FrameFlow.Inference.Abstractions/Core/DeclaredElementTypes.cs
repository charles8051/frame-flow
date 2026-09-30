// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Core;

/// <summary>
/// The element type to bind for a model's input or output, from the types its session declares
/// through <see cref="IElementTypedSession"/>, or none when it declares none. Pure.
/// </summary>
internal static class DeclaredElementTypes
{
    /// <summary>
    /// The type of tensor <paramref name="index"/> of <paramref name="names"/>: the one in
    /// <paramref name="declared"/>, or <see cref="DType.Float32"/> when <paramref name="declared"/>
    /// is null.
    /// </summary>
    /// <param name="declared">The session's types in model order, or null when it reports none.</param>
    /// <param name="names">The session's input or output names.</param>
    /// <param name="index">Which of them.</param>
    /// <param name="kind"><c>input</c> or <c>output</c>, for the message.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="declared"/> does not hold one type per name.
    /// </exception>
    public static DType OrFloat32(IReadOnlyList<DType>? declared, IReadOnlyList<string> names, int index, string kind)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, names.Count);
        if (declared is null)
            return DType.Float32;
        if (declared.Count != names.Count)
        {
            throw new InvalidOperationException(
                $"The session declares {declared.Count} {kind} element types for {names.Count} {kind}s.");
        }

        return declared[index];
    }

    /// <summary>
    /// <see cref="OrFloat32"/>, for a <paramref name="reader"/> that binds floats or halves only.
    /// </summary>
    /// <param name="declared">The session's types in model order, or null when it reports none.</param>
    /// <param name="names">The session's input or output names.</param>
    /// <param name="index">Which of them.</param>
    /// <param name="kind"><c>input</c> or <c>output</c>, for the message.</param>
    /// <param name="reader">What binds the tensor, such as a detector's type name, for the message.</param>
    /// <returns><see cref="DType.Float32"/> or <see cref="DType.Float16"/>.</returns>
    /// <exception cref="NotSupportedException">The declared type is neither. The message names the tensor and its type.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="declared"/> does not hold one type per name.
    /// </exception>
    public static DType Floating(
        IReadOnlyList<DType>? declared, IReadOnlyList<string> names, int index, string kind, string reader)
    {
        var type = OrFloat32(declared, names, index, kind);
        if (type is DType.Float32 or DType.Float16)
            return type;

        throw new NotSupportedException(
            $"The model's {kind} '{names[index]}' is {type}; {reader} takes Float32 or Float16 tensors.");
    }
}
