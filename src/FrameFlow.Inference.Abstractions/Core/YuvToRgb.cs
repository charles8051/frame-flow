// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Core;

/// <summary>
/// How a stage that reads YUV samples turns them into RGB: the levels its range sets at its bit
/// depth, and its matrix's coefficients. Pure: a matrix, a range and how the stage reads a sample
/// in, eight numbers out. Shared by every device-side stage, whatever its GPU API.
/// </summary>
/// <remarks>
/// With <c>s</c> a sample as the stage reads it, <c>Y' = (s - YOffset) · YScale</c> runs 0 to 1 and
/// <c>C' = (s - COffset) · CScale</c> runs -½ to ½. Then <c>R = Y' + RedFromCr·Cr'</c>,
/// <c>G = Y' - GreenFromCb·Cb' - GreenFromCr·Cr'</c> and <c>B = Y' + BlueFromCb·Cb'</c>.
/// </remarks>
internal readonly record struct YuvToRgb(
    double YOffset,
    double YScale,
    double COffset,
    double CScale,
    double RedFromCr,
    double GreenFromCb,
    double GreenFromCr,
    double BlueFromCb)
{
    /// <param name="matrix">The matrix.</param>
    /// <param name="range">The range, whose levels scale with <paramref name="bitDepth"/>.</param>
    /// <param name="bitDepth">The samples' bit depth, 8 to 16.</param>
    /// <param name="codeValue">
    /// The value the stage reads for one code: <c>1 / 255</c> for an 8-bit sample read as UNORM, and
    /// <c>64 / 65535</c> for a P010 sample, whose 10-bit code sits in the high bits of a 16-bit UNORM.
    /// </param>
    public static YuvToRgb Create(YuvMatrix matrix, YuvRange range, int bitDepth, double codeValue)
    {
        var (kr, kb) = matrix switch
        {
            YuvMatrix.Bt601 => (0.299, 0.114),
            YuvMatrix.Bt709 => (0.2126, 0.0722),
            _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Undefined YUV matrix."),
        };
        bool limited = range switch
        {
            YuvRange.Limited => true,
            YuvRange.Full => false,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Undefined YUV range."),
        };
        if (bitDepth is < 8 or > 16)
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "The bit depth must be 8 to 16.");
        if (!double.IsFinite(codeValue) || codeValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(codeValue), codeValue, "A code's value must be positive.");

        // Limited range is 16 to 235 (chroma 16 to 240) at 8 bits; each extra bit doubles the codes.
        double step = 1 << (bitDepth - 8);
        double max = (1 << bitDepth) - 1;
        double chromaZero = 1 << (bitDepth - 1);
        double kg = 1 - kr - kb;

        return new YuvToRgb(
            YOffset: limited ? 16 * step * codeValue : 0,
            YScale: 1 / ((limited ? 219 * step : max) * codeValue),
            COffset: chromaZero * codeValue,
            CScale: 1 / ((limited ? 224 * step : max) * codeValue),
            RedFromCr: 2 * (1 - kr),
            GreenFromCb: 2 * kb * (1 - kb) / kg,
            GreenFromCr: 2 * kr * (1 - kr) / kg,
            BlueFromCb: 2 * (1 - kb));
    }
}
