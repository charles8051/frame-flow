// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Hardware-acceleration additions to the <c>libavcodec</c> P/Invoke surface:
/// per-codec hardware capability enumeration (ADR-0033).
/// </summary>
/// <remarks>
/// Partial class extension of <see cref="FFAvCodec"/>. Targets FFmpeg 7.x
/// (libavcodec-61).
/// </remarks>
internal static partial class FFAvCodec
{
    /// <summary>
    /// Retrieves the hardware configuration descriptor at <paramref name="index"/>
    /// for the given <paramref name="codec"/>. Returns <see cref="nint.Zero"/> when
    /// no further entries exist (sentinel for end-of-iteration).
    /// </summary>
    /// <remarks>
    /// The returned pointer is owned by FFmpeg and must not be freed. Treat it as
    /// a view onto the static codec table; it remains valid for the lifetime of
    /// the loaded libavcodec.
    /// </remarks>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint avcodec_get_hw_config(nint codec, int index);
}

/// <summary>
/// Read-only view onto a native <c>AVCodecHWConfig</c> struct.
/// </summary>
/// <remarks>
/// The struct layout in FFmpeg 7.x (libavcodec/codec.h) is:
/// <code>
/// typedef struct AVCodecHWConfig {
///     enum AVPixelFormat pix_fmt;     // offset 0,  4 bytes
///     int methods;                    // offset 4,  4 bytes (AV_CODEC_HW_CONFIG_METHOD_*)
///     enum AVHWDeviceType device_type;// offset 8,  4 bytes
/// } AVCodecHWConfig;
/// </code>
/// This layout has been stable since the API was introduced in FFmpeg 3.4.
/// </remarks>
internal readonly unsafe ref struct AvCodecHwConfigAccessor
{
    private readonly byte* _ptr;

    internal AvCodecHwConfigAccessor(nint configPtr)
    {
        _ptr = (byte*)configPtr;
    }

    /// <summary>The hardware pixel format produced by this configuration
    /// (<c>AVPixelFormat</c>, e.g. <c>AV_PIX_FMT_CUDA</c>, <c>AV_PIX_FMT_VAAPI</c>).</summary>
    internal int PixelFormat => Unsafe.ReadUnaligned<int>(_ptr);

    /// <summary>Bitfield of <c>AV_CODEC_HW_CONFIG_METHOD_*</c> flags advertised by
    /// this configuration. FrameFlow v1 uses configurations that advertise
    /// <see cref="FFAvUtil.AvCodecHwConfigMethodHwDeviceCtx"/>.</summary>
    internal int Methods => Unsafe.ReadUnaligned<int>(_ptr + 4);

    /// <summary>The <c>AVHWDeviceType</c> integer associated with this configuration.</summary>
    internal int DeviceType => Unsafe.ReadUnaligned<int>(_ptr + 8);
}
