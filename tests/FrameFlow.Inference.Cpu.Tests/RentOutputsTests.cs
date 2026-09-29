using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;

namespace FrameFlow.Inference.Cpu.Tests;

/// <summary>
/// An ONNX Runtime session reports the model's dimension names, and its outputs rent by them (#485).
/// </summary>
public sealed class RentOutputsTests
{
    [Fact]
    public void TheSession_ReportsTheModelsDimensionNames()
    {
        using var session = new CpuInferenceSession(OnnxModel.Negate("batch", 4, "width"));

        Assert.Equal([["batch", "", "width"]], session.OutputDimensionNames);
    }

    [Fact]
    public void OutputsRentedByDimensionName_RunTheModel_AndReturnToThePool()
    {
        using var session = new CpuInferenceSession(OnnxModel.Negate("batch", 4));
        using var pool = new CpuTensorPool();
        using var input = pool.Rent<float>(new TensorShape(2, 4));
        for (int i = 0; i < input.Span.Length; i++)
            input.Span[i] = i;

        var outputs = session.RentOutputs<float>(pool, new Dictionary<string, int> { ["batch"] = 2 });
        session.Run(new Dictionary<string, ICpuTensor> { ["x"] = input }, outputs);

        Assert.Equal(new TensorShape(2, 4), outputs["y"].Shape);
        Assert.Equal(input.ReadOnlySpan.ToArray().Select(v => -v), outputs["y"].ReadOnlySpan.ToArray());
        outputs.Dispose();
        Assert.Equal(1, pool.Outstanding);
    }
}
