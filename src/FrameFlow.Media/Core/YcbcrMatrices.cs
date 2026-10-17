// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>A YCbCr-to-RGB matrix a frame converts with.</summary>
internal enum YcbcrMatrix
{
    /// <summary>ITU-R BT.601 (and SMPTE 170M, BT.470 BG).</summary>
    Bt601,

    /// <summary>ITU-R BT.709.</summary>
    Bt709,

    /// <summary>ITU-R BT.2020 non-constant luminance.</summary>
    Bt2020,

    /// <summary>The FCC's 1953 NTSC matrix.</summary>
    Fcc,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M,
}

/// <summary>
/// Which matrix a decoded frame converts with (#841). Pure. The decoder's <c>Bgra32</c>
/// conversion, a hardware frame's readback and the D3D11 and D3D12 presenters all take it from
/// here, so the CPU and GPU paths agree on every frame.
/// </summary>
/// <remarks>
/// <para>
/// A frame that signals a matrix converts with it. A frame that signals none converts by its
/// height: BT.709 at <see cref="HdRows"/> rows or more, BT.601 below. SD video is mastered in
/// BT.601 and HD video in BT.709, and an untagged stream is usually one or the other by its size,
/// so players and FFmpeg's own tools guess the same way. The height is the frame's picture height,
/// so a 1080-row picture coded as 1088 rows is HD and a 576-row picture is SD.
/// </para>
/// <para>
/// A caller's <see cref="ColorMatrix"/> other than <see cref="ColorMatrix.Auto"/> replaces both the
/// signalled matrix and the size rule.
/// </para>
/// </remarks>
internal static class YcbcrMatrices
{
    /// <summary>The fewest rows an unsignalled frame converts with BT.709 at.</summary>
    public const int HdRows = 720;

    /// <summary>The matrix for a frame <paramref name="height"/> rows tall that signals none.</summary>
    public static YcbcrMatrix Unsignalled(int height) =>
        height >= HdRows ? YcbcrMatrix.Bt709 : YcbcrMatrix.Bt601;

    /// <summary>
    /// The matrix a frame <paramref name="height"/> rows tall converts with, when it signals
    /// <paramref name="signalled"/> (<see langword="null"/> for none) and the caller chose
    /// <paramref name="choice"/>.
    /// </summary>
    public static YcbcrMatrix Resolve(ColorMatrix choice, YcbcrMatrix? signalled, int height) =>
        choice switch
        {
            ColorMatrix.Bt601 => YcbcrMatrix.Bt601,
            ColorMatrix.Bt709 => YcbcrMatrix.Bt709,
            ColorMatrix.Bt2020 => YcbcrMatrix.Bt2020,
            _ => signalled ?? Unsignalled(height),
        };

    /// <summary>
    /// The luma weights of red and blue, <c>Kr</c> and <c>Kb</c>, that define
    /// <paramref name="matrix"/>. Green's is <c>1 - Kr - Kb</c>.
    /// </summary>
    public static (double Kr, double Kb) Weights(YcbcrMatrix matrix) =>
        matrix switch
        {
            YcbcrMatrix.Bt601 => (0.299, 0.114),
            YcbcrMatrix.Bt709 => (0.2126, 0.0722),
            YcbcrMatrix.Bt2020 => (0.2627, 0.0593),
            YcbcrMatrix.Fcc => (0.30, 0.11),
            YcbcrMatrix.Smpte240M => (0.212, 0.087),
            _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Undefined matrix."),
        };
}
