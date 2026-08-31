// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Blittable mirror of FFmpeg's <c>AVRational</c> struct:
/// a rational number expressed as a numerator/denominator pair.
/// </summary>
/// <remarks>
/// <para>
/// Layout matches FFmpeg's <c>typedef struct AVRational { int num; int den; } AVRational</c>
/// exactly — two consecutive 32-bit integers, 8 bytes total. On x64 this fits in a single
/// 64-bit register, which is what both the Windows and Linux calling conventions expect
/// for an 8-byte struct passed by value.
/// </para>
/// <para>
/// The earlier broken binding of <see cref="FFAvUtil.av_rescale_q"/> flattened these two
/// struct parameters into four loose <c>int</c> arguments, spreading them across four
/// argument slots instead of two registers. The callee read a garbage denominator and
/// returned <c>AV_NOPTS_VALUE</c>. Using this struct by value is the ABI-correct fix.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct AvRational
{
    /// <summary>Numerator.</summary>
    public readonly int Num;

    /// <summary>Denominator.</summary>
    public readonly int Den;

    internal AvRational(int num, int den) => (Num, Den) = (num, den);
}
