using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Media.Core;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The coefficients the presenters' shaders convert luma and chroma to RGB with (#841), from the
/// matrix the CPU path converts the same frame with. Pure.
/// </summary>
/// <remarks>The matrix is internal, so the theories name it.</remarks>
public sealed class YcbcrCoefficientsTests
{
    /// <summary>
    /// The inverse matrices, as the standards' luma weights give them to four places. BT.709's are
    /// the literals the shaders held before #841.
    /// </summary>
    [Theory]
    [InlineData(nameof(YcbcrMatrix.Bt709), 1.5748, 0.1873, 0.4681, 1.8556)]
    [InlineData(nameof(YcbcrMatrix.Bt601), 1.4020, 0.3441, 0.7141, 1.7720)]
    [InlineData(nameof(YcbcrMatrix.Bt2020), 1.4746, 0.1646, 0.5714, 1.8814)]
    [InlineData(nameof(YcbcrMatrix.Fcc), 1.4000, 0.3319, 0.7119, 1.7800)]
    [InlineData(nameof(YcbcrMatrix.Smpte240M), 1.5760, 0.2266, 0.4766, 1.8260)]
    public void EachMatrixHasItsInverse(string matrixName, double rv, double gu, double gv, double bu)
    {
        var c = YcbcrCoefficients.For(Enum.Parse<YcbcrMatrix>(matrixName));

        Assert.Equal(rv, c.RV, 4);
        Assert.Equal(gu, c.GU, 4);
        Assert.Equal(gv, c.GV, 4);
        Assert.Equal(bu, c.BU, 4);
    }

    /// <summary>
    /// The coefficients invert the matrix's luma: chroma alone adds no luma, so the RGB it makes
    /// weighs to zero with the matrix's own weights.
    /// </summary>
    [Fact]
    public void ChromaAloneAddsNoLuma()
    {
        foreach (var matrix in Enum.GetValues<YcbcrMatrix>())
        {
            var (kr, kb) = YcbcrMatrices.Weights(matrix);
            var c = YcbcrCoefficients.For(matrix);

            double u = 0.25, v = -0.125;
            double r = c.RV * v, g = -c.GU * u - c.GV * v, b = c.BU * u;
            Assert.Equal(0, kr * r + (1 - kr - kb) * g + kb * b, 6);
        }
    }

    [Theory]
    [InlineData(nameof(YcbcrMatrix.Bt601), ColorSpaceType.YcbcrStudioG22LeftP601)]
    [InlineData(nameof(YcbcrMatrix.Fcc), ColorSpaceType.YcbcrStudioG22LeftP601)]
    [InlineData(nameof(YcbcrMatrix.Bt709), ColorSpaceType.YcbcrStudioG22LeftP709)]
    [InlineData(nameof(YcbcrMatrix.Smpte240M), ColorSpaceType.YcbcrStudioG22LeftP709)]
    [InlineData(nameof(YcbcrMatrix.Bt2020), ColorSpaceType.YcbcrStudioG22LeftP2020)]
    public void TheVideoProcessorTakesTheMatrixsColourSpace(string matrixName, ColorSpaceType expected)
    {
        Assert.Equal(expected, D3D11Nv12SharedConverter.ProcessorColorSpace(Enum.Parse<YcbcrMatrix>(matrixName)));
    }
}
