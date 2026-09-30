using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Which element type a tensor is bound as, from what the session declares (#520), and which of
/// those a reader of floats takes (#10).
/// </summary>
public sealed class DeclaredElementTypesTests
{
    private static readonly string[] Names = ["images", "boxes"];

    [Fact]
    public void ASessionThatDeclaresNoTypes_IsBoundAsFloats()
    {
        Assert.Equal(DType.Float32, DeclaredElementTypes.OrFloat32(null, Names, 1, "output"));
        Assert.Equal(DType.Float32, DeclaredElementTypes.Floating(null, Names, 1, "output", "Detector"));
    }

    [Theory]
    [InlineData(DType.Float16)]
    [InlineData(DType.Int64)]
    [InlineData(DType.BFloat16)]
    public void ADeclaredType_IsTheOneBound(DType declared) =>
        Assert.Equal(declared, DeclaredElementTypes.OrFloat32([DType.Float32, declared], Names, 1, "output"));

    [Theory]
    [InlineData(DType.Float32)]
    [InlineData(DType.Float16)]
    public void AReaderOfFloats_TakesFloatsAndHalves(DType declared) =>
        Assert.Equal(declared, DeclaredElementTypes.Floating([DType.UInt8, declared], Names, 1, "output", "Detector"));

    [Theory]
    [InlineData(DType.UInt8)]
    [InlineData(DType.BFloat16)]
    [InlineData(DType.Float64)]
    [InlineData(DType.Int32)]
    public void AReaderOfFloats_RefusesAnyOtherType_NamingTheTensorTheTypeAndTheReader(DType declared)
    {
        var error = Assert.Throws<NotSupportedException>(
            () => DeclaredElementTypes.Floating([declared, DType.Float32], Names, 0, "input", "Detector"));

        Assert.Contains("input 'images'", error.Message, StringComparison.Ordinal);
        Assert.Contains(declared.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("Detector", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A session implemented elsewhere that reports the wrong number of types is refused, not indexed past.</summary>
    [Fact]
    public void TypesThatDoNotMatchTheNames_AreRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DeclaredElementTypes.OrFloat32([DType.Float16], Names, 0, "output"));

        Assert.Contains("1 output element types for 2 outputs", error.Message, StringComparison.Ordinal);
    }
}
