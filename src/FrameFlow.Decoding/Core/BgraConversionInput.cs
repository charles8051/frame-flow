// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;
using FrameFlow.Media.Core;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// What swscale converts a decoded frame to <c>Bgra32</c> from: the source format, whether the
/// chroma is split into planes first, the range, the matrix and the scaler flags (#792, #796). Pure. The software
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
/// <para>
/// The matrix is also taken from the frame (#796). A stream that signals BT.709 converts with
/// BT.709, one that signals BT.601 with BT.601, and so on for every matrix swscale has
/// coefficients for. BT.2020 constant luminance is approximated with the BT.2020 non-constant
/// luminance coefficients. A stream that signals none, or one swscale cannot convert, converts by
/// its height (#841): BT.709 at 720 rows or more, BT.601 below. The rule is
/// <see cref="YcbcrMatrices"/>, which the D3D11 and D3D12 presenters follow too, so a frame looks
/// the same on the CPU path and the GPU path. A caller's <see cref="ColorMatrix"/> replaces both.
/// </para>
/// </remarks>
/// <param name="SourceFormat">The <c>AVPixelFormat</c> swscale is set up to read.</param>
/// <param name="SplitChroma">
/// Whether the frame is NV12, whose interleaved chroma is split into a U plane and a V plane before
/// swscale reads it.
/// </param>
/// <param name="FullRange">Whether the samples are full range (JPEG) rather than limited (MPEG).</param>
/// <param name="Matrix">
/// The <c>SWS_CS_*</c> value whose coefficients swscale converts the samples to RGB with.
/// </param>
internal readonly record struct BgraConversionInput(int SourceFormat, bool SplitChroma, bool FullRange, int Matrix)
{
    /// <summary><c>SWS_CS_ITU709</c>: ITU-R BT.709.</summary>
    public const int Bt709 = 1;

    /// <summary><c>SWS_CS_FCC</c>: the FCC's 1953 NTSC matrix.</summary>
    public const int Fcc = 4;

    /// <summary><c>SWS_CS_ITU601</c>: ITU-R BT.601, which swscale also calls its default.</summary>
    public const int Bt601 = 5;

    /// <summary><c>SWS_CS_SMPTE240M</c>: SMPTE 240M.</summary>
    public const int Smpte240M = 7;

    /// <summary><c>SWS_CS_BT2020</c>: ITU-R BT.2020.</summary>
    public const int Bt2020 = 9;

    /// <summary>The swscale flags every <c>Bgra32</c> conversion uses: <c>SWS_BILINEAR</c>.</summary>
    public const int ScaleFlags = 2;

    /// <summary>
    /// The input for a decoded frame in <paramref name="decodedFormat"/>, an <c>AVPixelFormat</c>,
    /// whose <c>color_range</c> is <paramref name="colorRange"/>, an <c>AVColorRange</c>, and which
    /// converts with <paramref name="ycbcr"/> (from <see cref="MatrixFor"/>).
    /// </summary>
    public static BgraConversionInput For(int decodedFormat, int colorRange, YcbcrMatrix ycbcr)
    {
        bool fullRange = colorRange == (int)AVColorRange.AVCOL_RANGE_JPEG;
        int matrix = SwsMatrix(ycbcr);
        return decodedFormat switch
        {
            (int)AVPixelFormat.AV_PIX_FMT_NV12 => new((int)AVPixelFormat.AV_PIX_FMT_YUV420P, true, fullRange, matrix),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P => new((int)AVPixelFormat.AV_PIX_FMT_YUV420P, false, true, matrix),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ422P => new((int)AVPixelFormat.AV_PIX_FMT_YUV422P, false, true, matrix),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ444P => new((int)AVPixelFormat.AV_PIX_FMT_YUV444P, false, true, matrix),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ440P => new((int)AVPixelFormat.AV_PIX_FMT_YUV440P, false, true, matrix),
            (int)AVPixelFormat.AV_PIX_FMT_YUVJ411P => new((int)AVPixelFormat.AV_PIX_FMT_YUV411P, false, true, matrix),
            _ => new(decodedFormat, false, fullRange, matrix),
        };
    }

    /// <summary>
    /// The matrix a frame whose <c>colorspace</c> is <paramref name="colorSpace"/>, an
    /// <c>AVColorSpace</c>, and whose picture is <paramref name="height"/> rows tall converts with
    /// when the caller chose <paramref name="choice"/>. An unsignalled frame takes
    /// <see cref="YcbcrMatrices.Unsignalled"/>.
    /// </summary>
    public static YcbcrMatrix MatrixFor(int colorSpace, int height, ColorMatrix choice = ColorMatrix.Auto) =>
        YcbcrMatrices.Resolve(choice, Signalled(colorSpace), height);

    /// <summary>
    /// The matrix a frame whose <c>colorspace</c> is <paramref name="colorSpace"/> signals, or
    /// <see langword="null"/> when it signals none swscale can convert with. Every
    /// <c>AVColorSpace</c> is named; a value outside the enum signals none.
    /// </summary>
    public static YcbcrMatrix? Signalled(int colorSpace) =>
        (AVColorSpace)colorSpace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => YcbcrMatrix.Bt709,
            AVColorSpace.AVCOL_SPC_BT470BG => YcbcrMatrix.Bt601,
            AVColorSpace.AVCOL_SPC_SMPTE170M => YcbcrMatrix.Bt601,
            AVColorSpace.AVCOL_SPC_FCC => YcbcrMatrix.Fcc,
            AVColorSpace.AVCOL_SPC_SMPTE240M => YcbcrMatrix.Smpte240M,
            AVColorSpace.AVCOL_SPC_BT2020_NCL => YcbcrMatrix.Bt2020,
            // An approximation. Constant luminance is not a matrix: its conversion is piecewise and
            // works on linear light, which a choice of coefficients cannot reproduce. The NCL
            // coefficients share its primaries and luma weights, so they come closer than the
            // size rule, and swscale's own table gives CL the same ones.
            AVColorSpace.AVCOL_SPC_BT2020_CL => YcbcrMatrix.Bt2020,

            // Not signalled.
            AVColorSpace.AVCOL_SPC_UNSPECIFIED => null,
            AVColorSpace.AVCOL_SPC_RESERVED => null,

            // RGB (and an RGB pixel format) does not go through the matrix at all.
            AVColorSpace.AVCOL_SPC_RGB => null,

            // Signalled, but swscale has no coefficients for them.
            AVColorSpace.AVCOL_SPC_YCGCO => null,
            AVColorSpace.AVCOL_SPC_YCGCO_RE => null,
            AVColorSpace.AVCOL_SPC_YCGCO_RO => null,
            AVColorSpace.AVCOL_SPC_SMPTE2085 => null,
            AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_NCL => null,
            AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_CL => null,
            AVColorSpace.AVCOL_SPC_ICTCP => null,
            AVColorSpace.AVCOL_SPC_IPT_C2 => null,
            AVColorSpace.AVCOL_SPC_NB => null,

            _ => null,
        };

    /// <summary>The <c>SWS_CS_*</c> value whose coefficients are <paramref name="matrix"/>.</summary>
    public static int SwsMatrix(YcbcrMatrix matrix) =>
        matrix switch
        {
            YcbcrMatrix.Bt601 => Bt601,
            YcbcrMatrix.Bt709 => Bt709,
            YcbcrMatrix.Bt2020 => Bt2020,
            YcbcrMatrix.Fcc => Fcc,
            YcbcrMatrix.Smpte240M => Smpte240M,
            _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Undefined matrix."),
        };
}
