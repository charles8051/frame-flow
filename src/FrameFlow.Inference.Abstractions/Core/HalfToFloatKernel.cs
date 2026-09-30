// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Numerics;
using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Core;

/// <summary>
/// Converts halves to floats, the reverse of <see cref="ImageToTensorKernel.ToHalves"/>. A model
/// with fp16 outputs is read through it, so its decoders keep reading floats (#10). Pure.
/// </summary>
internal static class HalfToFloatKernel
{
    /// <summary>
    /// Each of <paramref name="values"/> as a float, into the start of
    /// <paramref name="destination"/>, a vector at a time where the hardware allows.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="values"/>.</exception>
    public static void ToFloats(ReadOnlySpan<Half> values, Span<float> destination) =>
        ToFloats(values, destination, Vector.IsHardwareAccelerated);

    /// <summary>
    /// Each value converted to a float, exactly: every half is a float. The vector loop is
    /// <see cref="FloatBits"/>, which gives <c>(float)value</c>'s bits.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="values"/>.</exception>
    internal static void ToFloats(ReadOnlySpan<Half> values, Span<float> destination, bool vector)
    {
        if (destination.Length < values.Length)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} floats; the values are {values.Length}.", nameof(destination));
        }

        int i = 0;
        if (vector)
        {
            // One vector of 16-bit halves widens to two float vectors.
            var bits = MemoryMarshal.Cast<Half, ushort>(values);
            var floats = MemoryMarshal.Cast<float, uint>(destination);
            int half = Vector<uint>.Count;
            for (; i <= values.Length - Vector<ushort>.Count; i += Vector<ushort>.Count)
            {
                Vector.Widen(new Vector<ushort>(bits[i..]), out var low, out var high);
                FloatBits(low).CopyTo(floats[i..]);
                FloatBits(high).CopyTo(floats[(i + half)..]);
            }
        }

        for (; i < values.Length; i++)
        {
            destination[i] = (float)values[i];
        }
    }

    /// <summary>
    /// <c>(float)value</c>'s bits for each lane's half, held in its low 16 bits, by the same
    /// branch-free steps as the runtime's scalar conversion.
    /// </summary>
    private static Vector<uint> FloatBits(Vector<uint> half)
    {
        var sign = Vector.ShiftLeft(half & new Vector<uint>(0x8000u), 16);
        var exponent = half & new Vector<uint>(0x7C00u);
        var subnormal = Vector.Equals(exponent, Vector<uint>.Zero);
        var special = Vector.Equals(exponent, new Vector<uint>(0x7C00u));

        // Move the exponent and fraction into a float's places and rebias the exponent by 112. An
        // infinity or NaN rebiases by 224, which gives it an all-ones exponent. A subnormal or zero
        // rebiases by 113 instead, as if it had a leading one, and 2^-14 is then subtracted, so the
        // float subtraction normalizes it.
        var lowerBound = subnormal & new Vector<uint>(0x3880_0000u);
        var offset = new Vector<uint>(0x3800_0000u) | lowerBound;
        offset += offset & special;
        var bits = (Vector.ShiftLeft(half, 13) & new Vector<uint>(0x0FFF_E000u)) + offset;
        var magnitude = Vector.AsVectorSingle(bits) - Vector.AsVectorSingle(lowerBound);
        return Vector.AsVectorUInt32(magnitude) | sign;
    }
}
