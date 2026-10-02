// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Source-generated P/Invoke declarations for <c>libavutil</c>.
/// </summary>
/// <remarks>
/// Phase 01 surface: version query only.
/// Additional declarations will be added in later phases as the decode
/// and pixel-conversion surface is built out (ADR-0011).
/// </remarks>
internal static partial class FFAvUtil
{
    /// <summary>
    /// Returns the packed version integer for the loaded <c>libavutil</c> build.
    /// Use <see cref="AvVersionMajor"/>, <see cref="AvVersionMinor"/>, and
    /// <see cref="AvVersionMicro"/> to unpack the individual components.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint avutil_version();

    /// <summary>
    /// The counterclockwise rotation, in degrees, that a 3x3 display matrix applies, or NaN for a
    /// degenerate one. FFmpeg's own reading of <c>AV_PKT_DATA_DISPLAYMATRIX</c> (#542).
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial double av_display_rotation_get(int* matrix);

    /// <summary>Extracts the major component from a packed FFmpeg version integer.</summary>
    internal static int AvVersionMajor(uint version) => (int)(version >> 16);

    /// <summary>Extracts the minor component from a packed FFmpeg version integer.</summary>
    internal static int AvVersionMinor(uint version) => (int)((version >> 8) & 0xFF);

    /// <summary>Extracts the micro component from a packed FFmpeg version integer.</summary>
    internal static int AvVersionMicro(uint version) => (int)(version & 0xFF);
}
