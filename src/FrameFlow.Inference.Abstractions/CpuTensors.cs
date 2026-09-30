// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference;

/// <summary>Host tensors whose element type is known only at run time, as a model declares it.</summary>
internal static class CpuTensors
{
    /// <summary>Rents a tensor of <paramref name="shape"/> whose elements are <paramref name="dtype"/>.</summary>
    /// <param name="pool">The pool to rent from.</param>
    /// <param name="dtype">The element type.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="what">What the tensor is, such as <c>Output 'boxes'</c>, for the message.</param>
    /// <exception cref="NotSupportedException">No <see cref="CpuTensor{T}"/> holds <paramref name="dtype"/>, as for <see cref="DType.BFloat16"/>.</exception>
    public static ICpuTensor Rent(CpuTensorPool pool, DType dtype, TensorShape shape, string what) =>
        dtype switch
        {
            DType.Float32 => pool.Rent<float>(shape),
            DType.Float16 => pool.Rent<Half>(shape),
            DType.Float64 => pool.Rent<double>(shape),
            DType.Int8 => pool.Rent<sbyte>(shape),
            DType.UInt8 => pool.Rent<byte>(shape),
            DType.Int16 => pool.Rent<short>(shape),
            DType.UInt16 => pool.Rent<ushort>(shape),
            DType.Int32 => pool.Rent<int>(shape),
            DType.UInt32 => pool.Rent<uint>(shape),
            DType.Int64 => pool.Rent<long>(shape),
            DType.UInt64 => pool.Rent<ulong>(shape),
            DType.Bool => pool.Rent<bool>(shape),
            _ => throw new NotSupportedException($"{what} is {dtype}, which no CpuTensor holds."),
        };

    /// <summary>Sets every element of <paramref name="tensor"/>, which the caller rented and owns, to zero.</summary>
    public static void Clear(ICpuTensor tensor) => MemoryMarshal.AsMemory(tensor.Bytes).Span.Clear();
}
