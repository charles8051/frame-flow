using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Cpu.Tests;

/// <summary>
/// With FrameFlow.Inference.Cpu and no other inference package, a CPU session opens and runs on
/// every OS CI builds on (#437). Before the package, Linux and macOS had no native runtime to load.
/// </summary>
public sealed class CpuSessionTests
{
    [Fact]
    public void ACpuSession_RunsAModel()
    {
        using var session = new CpuInferenceSession(OnnxModel.Negate(1, 4));
        var pool = new CpuTensorPool();
        var input = pool.Rent<float>(new TensorShape(1, 4));
        var output = pool.Rent<float>(new TensorShape(1, 4));
        float[] values = [1f, -2f, 3.5f, 0f];
        values.AsSpan().CopyTo(input.Span);

        session.Run(
            new Dictionary<string, ICpuTensor> { ["x"] = input },
            new Dictionary<string, ICpuTensor> { ["y"] = output });

        Assert.Equal(values.Select(v => -v), MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray());
    }

    /// <summary>
    /// The native runtime that loaded is the CPU package's, and the managed layer is the same
    /// version, which is what #438's minimum resolves to.
    /// </summary>
    [Fact]
    public void TheNativeRuntime_IsTheCpuPackagesAndMatchesTheManagedLayer()
    {
        string native = OrtEnv.Instance().GetVersionString();
        var managed = typeof(OrtEnv).Assembly.GetName().Version!;

        Assert.Equal("1.30.0", native);
        Assert.Equal(native, $"{managed.Major}.{managed.Minor}.{managed.Build}");
    }
}
