// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Element type of an <see cref="ITensor"/>. The set covers the common
/// numeric types used in inference (float / half / int8 / int32) plus a
/// few less-common ones for completeness. Add new entries cautiously —
/// every consumer with an exhaustive switch over <see cref="DType"/>
/// becomes a downstream API consideration when the enum grows.
/// </summary>
public enum DType
{
    /// <summary>32-bit IEEE 754 floating point.</summary>
    Float32,

    /// <summary>16-bit IEEE 754 half-precision floating point.</summary>
    Float16,

    /// <summary>16-bit Brain floating point (1 sign + 8 exponent + 7 mantissa).</summary>
    BFloat16,

    /// <summary>64-bit IEEE 754 floating point.</summary>
    Float64,

    /// <summary>Signed 8-bit integer.</summary>
    Int8,

    /// <summary>Unsigned 8-bit integer (the canonical pixel-channel dtype).</summary>
    UInt8,

    /// <summary>Signed 16-bit integer.</summary>
    Int16,

    /// <summary>Unsigned 16-bit integer.</summary>
    UInt16,

    /// <summary>Signed 32-bit integer.</summary>
    Int32,

    /// <summary>Unsigned 32-bit integer.</summary>
    UInt32,

    /// <summary>Signed 64-bit integer.</summary>
    Int64,

    /// <summary>Unsigned 64-bit integer.</summary>
    UInt64,

    /// <summary>Single-byte boolean (0 = false, non-zero = true).</summary>
    Bool,
}

/// <summary>
/// Helpers for <see cref="DType"/>.
/// </summary>
public static class DTypeExtensions
{
    /// <summary>
    /// The number of bytes a single element of <paramref name="dtype"/> occupies.
    /// </summary>
    public static int ByteSize(this DType dtype) =>
        dtype switch
        {
            DType.Float32 => 4,
            DType.Float16 => 2,
            DType.BFloat16 => 2,
            DType.Float64 => 8,
            DType.Int8 => 1,
            DType.UInt8 => 1,
            DType.Int16 => 2,
            DType.UInt16 => 2,
            DType.Int32 => 4,
            DType.UInt32 => 4,
            DType.Int64 => 8,
            DType.UInt64 => 8,
            DType.Bool => 1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(dtype),
                dtype,
                "Unknown DType. Add a case to DTypeExtensions.ByteSize."
            ),
        };

    /// <summary>
    /// The CLR <see cref="Type"/> that corresponds to <paramref name="dtype"/>.
    /// Used by typed accessors and reflection-based bindings.
    /// </summary>
    public static Type ClrType(this DType dtype) =>
        dtype switch
        {
            DType.Float32 => typeof(float),
            DType.Float16 => typeof(Half),
            DType.BFloat16 => typeof(ushort), // no first-class CLR type; consumers reinterpret
            DType.Float64 => typeof(double),
            DType.Int8 => typeof(sbyte),
            DType.UInt8 => typeof(byte),
            DType.Int16 => typeof(short),
            DType.UInt16 => typeof(ushort),
            DType.Int32 => typeof(int),
            DType.UInt32 => typeof(uint),
            DType.Int64 => typeof(long),
            DType.UInt64 => typeof(ulong),
            DType.Bool => typeof(bool),
            _ => throw new ArgumentOutOfRangeException(nameof(dtype), dtype, null),
        };
}
