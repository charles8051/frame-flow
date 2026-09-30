using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;

namespace FrameFlow.Inference.Cpu.Tests;

/// <summary>An ONNX Runtime session reports the element types its model declares (#520).</summary>
public sealed class ElementTypeTests
{
    [Fact]
    public void TheSession_ReportsTheModelsElementTypes_AndRunsHalvesIn()
    {
        using var session = new CpuInferenceSession(OnnxModel.Cast(OnnxModel.Float16, OnnxModel.Float, 1, 3));
        using var pool = new CpuTensorPool();
        using var input = pool.Rent<Half>(new TensorShape(1, 3));
        using var output = pool.Rent<float>(new TensorShape(1, 3));
        Half[] values = [(Half)1.5f, (Half)(-2f), Half.MaxValue];
        values.CopyTo(input.Span);

        session.Run(input, output);

        Assert.Equal([DType.Float16], session.InputElementTypes);
        Assert.Equal([DType.Float32], session.OutputElementTypes);
        Assert.Equal([1.5f, -2f, 65504f], output.ReadOnlySpan.ToArray());
    }

    /// <summary>
    /// A model with an output FrameFlow has no type for still loads, reports its inputs, and says
    /// which output it cannot report.
    /// </summary>
    [Fact]
    public void AnElementTypeWithNoDType_IsNamedWhenItsListIsRead()
    {
        using var session = new CpuInferenceSession(OnnxModel.Cast(OnnxModel.Float, OnnxModel.String, 1, 2));

        Assert.Equal([DType.Float32], session.InputElementTypes);
        var error = Assert.Throws<NotSupportedException>(() => session.OutputElementTypes);
        Assert.Equal("Output 'y' has element type String, which has no FrameFlow DType.", error.Message);
    }
}
