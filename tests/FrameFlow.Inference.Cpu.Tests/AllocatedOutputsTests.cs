using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;

namespace FrameFlow.Inference.Cpu.Tests;

/// <summary>
/// A model whose output shape depends on its input runs, with ONNX Runtime allocating the outputs
/// the caller leaves out (#479). The model's <c>indices</c> are <c>NonZero(x)</c>: int64
/// <c>[2, count of non-zero elements]</c> for a 2x3 input.
/// </summary>
public sealed class AllocatedOutputsTests
{
    private static readonly float[] ThreeNonZero = [0f, 1.5f, 0f, 2f, 0f, -3f];

    [Fact]
    public void AnOutputShapedByTheInput_ComesBackAtTheShapeTheRunProduced()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var input = Input(ThreeNonZero);

        using var outputs = session.RunAllocating(Inputs(input));

        var indices = outputs["indices"];
        Assert.Equal(DType.Int64, indices.Dtype);
        Assert.Equal(new TensorShape(2, 3), indices.Shape);
        // Row then column of (0, 1), (1, 0) and (1, 2).
        Assert.Equal([0L, 1, 1, 1, 0, 2], Longs(indices));
    }

    [Fact]
    public void WithEveryOutputLeftOut_EveryOutputComesBack()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var input = Input(ThreeNonZero);

        using var outputs = session.RunAllocating(Inputs(input));

        Assert.Equal(["indices", "size", "y"], outputs.Keys.Order());
        Assert.Equal(new TensorShape(2, 3), outputs["y"].Shape);
        Assert.Equal(ThreeNonZero.Select(v => -v), MemoryMarshal.Cast<byte, float>(outputs["y"].Bytes.Span).ToArray());
    }

    [Fact]
    public void AScalarOutput_HasTheDefaultShape()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var input = Input(ThreeNonZero);

        using var outputs = session.RunAllocating(Inputs(input));

        var size = outputs["size"];
        Assert.Equal(0, size.Shape.Rank);
        Assert.Equal(default, size.Shape);
        Assert.Equal([6L], Longs(size));
    }

    [Fact]
    public void NothingFound_IsADimensionOfZero()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var input = Input(new float[6]);

        using var outputs = session.RunAllocating(Inputs(input));

        var indices = outputs["indices"];
        Assert.Equal(new TensorShape(2, 0), indices.Shape);
        Assert.Equal(0, indices.ByteCount);
        Assert.True(indices.Bytes.IsEmpty);
    }

    /// <summary>
    /// The mixed form: the caller binds the output whose shape is fixed and leaves the one shaped by
    /// the input to ONNX Runtime.
    /// </summary>
    [Fact]
    public void ABoundOutputIsWrittenInPlace_AndOnlyTheOthersComeBack()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var pool = new CpuTensorPool();
        using var input = Input(ThreeNonZero);
        using var y = pool.Rent<float>(new TensorShape(2, 3));

        using var outputs = session.RunAllocating(Inputs(input), new Dictionary<string, ICpuTensor> { ["y"] = y });

        Assert.Equal(["indices", "size"], outputs.Keys.Order());
        Assert.Equal(ThreeNonZero.Select(v => -v), y.ReadOnlySpan.ToArray());
        Assert.Equal([0L, 1, 1, 1, 0, 2], Longs(outputs["indices"]));
    }

    [Fact]
    public void EveryOutputBound_ReturnsNone()
    {
        using var session = new CpuInferenceSession(OnnxModel.Negate(2, 3));
        using var pool = new CpuTensorPool();
        using var input = Input(ThreeNonZero);
        using var y = pool.Rent<float>(new TensorShape(2, 3));

        using var outputs = session.RunAllocating(Inputs(input), new Dictionary<string, ICpuTensor> { ["y"] = y });

        Assert.Empty(outputs);
        Assert.Equal(ThreeNonZero.Select(v => -v), y.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void DisposingTheSet_ReleasesEachOutput()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var input = Input(ThreeNonZero);
        var outputs = session.RunAllocating(Inputs(input));
        var indices = outputs["indices"];
        var memory = indices.Bytes;

        outputs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => indices.Bytes);
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(indices.AddRef);
    }

    [Fact]
    public void AnOutputWithAReferenceTaken_OutlivesTheSetAndTheSession()
    {
        ICpuTensor indices;
        using (var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3)))
        using (var input = Input(ThreeNonZero))
        using (var outputs = session.RunAllocating(Inputs(input)))
        {
            indices = (ICpuTensor)outputs["indices"].AddRef();
        }

        Assert.Equal([0L, 1, 1, 1, 0, 2], Longs(indices));
        indices.Dispose();
        Assert.Throws<ObjectDisposedException>(() => indices.Bytes);
    }

    /// <summary>An allocated output binds as another run's input, pinned in place.</summary>
    [Fact]
    public void AnAllocatedOutput_IsAnotherRunsInput()
    {
        using var first = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var second = new CpuInferenceSession(OnnxModel.Negate(2, 3));
        using var input = Input(ThreeNonZero);
        using var negated = first.RunAllocating(Inputs(input));

        using var back = second.RunAllocating(Inputs(negated["y"]));

        Assert.Equal(ThreeNonZero, MemoryMarshal.Cast<byte, float>(back["y"].Bytes.Span).ToArray());
    }

    [Fact]
    public void AnUnknownOutputName_IsRefused()
    {
        using var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var pool = new CpuTensorPool();
        using var input = Input(ThreeNonZero);
        using var stray = pool.Rent<float>(new TensorShape(2, 3));

        var error = Assert.Throws<ArgumentException>(
            () => session.RunAllocating(Inputs(input), new Dictionary<string, ICpuTensor> { ["z"] = stray }));

        Assert.Contains("'z'", error.Message);
    }

    [Fact]
    public void ADisposedSession_RefusesTheRun()
    {
        var session = new CpuInferenceSession(OnnxModel.DataDependent(2, 3));
        using var input = Input(ThreeNonZero);
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.RunAllocating(Inputs(input)));
    }

    [Fact]
    public void EveryOrtSession_IsAnAllocatingSession()
    {
        using IInferenceSession session = new CpuInferenceSession(OnnxModel.Negate(1, 4));

        Assert.IsAssignableFrom<IAllocatingSession>(session);
    }

    private static CpuTensor<float> Input(float[] values)
    {
        var tensor = new CpuTensorPool().Rent<float>(new TensorShape(2, 3));
        values.AsSpan().CopyTo(tensor.Span);
        return tensor;
    }

    private static Dictionary<string, ICpuTensor> Inputs(ICpuTensor x) => new() { ["x"] = x };

    private static long[] Longs(ICpuTensor tensor) => MemoryMarshal.Cast<byte, long>(tensor.Bytes.Span).ToArray();
}
