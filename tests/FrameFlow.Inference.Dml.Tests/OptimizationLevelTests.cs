using System.Runtime.InteropServices;
using FrameFlow.Graph;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>The graph optimization level a DirectML session loads its model at (#481).</summary>
public sealed class OptimizationLevelTests
{
    [Fact]
    public void TheDefault_IsBasic() =>
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_BASIC, new DmlInferenceSessionOptions().OptimizationLevel);

    [Fact]
    public void AnUndefinedLevel_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DmlInferenceSessionOptions { OptimizationLevel = (GraphOptimizationLevel)42 });

    [Theory]
    [InlineData(GraphOptimizationLevel.ORT_DISABLE_ALL)]
    [InlineData(GraphOptimizationLevel.ORT_ENABLE_BASIC)]
    [InlineData(GraphOptimizationLevel.ORT_ENABLE_ALL)]
    public void TheSessionOptions_CarryTheLevel(GraphOptimizationLevel level)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var options = DmlInferenceSession.BuildSessionOptions(
            new DmlInferenceSessionOptions { OptimizationLevel = level }, static _ => { });

        Assert.Equal(level, options.GraphOptimizationLevel);
        Assert.False(options.EnableMemoryPattern);
    }

    [RequiresDirectMLFact]
    public void AtEveryLevel_TheModelRuns()
    {
        foreach (var level in new[] { GraphOptimizationLevel.ORT_DISABLE_ALL, GraphOptimizationLevel.ORT_ENABLE_ALL })
        {
            using var session = new DmlInferenceSession(
                OnnxModel.Negate(1, 3, 4, 4), new DmlInferenceSessionOptions { OptimizationLevel = level });
            using var pool = new CpuTensorPool();
            using var input = pool.Rent<float>(new TensorShape(1, 3, 4, 4));
            using var output = pool.Rent<float>(new TensorShape(1, 3, 4, 4));
            for (int i = 0; i < input.Span.Length; i++)
                input.Span[i] = i;

            session.Run(input, output);

            Assert.Equal(input.ReadOnlySpan.ToArray().Select(v => -v), MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray());
        }
    }
}
