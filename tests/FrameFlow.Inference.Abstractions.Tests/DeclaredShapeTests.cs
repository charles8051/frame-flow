using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// The shape rented for one output: the declared shape, with each dynamic dimension sized by its
/// name (#485). Pure.
/// </summary>
public sealed class DeclaredShapeTests
{
    private static readonly Dictionary<string, int> Sizes = new() { ["batch"] = 4, ["n"] = 7 };

    public static TheoryData<long[], string[], int[]> Resolved => new()
    {
        // Every dimension fixed: the sizes are not consulted.
        { [1, 84, 8400], ["", "", ""], [1, 84, 8400] },
        // A dynamic dimension takes the size of its name.
        { [-1, 468, 3], ["batch", "", ""], [4, 468, 3] },
        // Two dimensions with one name take one size; two names take their own.
        { [-1, -1], ["batch", "batch"], [4, 4] },
        { [-1, 2, -1], ["batch", "", "n"], [4, 2, 7] },
        // A name on a fixed dimension does not override its size.
        { [3, -1], ["n", "batch"], [3, 4] },
        { [int.MaxValue], [""], [int.MaxValue] },
        // A fixed dimension of 0 is an output with no elements.
        { [0, 4], ["", ""], [0, 4] },
    };

    [Theory]
    [MemberData(nameof(Resolved))]
    public void TheDeclaredShape_WithDynamicDimensionsSizedByName(long[] declared, string[] names, int[] expected) =>
        Assert.Equal(new TensorShape(expected), DeclaredShape.Resolve("y", declared, names, Sizes));

    [Fact]
    public void AFixedShape_NeedsNoSizes() =>
        Assert.Equal(new TensorShape(2, 3), DeclaredShape.Resolve("y", [2, 3], ["", ""], dynamicSizes: null));

    [Fact]
    public void ASizeNoDimensionUses_IsIgnored()
    {
        var sizes = new Dictionary<string, int> { ["batch"] = 2, ["height"] = 0, ["unused"] = -5 };

        Assert.Equal(new TensorShape(2, 5), DeclaredShape.Resolve("y", [-1, 5], ["batch", ""], sizes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    public void AMissingSize_NamesTheOutputTheDimensionAndItsName(string? present)
    {
        var sizes = present is null ? null : new Dictionary<string, int> { [present] = 1 };

        var error = Assert.Throws<ArgumentException>(
            () => DeclaredShape.Resolve("landmarks", [1, -1, 3], ["", "points", ""], sizes));

        Assert.Equal("dynamicSizes", error.ParamName);
        Assert.Contains("'landmarks'", error.Message);
        Assert.Contains("dimension 1", error.Message);
        Assert.Contains("'points'", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ASizeBelowOne_IsRefused(int size)
    {
        var error = Assert.Throws<ArgumentException>(
            () => DeclaredShape.Resolve("boxes", [-1, 4], ["batch", ""], new Dictionary<string, int> { ["batch"] = size }));

        Assert.Equal("dynamicSizes", error.ParamName);
        Assert.Contains("'boxes'", error.Message);
        Assert.Contains("dimension 0", error.Message);
        Assert.Contains(size.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    public void AnUnnamedDynamicDimension_CannotBeSized()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => DeclaredShape.Resolve("scores", [1, -1], ["", ""], Sizes));

        Assert.Contains("'scores'", error.Message);
        Assert.Contains("dimension 1", error.Message);
    }

    [Fact]
    public void AFixedDimensionATensorShapeCannotHold_IsRefused()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => DeclaredShape.Resolve("y", [1, int.MaxValue + 1L], ["", ""], Sizes));

        Assert.Contains("'y'", error.Message);
        Assert.Contains("dimension 1", error.Message);
    }

    [Fact]
    public void AScalarOutput_IsRefused()
    {
        var error = Assert.Throws<NotSupportedException>(() => DeclaredShape.Resolve("count", [], [], Sizes));

        Assert.Contains("'count'", error.Message);
    }

    [Fact]
    public void NamesThatDoNotMatchTheShape_AreRefused() =>
        Assert.Throws<ArgumentException>(() => DeclaredShape.Resolve("y", [1, -1], ["batch"], Sizes));
}
