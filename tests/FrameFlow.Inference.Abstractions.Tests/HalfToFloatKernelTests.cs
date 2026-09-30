using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// <see cref="HalfToFloatKernel"/> gives <c>(float)value</c>'s bits for every half, on both loops,
/// and <see cref="FloatReader"/> reads a model's fp16 output through it (#10).
/// </summary>
public sealed class HalfToFloatKernelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToFloats_IsTheFloatCast_ForEveryHalf(bool vector)
    {
        var halves = new Half[ushort.MaxValue + 1];
        for (int i = 0; i < halves.Length; i++)
            halves[i] = BitConverter.UInt16BitsToHalf((ushort)i);
        var floats = new float[halves.Length];

        HalfToFloatKernel.ToFloats(halves, floats, vector);

        for (int i = 0; i < halves.Length; i++)
        {
            uint expected = BitConverter.SingleToUInt32Bits((float)halves[i]);
            uint actual = BitConverter.SingleToUInt32Bits(floats[i]);
            Assert.True(expected == actual, $"Half 0x{i:X4}: expected 0x{expected:X8}, got 0x{actual:X8}.");
        }
    }

    /// <summary>
    /// A length that is not a whole number of vectors leaves a tail for the scalar loop, and the
    /// destination past the values is left alone.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(1001)]
    public void ToFloats_ConvertsTheTail_AndWritesNoFurther(int length)
    {
        var halves = new Half[length];
        for (int i = 0; i < length; i++)
            halves[i] = (Half)(i - (length / 2) + 0.5f);
        var floats = new float[length + 2];
        floats[length] = floats[length + 1] = 7f;

        HalfToFloatKernel.ToFloats(halves, floats);

        Assert.Equal(halves.Select(h => (float)h), floats[..length]);
        Assert.Equal([7f, 7f], floats[length..]);
    }

    [Fact]
    public void ToFloats_RefusesAShortDestination()
    {
        var error = Assert.Throws<ArgumentException>(() => HalfToFloatKernel.ToFloats(new Half[4], new float[3]));
        Assert.Equal("destination", error.ParamName);
    }

    [Fact]
    public void AReader_ReadsAFloatTensorsOwnElements_AndConvertsAHalfTensors()
    {
        using var pool = new CpuTensorPool();
        using var floats = pool.Rent<float>(new TensorShape(3));
        using var halves = pool.Rent<Half>(new TensorShape(3));
        float[] values = [1.5f, -2f, 65504f];
        values.CopyTo(floats.Span);
        for (int i = 0; i < values.Length; i++)
            halves.Span[i] = (Half)values[i];
        var reader = new FloatReader();

        var read = reader.Read(floats);
        Assert.True(
            Unsafe.AreSame(ref MemoryMarshal.GetReference(read), ref MemoryMarshal.GetReference(floats.ReadOnlySpan)),
            "A float tensor is read in place.");
        Assert.Equal(values, reader.Read(halves).ToArray());
    }

    [Fact]
    public void AReader_RefusesATensorOfAnotherType()
    {
        using var pool = new CpuTensorPool();
        using var ints = pool.Rent<int>(new TensorShape(2));

        var error = Assert.Throws<NotSupportedException>(() => new FloatReader().Read(ints));
        Assert.Contains("Int32", error.Message, StringComparison.Ordinal);
    }
}
