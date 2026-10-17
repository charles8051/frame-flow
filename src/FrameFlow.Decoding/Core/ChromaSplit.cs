// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// Splits one row of NV12's interleaved chroma into the U row and V row the software decoder
/// would have produced from the same stream (#792). Pure.
/// </summary>
internal static class ChromaSplit
{
    /// <summary>
    /// Splits <paramref name="uv"/>, NV12's <c>U0 V0 U1 V1 ...</c>, into <paramref name="u"/> and
    /// <paramref name="v"/>. Both hold at least <paramref name="samples"/> bytes and
    /// <paramref name="uv"/> twice that.
    /// </summary>
    public static void Nv12Row(ReadOnlySpan<byte> uv, Span<byte> u, Span<byte> v, int samples)
    {
        ReadOnlySpan<ushort> pairs = MemoryMarshal.Cast<byte, ushort>(uv[..(samples * 2)]);
        u = u[..samples];
        v = v[..samples];

        int i = 0;
        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // Sixteen pairs a pass. Read little-endian, each pair is a ushort with U low, V high.
            var low = Vector128.Create((ushort)0xFF);
            for (; i <= samples - 16; i += 16)
            {
                var a = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(pairs), (nuint)i);
                var b = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(pairs), (nuint)(i + 8));
                Vector128.Narrow(a & low, b & low).StoreUnsafe(ref MemoryMarshal.GetReference(u), (nuint)i);
                Vector128
                    .Narrow(Vector128.ShiftRightLogical(a, 8), Vector128.ShiftRightLogical(b, 8))
                    .StoreUnsafe(ref MemoryMarshal.GetReference(v), (nuint)i);
            }
        }

        for (; i < samples; i++)
        {
            u[i] = uv[2 * i];
            v[i] = uv[2 * i + 1];
        }
    }
}
