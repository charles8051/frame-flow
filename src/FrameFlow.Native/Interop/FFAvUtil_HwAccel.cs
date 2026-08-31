// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Hardware-acceleration additions to the <c>libavutil</c> P/Invoke surface:
/// AVHWDeviceType enumeration, hardware device context lifecycle, and
/// hardware frame transfer (ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// Partial class extension of <see cref="FFAvUtil"/>. Split out so the hwaccel
/// surface is auditable separately from the Phase 01 / Phase 03 declarations.
/// Targets FFmpeg 7.x (libavutil-59).
/// </para>
/// <para>
/// The <c>AVHWDeviceType</c> integer values mirror FFmpeg's
/// <c>enum AVHWDeviceType</c>. These have been stable across FFmpeg 4.x–7.x;
/// new values are appended, never reordered.
/// </para>
/// </remarks>
internal static partial class FFAvUtil
{
    // -------------------------------------------------------------------------
    // AVHWDeviceType enumeration values (FFmpeg 7.x stable)
    // -------------------------------------------------------------------------

    internal const int AvHwDeviceTypeNone = 0;
    internal const int AvHwDeviceTypeVdpau = 1;
    internal const int AvHwDeviceTypeCuda = 2;
    internal const int AvHwDeviceTypeVaApi = 3;
    internal const int AvHwDeviceTypeDxva2 = 4;
    internal const int AvHwDeviceTypeQsv = 5;
    internal const int AvHwDeviceTypeVideoToolbox = 6;
    internal const int AvHwDeviceTypeD3D11Va = 7;
    internal const int AvHwDeviceTypeDrm = 8;
    internal const int AvHwDeviceTypeOpenCl = 9;
    internal const int AvHwDeviceTypeMediaCodec = 10;
    internal const int AvHwDeviceTypeVulkan = 11;
    internal const int AvHwDeviceTypeD3D12Va = 12;

    // -------------------------------------------------------------------------
    // Hardware device enumeration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Iterates over hardware device types compiled into this FFmpeg build.
    /// Pass <see cref="AvHwDeviceTypeNone"/> on the first call; pass the previous
    /// return value on subsequent calls. Returns <see cref="AvHwDeviceTypeNone"/>
    /// when iteration is exhausted.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_hwdevice_iterate_types(int prev);

