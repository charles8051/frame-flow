using System.Runtime.InteropServices;
using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Who releases an output ONNX Runtime allocated (#479): the tensor that reads it in place, on its
/// final reference, and the session's hand-off of the values a run returns. Driven with native
/// memory and a recording owner instead of an <c>OrtValue</c>, so no session runs.
/// </summary>
public sealed unsafe class AllocatedOutputOwnershipTests
{
    [Fact]
    public void TheTensorReadsTheOwnersMemoryInPlace()
    {
        using var owner = new NativeOwner(8);
        ((float*)owner.Address)[0] = 1.5f;
        ((float*)owner.Address)[1] = -2f;
        var tensor = Tensor(owner, 8);

        Assert.Equal([1.5f, -2f], MemoryMarshal.Cast<byte, float>(tensor.Bytes.Span).ToArray());
        ((float*)owner.Address)[1] = 7f;
        Assert.Equal(7f, MemoryMarshal.Cast<byte, float>(tensor.Bytes.Span)[1]);
        tensor.Dispose();
    }

    [Fact]
    public void TheFinalDispose_ReleasesTheOwnerOnce()
    {
        var owner = new NativeOwner(4);
        var tensor = Tensor(owner, 4);
        tensor.AddRef();

        tensor.Dispose();
        Assert.Equal(0, owner.Disposals);
        tensor.Dispose();

        Assert.Equal(1, owner.Disposals);
        Assert.True(tensor.IsReleased);
        Assert.Throws<ObjectDisposedException>(tensor.Dispose);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void AfterRelease_TheBytesAndEarlierSpansRefuseToRead()
    {
        var owner = new NativeOwner(4);
        var tensor = Tensor(owner, 4);
        var memory = tensor.Bytes;

        tensor.Dispose();

        Assert.Throws<ObjectDisposedException>(() => tensor.Bytes);
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => memory.Pin());
        Assert.Throws<ObjectDisposedException>(tensor.AddRef);
    }

    /// <summary>A session binding the tensor as an input pins it; the pin keeps the memory.</summary>
    [Fact]
    public void APin_KeepsTheMemoryUntilItIsDisposed()
    {
        var owner = new NativeOwner(4);
        var tensor = Tensor(owner, 4);

        var pin = tensor.Bytes.Pin();
        tensor.Dispose();
        Assert.Equal(0, owner.Disposals);
        Assert.Equal(owner.Address, (nint)pin.Pointer);

        pin.Dispose();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void TheValuesTheCallerBound_AreDisposed_AndTheAllocatedOnesGoToTheirTensors()
    {
        var bound = new NativeOwner(4);
        var allocated = new NativeOwner(4);

        using var outputs = OrtInferenceSessionBase.TakeValues(
            ["y", "indices"], [bound, allocated], ["indices"], (_, owner) => Tensor(owner, 4));

        Assert.Equal(["indices"], outputs.Keys);
        Assert.Equal(1, bound.Disposals);
        Assert.Equal(0, allocated.Disposals);
        outputs.Dispose();
        Assert.Equal(1, allocated.Disposals);
    }

    [Fact]
    public void AWrapThatFails_ReleasesEveryValue()
    {
        var first = new NativeOwner(4);
        var failing = new NativeOwner(4);
        var bound = new NativeOwner(4);

        Assert.Throws<NotSupportedException>(() => OrtInferenceSessionBase.TakeValues(
            ["a", "b", "y"],
            [first, failing, bound],
            ["a", "b"],
            (name, owner) => name == "b" ? throw new NotSupportedException() : Tensor(owner, 4)));

        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, failing.Disposals);
        Assert.Equal(1, bound.Disposals);
    }

    [Fact]
    public void NamesThatDoNotMatchTheValues_ReleaseEveryValue()
    {
        var a = new NativeOwner(4);
        var b = new NativeOwner(4);

        Assert.Throws<InvalidOperationException>(() => OrtInferenceSessionBase.TakeValues(
            ["a"], [a, b], ["a"], (_, owner) => Tensor(owner, 4)));

        Assert.Equal(1, a.Disposals);
        Assert.Equal(1, b.Disposals);
    }

    private static OrtOutputTensor Tensor(NativeOwner owner, int bytes) =>
        new(owner, owner.Address, bytes, DType.Float32, new TensorShape(bytes / 4));

    /// <summary>Native memory that counts its disposals and frees on the first.</summary>
    private sealed class NativeOwner : IDisposable
    {
        public NativeOwner(int bytes) => Address = (nint)NativeMemory.AllocZeroed((nuint)bytes);

        public nint Address { get; }

        public int Disposals { get; private set; }

        public void Dispose()
        {
            if (++Disposals == 1)
                NativeMemory.Free((void*)Address);
        }
    }
}
