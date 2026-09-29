using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A shape may have a dimension of 0, for a model output with nothing in it, and the default shape
/// is a scalar's (#479).
/// </summary>
public sealed class TensorShapeTests
{
    [Fact]
    public void ADimensionOfZero_HoldsNoElements()
    {
        var shape = new TensorShape(0, 4);

        Assert.Equal(2, shape.Rank);
        Assert.Equal(0, shape[0]);
        Assert.Equal(0, shape.ElementCount);
        Assert.Equal(0, shape.ByteCount(DType.Float32));
    }

    [Fact]
    public void AnImmutableArrayWithAZero_IsAShapeToo() =>
        Assert.Equal(new TensorShape(3, 0), new TensorShape(System.Collections.Immutable.ImmutableArray.Create(3, 0)));

    [Fact]
    public void ANegativeDimension_IsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => new TensorShape(2, -1));

        Assert.Contains("Dimension 1", error.Message);
    }

    [Fact]
    public void NoDimensions_AreRefused() => Assert.Throws<ArgumentException>(() => new TensorShape(Array.Empty<int>()));

    [Fact]
    public void TheDefaultShape_IsAScalar()
    {
        TensorShape scalar = default;

        Assert.Equal(0, scalar.Rank);
        Assert.Equal(1, scalar.ElementCount);
        Assert.Equal(8, scalar.ByteCount(DType.Int64));
    }

    [Fact]
    public void AnEmptyTensor_RentsAndReturns()
    {
        using var pool = new CpuTensorPool();

        var tensor = pool.Rent<float>(new TensorShape(0, 4));
        Assert.Equal(0, tensor.ByteCount);
        Assert.True(tensor.Span.IsEmpty);
        tensor.Dispose();

        Assert.Equal(0, pool.Outstanding);
    }
}