    /// <summary>
    /// Returns the human-readable name for an <c>AVHWDeviceType</c> value
    /// (e.g. <c>"cuda"</c>, <c>"vaapi"</c>, <c>"d3d11va"</c>) as a pointer to a
    /// statically-allocated UTF-8 string owned by FFmpeg. Returns
    /// <see cref="nint.Zero"/> if the type is unknown.
    /// </summary>
    /// <remarks>
    /// As with <see cref="FFAvCodec.avcodec_get_name"/>, we must not use
    /// <c>UnmanagedType.LPUTF8Str</c> here — that marshaling attribute causes the
    /// runtime to call <c>CoTaskMemFree</c> on the returned pointer, which would
    /// corrupt the FFmpeg heap. Callers should use
    /// <see cref="System.Runtime.InteropServices.Marshal.PtrToStringUTF8(nint)"/>.
    /// </remarks>
    [LibraryImport("avutil", EntryPoint = "av_hwdevice_get_type_name")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_hwdevice_get_type_name_native(int type);

    /// <summary>
    /// Managed wrapper around <see cref="av_hwdevice_get_type_name_native"/> that
    /// returns a <see cref="string"/> via
    /// <see cref="System.Runtime.InteropServices.Marshal.PtrToStringUTF8(nint)"/>.
    /// Returns <c>"unknown"</c> if the type is not recognised.
    /// </summary>
    internal static string AvHwDeviceGetTypeName(int type)
    {
        nint ptr = av_hwdevice_get_type_name_native(type);
        return ptr == nint.Zero ? "unknown" : Marshal.PtrToStringUTF8(ptr) ?? "unknown";
    }

    // -------------------------------------------------------------------------
    // Hardware device context lifecycle
    //
    // av_hwdevice_ctx_create allocates an AVHWDeviceContext wrapped in an
    // AVBufferRef. Ownership returns to the caller via the out parameter.
    // Release with av_buffer_unref.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an <c>AVHWDeviceContext</c> of the given type and writes a
    /// <c>AVBufferRef*</c> to <paramref name="deviceCtxRef"/>.
    /// </summary>
    /// <param name="deviceCtxRef">
    /// Out: receives a new <c>AVBufferRef*</c> wrapping the device context.
    /// Must be released with <see cref="av_buffer_unref"/>.
    /// </param>
    /// <param name="type">An <c>AVHWDeviceType</c> integer value.</param>
    /// <param name="device">
    /// Optional device specifier (e.g. <c>"/dev/dri/renderD128"</c> for VAAPI).
    /// Pass <see langword="null"/> to use the default device for the type.
    /// </param>
    /// <param name="opts">Optional <c>AVDictionary*</c>; pass <see cref="nint.Zero"/>.</param>
    /// <param name="flags">Reserved; pass 0.</param>
    /// <returns>0 on success; negative AVERROR on failure.</returns>
    [LibraryImport("avutil", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_hwdevice_ctx_create(
        out nint deviceCtxRef,
        int type,
        string? device,
        nint opts,
        int flags
    );

    /// <summary>
    /// Increments the reference count of <paramref name="buf"/> and returns a new
    /// <c>AVBufferRef*</c> aliasing the same buffer. Returns <see cref="nint.Zero"/>
    /// on OOM.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_buffer_ref(nint buf);

    /// <summary>
    /// Decrements the buffer's reference count and frees the buffer if it reaches
    /// zero. Sets the pointer to <see langword="null"/>. Safe to call on a
    /// zero-initialised pointer.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void av_buffer_unref(ref nint buf);

    // -------------------------------------------------------------------------
    // Hardware frame transfer (GPU → CPU readback)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Copies frame data from a hardware <paramref name="src"/> frame (e.g. one
    /// with <c>AV_PIX_FMT_CUDA</c>) into a software <paramref name="dst"/> frame
    /// that already has its <c>format</c> set to the desired CPU pixel format.
    /// Allocates the destination buffers if they aren't already allocated.
    /// </summary>
    /// <param name="dst">Pre-allocated <c>AVFrame*</c> for the CPU destination.</param>
    /// <param name="src">Source <c>AVFrame*</c> with hardware format and a populated
    /// <c>hw_frames_ctx</c>.</param>
    /// <param name="flags">Reserved; pass 0.</param>
    /// <returns>0 on success; negative AVERROR on failure.</returns>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_hwframe_transfer_data(nint dst, nint src, int flags);

    // -------------------------------------------------------------------------
    // AV_CODEC_HW_CONFIG_METHOD_* flag constants (declared in libavcodec but
    // semantically describe hwaccel capability — co-located here for cohesion).
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX</c> — the codec accepts a
    /// hardware device context via <c>AVCodecContext.hw_device_ctx</c>. This is
    /// the path FrameFlow uses for v1 of hwaccel (ADR-0033).
    /// </summary>
    internal const int AvCodecHwConfigMethodHwDeviceCtx = 0x01;

    /// <summary>
    /// <c>AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX</c> — the codec accepts a
    /// pre-allocated hardware frames context. Not used by FrameFlow v1.
    /// </summary>
    internal const int AvCodecHwConfigMethodHwFramesCtx = 0x02;

    /// <summary>
    /// <c>AV_CODEC_HW_CONFIG_METHOD_INTERNAL</c> — the codec handles the device
    /// internally; no caller setup needed. Not used by FrameFlow v1.
    /// </summary>
    internal const int AvCodecHwConfigMethodInternal = 0x04;

    /// <summary>
    /// <c>AV_CODEC_HW_CONFIG_METHOD_AD_HOC</c> — the codec uses an ad-hoc
    /// configuration mechanism. Not used by FrameFlow v1.
    /// </summary>
    internal const int AvCodecHwConfigMethodAdHoc = 0x08;
}
