using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// What a YCbCr sample converts to in 8-bit RGB, computed from the matrix's definition, for the
/// tests that check which matrix a conversion used (#796).
/// </summary>
internal static class YuvReference
{
    /// <summary>A matrix's luma coefficients for red and blue.</summary>
    internal readonly record struct Matrix(double Kr, double Kb);

    public static readonly Matrix Bt601 = new(0.299, 0.114);
    public static readonly Matrix Bt709 = new(0.2126, 0.0722);
    public static readonly Matrix Bt2020 = new(0.2627, 0.0593);

    /// <summary>
    /// The 75% bars in BT.709 limited range: white, yellow, cyan, green, magenta, red, blue,
    /// black. The corpus fixture <c>test-video-ffv1-bt709-bars.mkv</c> holds the same samples.
    /// </summary>
    public static readonly (int Y, int Cb, int Cr)[] Bars =
    [
        (180, 128, 128),
        (168, 44, 136),
        (145, 147, 44),
        (133, 63, 52),
        (63, 193, 204),
        (51, 109, 212),
        (28, 212, 120),
        (16, 128, 128),
    ];

    /// <summary>
    /// The matrix a frame <paramref name="height"/> rows tall and tagged <paramref name="colorSpace"/>
    /// should convert with. An untagged frame is BT.601 below 720 rows and BT.709 from 720 (#841).
    /// </summary>
    public static Matrix For(AVColorSpace colorSpace, int height) =>
        colorSpace switch
        {
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => Bt601,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => Bt2020,
            AVColorSpace.AVCOL_SPC_BT709 => Bt709,
            AVColorSpace.AVCOL_SPC_UNSPECIFIED => height >= 720 ? Bt709 : Bt601,
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace), colorSpace, null),
        };

    /// <summary>Blue, green and red, in that order, for one 8-bit sample.</summary>
    public static byte[] Bgr(int y, int cb, int cr, Matrix m, bool fullRange)
    {
        double luma = fullRange ? y / 255.0 : (y - 16) / 219.0;
        double pb = fullRange ? (cb - 128) / 255.0 : (cb - 128) / 224.0;
        double pr = fullRange ? (cr - 128) / 255.0 : (cr - 128) / 224.0;
        double r = luma + 2 * (1 - m.Kr) * pr;
        double b = luma + 2 * (1 - m.Kb) * pb;
        double g = (luma - m.Kr * r - m.Kb * b) / (1 - m.Kr - m.Kb);
        return [Level(b), Level(g), Level(r)];
    }

    /// <summary>The largest channel difference between two matrices over <see cref="Bars"/>.</summary>
    public static int WorstDifference(Matrix a, Matrix b, bool fullRange)
    {
        int worst = 0;
        foreach (var (y, cb, cr) in Bars)
        {
            var x = Bgr(y, cb, cr, a, fullRange);
            var z = Bgr(y, cb, cr, b, fullRange);
            for (int c = 0; c < 3; c++)
                worst = Math.Max(worst, Math.Abs(x[c] - z[c]));
        }

        return worst;
    }

    /// <summary>Each of blue, green and red within <paramref name="tolerance"/> levels.</summary>
    public static void AssertNear(byte[] expected, ReadOnlySpan<byte> actual, string what, int tolerance = 1)
    {
        for (int c = 0; c < 3; c++)
        {
            Assert.True(
                Math.Abs(expected[c] - actual[c]) <= tolerance,
                $"{what}: BGR {actual[0]},{actual[1]},{actual[2]}, expected {expected[0]},{expected[1]},{expected[2]} within {tolerance}."
            );
        }
    }

    private static byte Level(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
