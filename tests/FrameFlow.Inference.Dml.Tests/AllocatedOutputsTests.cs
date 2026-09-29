using System.Runtime.InteropServices;
using FrameFlow.Graph;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// DirectML runs a model whose output shape depends on its input, with ONNX Runtime allocating the
/// outputs the caller leaves out and copying them to CPU memory (#479).
/// </summary>
public sealed class AllocatedOutputsTests
{
    private static readonly float[] ThreeNonZero = [0f, 1.5f, 0f, 2f, 0f, -3f];

    [RequiresDirectMLFact]
    public void AnOutputShapedByTheInput_ComesBack_BesideOneBoundByTheCaller()
    {
        using var session = new DmlInferenceSession(OnnxModel.DataDependent(2, 3));
        using var pool = new CpuTensorPool();
        using var input = pool.Rent<float>(new TensorShape(2, 3));
        using var y = pool.Rent<float>(new TensorShape(2, 3));
        ThreeNonZero.AsSpan().CopyTo(input.Span);

        using var outputs = session.RunAllocating(
            new Dictionary<string, ICpuTensor> { ["x"] = input },
            new Dictionary<string, ICpuTensor> { ["y"] = y });

        var indices = outputs["indices"];
        Assert.Equal(new TensorShape(2, 3), indices.Shape);
        Assert.Equal([0L, 1, 1, 1, 0, 2], MemoryMarshal.Cast<byte, long>(indices.Bytes.Span).ToArray());
        Assert.Equal(ThreeNonZero.Select(v => -v), y.ReadOnlySpan.ToArray());
        Assert.Equal(["indices", "size"], outputs.Keys.Order());
    }
}
