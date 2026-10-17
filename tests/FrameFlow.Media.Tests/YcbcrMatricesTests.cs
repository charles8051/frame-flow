using FrameFlow.Media.Core;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Which matrix a decoded frame converts with (#841): the signalled one, else BT.709 at 720 rows or
/// more and BT.601 below, unless the caller fixed one. Pure.
/// </summary>
/// <remarks>The matrix is internal, so the theories name it.</remarks>
public sealed class YcbcrMatricesTests
{
    [Theory]
    [InlineData(1, nameof(YcbcrMatrix.Bt601))]
    [InlineData(240, nameof(YcbcrMatrix.Bt601))]
    [InlineData(480, nameof(YcbcrMatrix.Bt601))]
    [InlineData(576, nameof(YcbcrMatrix.Bt601))]
    [InlineData(718, nameof(YcbcrMatrix.Bt601))]
    [InlineData(719, nameof(YcbcrMatrix.Bt601))]
    [InlineData(720, nameof(YcbcrMatrix.Bt709))]
    [InlineData(721, nameof(YcbcrMatrix.Bt709))]
    [InlineData(1080, nameof(YcbcrMatrix.Bt709))]
    [InlineData(1088, nameof(YcbcrMatrix.Bt709))]
    [InlineData(2160, nameof(YcbcrMatrix.Bt709))]
    public void AnUnsignalledFrameConvertsByItsHeight(int height, string expectedName)
    {
        var expected = Enum.Parse<YcbcrMatrix>(expectedName);
        Assert.Equal(expected, YcbcrMatrices.Unsignalled(height));
        Assert.Equal(expected, YcbcrMatrices.Resolve(ColorMatrix.Auto, signalled: null, height));
    }

    [Theory]
    [InlineData(nameof(YcbcrMatrix.Bt601), 1080)]
    [InlineData(nameof(YcbcrMatrix.Bt709), 480)]
    [InlineData(nameof(YcbcrMatrix.Bt2020), 480)]
    [InlineData(nameof(YcbcrMatrix.Fcc), 1080)]
    [InlineData(nameof(YcbcrMatrix.Smpte240M), 480)]
    public void ASignalledMatrixWinsOverTheSizeRule(string signalledName, int height)
    {
        var signalled = Enum.Parse<YcbcrMatrix>(signalledName);
        Assert.Equal(signalled, YcbcrMatrices.Resolve(ColorMatrix.Auto, signalled, height));
    }

    /// <summary>A fixed choice replaces the signalled matrix and the size rule, at either size.</summary>
    [Theory]
    [InlineData(ColorMatrix.Bt601, nameof(YcbcrMatrix.Bt601))]
    [InlineData(ColorMatrix.Bt709, nameof(YcbcrMatrix.Bt709))]
    [InlineData(ColorMatrix.Bt2020, nameof(YcbcrMatrix.Bt2020))]
    public void AFixedChoiceReplacesTheTagAndTheSizeRule(ColorMatrix choice, string expectedName)
    {
        var expected = Enum.Parse<YcbcrMatrix>(expectedName);
        foreach (int height in new[] { 480, 1080 })
        {
            Assert.Equal(expected, YcbcrMatrices.Resolve(choice, signalled: null, height));
            foreach (var signalled in Enum.GetValues<YcbcrMatrix>())
                Assert.Equal(expected, YcbcrMatrices.Resolve(choice, signalled, height));
        }
    }

    /// <summary>
    /// The luma weights, as the standards give them. Written as literals so a wrong table cannot
    /// agree with itself.
    /// </summary>
    [Theory]
    [InlineData(nameof(YcbcrMatrix.Bt601), 0.299, 0.114)]
    [InlineData(nameof(YcbcrMatrix.Bt709), 0.2126, 0.0722)]
    [InlineData(nameof(YcbcrMatrix.Bt2020), 0.2627, 0.0593)]
    [InlineData(nameof(YcbcrMatrix.Fcc), 0.30, 0.11)]
    [InlineData(nameof(YcbcrMatrix.Smpte240M), 0.212, 0.087)]
    public void EachMatrixHasItsLumaWeights(string matrixName, double kr, double kb)
    {
        var matrix = Enum.Parse<YcbcrMatrix>(matrixName);
        Assert.Equal((kr, kb), YcbcrMatrices.Weights(matrix));
    }

    [Fact]
    public void EveryMatrixHasWeights()
    {
        foreach (var matrix in Enum.GetValues<YcbcrMatrix>())
            _ = YcbcrMatrices.Weights(matrix);
    }
}
