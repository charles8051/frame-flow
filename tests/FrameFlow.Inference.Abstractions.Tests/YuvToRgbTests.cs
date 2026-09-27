using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// The YUV to RGB conversion every device-side stage shares: the matrices' coefficients and the
/// range's levels at 8 and 10 bits, on samples as a stage reads them. Pure.
/// </summary>
public sealed class YuvToRgbTests
{
    private const double Nv12Code = 1.0 / 255;
    private const double P010Code = 64.0 / 65535;

    [Theory]
    [InlineData(YuvMatrix.Bt601, 1.402, 0.344136, 0.714136, 1.772)]
    [InlineData(YuvMatrix.Bt709, 1.5748, 0.187324, 0.468124, 1.8556)]
    public void TheMatrix_HasItsStandardsCoefficients(
        YuvMatrix matrix, double redFromCr, double greenFromCb, double greenFromCr, double blueFromCb)
    {
        var c = YuvToRgb.Create(matrix, YuvRange.Limited, 8, Nv12Code);

        Assert.Equal(redFromCr, c.RedFromCr, 1e-5);
        Assert.Equal(greenFromCb, c.GreenFromCb, 1e-5);
        Assert.Equal(greenFromCr, c.GreenFromCr, 1e-5);
        Assert.Equal(blueFromCb, c.BlueFromCb, 1e-5);
    }

    [Fact]
    public void LimitedRange_StretchesLumaFrom16To235AndChromaFrom16To240()
    {
        var c = YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Limited, 8, Nv12Code);

        Assert.Equal(0, Luma(c, 16 * Nv12Code), 1e-9);
        Assert.Equal(1, Luma(c, 235 * Nv12Code), 1e-9);
        Assert.Equal(-0.5, Chroma(c, 16 * Nv12Code), 1e-9);
        Assert.Equal(0.5, Chroma(c, 240 * Nv12Code), 1e-9);
    }

    [Fact]
    public void FullRange_TakesSamplesAsTheyAre()
    {
        var c = YuvToRgb.Create(YuvMatrix.Bt709, YuvRange.Full, 8, Nv12Code);

        Assert.Equal(0, c.YOffset);
        Assert.Equal(1, c.YScale, 1e-12);
        Assert.Equal(128 * Nv12Code, c.COffset, 1e-12);
        Assert.Equal(1, c.CScale, 1e-12);
    }

    [Fact]
    public void TenBitLimitedRange_StretchesLumaFrom64To940AndChromaFrom64To960()
    {
        var c = YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Limited, 10, P010Code);

        Assert.Equal(0, Luma(c, 64 * P010Code), 1e-9);
        Assert.Equal(1, Luma(c, 940 * P010Code), 1e-9);
        Assert.Equal(-0.5, Chroma(c, 64 * P010Code), 1e-9);
        Assert.Equal(0.5, Chroma(c, 960 * P010Code), 1e-9);
    }

    [Fact]
    public void TenBitFullRange_SpansTheTenBitCodes()
    {
        var c = YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Full, 10, P010Code);

        Assert.Equal(0, Luma(c, 0), 1e-9);
        Assert.Equal(1, Luma(c, 1023 * P010Code), 1e-9);
        Assert.Equal(0, Chroma(c, 512 * P010Code), 1e-9);
    }

    [Fact]
    public void Create_RefusesWhatItCannotConvert()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvToRgb.Create((YuvMatrix)7, YuvRange.Limited, 8, Nv12Code));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvToRgb.Create(YuvMatrix.Bt601, (YuvRange)7, 8, Nv12Code));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Limited, 7, Nv12Code));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Limited, 17, Nv12Code));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Limited, 8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvToRgb.Create(YuvMatrix.Bt601, YuvRange.Limited, 8, double.NaN));
    }

    private static double Luma(YuvToRgb c, double sample) => (sample - c.YOffset) * c.YScale;

    private static double Chroma(YuvToRgb c, double sample) => (sample - c.COffset) * c.CScale;
}
