// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Core;

/// <summary>
/// The shape to allocate for one model output: the shape the model declares, with each dynamic
/// dimension sized by its name. Pure.
/// </summary>
internal static class DeclaredShape
{
    /// <summary>
    /// The shape of output <paramref name="output"/>. A negative dimension in
    /// <paramref name="declared"/> is dynamic, and takes the size <paramref name="dynamicSizes"/>
    /// gives its name in <paramref name="names"/>. Entries in <paramref name="dynamicSizes"/> that
    /// name no dynamic dimension of this output are ignored.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A dynamic dimension's name has no size in <paramref name="dynamicSizes"/>, or a size less
    /// than 1; or <paramref name="names"/> is not the length of <paramref name="declared"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The output is a scalar, a dynamic dimension has no name, or a fixed dimension is larger than
    /// <see cref="int.MaxValue"/>.
    /// </exception>
    public static TensorShape Resolve(
        string output,
        IReadOnlyList<long> declared,
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, int>? dynamicSizes)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count != declared.Count)
        {
            throw new ArgumentException(
                $"Output '{output}' has {declared.Count} dimensions and {names.Count} dimension names.",
                nameof(names));
        }

        if (declared.Count == 0)
            throw new NotSupportedException($"Output '{output}' is a scalar, and a TensorShape has at least one dimension.");

        var dims = new int[declared.Count];
        for (int i = 0; i < dims.Length; i++)
        {
            long dim = declared[i];
            if (dim < 0)
            {
                dims[i] = Dynamic(output, i, names[i], dynamicSizes);
            }
            else if (dim > int.MaxValue)
            {
                throw new NotSupportedException(
                    $"Output '{output}' dimension {i} is {dim}; a TensorShape dimension is at most {int.MaxValue}.");
            }
            else
            {
                dims[i] = (int)dim;
            }
        }

        return new TensorShape(dims);
    }

    private static int Dynamic(string output, int index, string name, IReadOnlyDictionary<string, int>? dynamicSizes)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new NotSupportedException(
                $"Output '{output}' dimension {index} is dynamic and the model does not name it, so no size can be "
                    + "given for it. Rent this output with CpuTensorPool.Rent.");
        }

        if (dynamicSizes is null || !dynamicSizes.TryGetValue(name, out int size))
        {
            throw new ArgumentException(
                $"Output '{output}' dimension {index} ('{name}') is dynamic, and dynamicSizes has no size for '{name}'.",
                nameof(dynamicSizes));
        }

        if (size < 1)
        {
            throw new ArgumentException(
                $"Output '{output}' dimension {index} ('{name}') is dynamic, and dynamicSizes gives '{name}' a size "
                    + $"of {size}; it must be at least 1.",
                nameof(dynamicSizes));
        }

        return size;
    }
}
