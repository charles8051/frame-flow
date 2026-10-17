// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// What swscale converts a decoded frame to <c>Bgra32</c> from: the source format, whether the
/// chroma is split into planes first, the range and the scaler flags (#792). Pure. The software
/// decoder's conversion and the hardware readback both take it from <see cref="For"/>.
/// </summary>
/// <remarks>
/// <para>
/// An 8-bit hardware frame reads back as NV12, and the same stream decoded in software comes out
/// as <c>yuv420p</c> holding the same samples. swscale does not convert the two layouts alike.
/// From <c>yuv420p</c> to <c>bgra</c> at the same size it takes its unscaled YUV-to-RGB converter,
/// which gives each chroma row to the two luma rows it covers. From NV12 there is no such
/// converter, so it takes the general scaler, which filters chroma vertically and blends
/// neighbouring chroma rows on every even luma row after the first. Across a sharp vertical chroma
/// edge the two differ by most of the chroma swing: 219 levels in one channel was seen on real
/// content. Converting an NV12 frame from the planar layout instead makes swscale read the same
/// bytes the same way, so the output is identical.
/// </para>
/// <para>
/// A 10-bit frame is left as P010. swscale has no unscaled converter from <c>yuv420p10le</c>, so it
/// takes the general scaler from both layouts and their output is already identical. The test
/// that converts both layouts and compares them holds that true for a later swscale.
/// </para>
/// <para>
/// The range is taken from the frame, not from the format alone. A full-range H.264 stream decodes
/// in software as <c>yuvj420p</c>, which swscale treats as full range, and on hardware as NV12,
/// which it treats as limited range. The JPEG formats are therefore mapped to their plain
/// counterparts and the range is passed separately, so one rule covers both paths.
/// </para>
/// </remarks>
/// <param name="SourceFormat">The <c>AVPixelFormat</c> swscale is set up to read.</param>
/// <param name="SplitChroma">
/// Whether the frame is NV12, whose interleaved chroma is split into a U plane and a V plane before
/// swscale reads it.
/// </param>
/// <param name="FullRange">Whether the samples are full range (JPEG) rather than limited (MPEG).</param>
internal readonly record struct BgraConversionInput(int SourceFormat, bool SplitChroma, bool FullRange)
{
    /// <summary>The swscale flags every <c>Bgra32</c> conversion uses: <c>SWS_BILINEAR</c>.</summary>
    public const int ScaleFlags = 2;

    /// <summary>
    /// The input for a decoded frame in <paramref name="decodedFormat"/>, an <c>AVPixelFormat</c>,
    /// whose <c>color_range</c> is <paramref name="colorRange"/>, an <c>AVColorRange</c>.
    /// </summary>
    public static BgraConversionInput For(int decodedFormat, int colorRange)
    {
        bool fullRange = colorRange == (int)AVColorRange.AVCOL_RANGE_JPEG;
        return decodedFormat switch
        {
            (int)AVPixelFormat.AV_PIX_FMT_NV12 => new((int)AVPixelFormat.AV_PIX_FMT_YUV420P, true, fullRange),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P => new((int)AVPixelFormat.AV_PIX_FMT_YUV420P, false, true),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ422P => new((int)AVPixelFormat.AV_PIX_FMT_YUV422P, false, true),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ444P => new((int)AVPixelFormat.AV_PIX_FMT_YUV444P, false, true),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ440P => new((int)AVPixelFormat.AV_PIX_FMT_YUV440P, false, true),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ411P => new((int)AVPixelFormat.AV_PIX_FMT_YUV411P, false, true),
            _ => new(decodedFormat, false, fullRange),
        };
    }
}
