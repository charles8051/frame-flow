using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Cuda;
using FrameFlow.Inference.Dml.Tests;
using Xunit;

namespace FrameFlow.Inference.Cuda.Tests;

/// <summary>
/// A <see cref="CudaInferenceSession"/> opens and runs a model on a real GPU.
/// Gated by <see cref="CudaBootstrapper.Probe"/> through
/// <see cref="RequiresCudaFactAttribute"/>, so it skips on any machine whose
/// CUDA stack is not ready, which includes every CI runner. It runs on a GPU
/// host with the driver, the CUDA 12 runtime and cuDNN 9 in place, on Windows
/// or Linux x64.
/// </summary>
public sealed class CudaSessionGpuTests
{
    [RequiresCudaFact]
    public void ACudaSession_RunsAModel()
    {
        using var session = new CudaInferenceSession(OnnxModel.Negate(1, 4));
        using var pool = new CpuTensorPool();
        var input = pool.Rent<float>(new TensorShape(1, 4));
        var output = pool.Rent<float>(new TensorShape(1, 4));
        float[] values = [1f, -2f, 3.5f, 0f];
        values.AsSpan().CopyTo(input.Span);

        session.Run(
            new Dictionary<string, ICpuTensor> { ["x"] = input },
            new Dictionary<string, ICpuTensor> { ["y"] = output }
        );

        Assert.Equal(
            values.Select(v => -v),
            MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray()
        );
    }
}

/// <summary>
/// Skipped unless <see cref="CudaBootstrapper.Probe"/> reports the CUDA stack
/// ready. <c>FRAMEFLOW_CUDA_TESTS=0</c> skips without probing, for a machine
/// where the probe itself, which loads the CUDA libraries, is unwanted.
/// </summary>
internal sealed class RequiresCudaFactAttribute : FactAttribute
{
    private static readonly Lazy<CudaBootstrapResult> Probe = new(
        () => new CudaBootstrapper().Probe(),
        isThreadSafe: true
    );

    public RequiresCudaFactAttribute()
    {
        var optOut = Environment.GetEnvironmentVariable("FRAMEFLOW_CUDA_TESTS");
        if (
            optOut is not null
            && (
                optOut.Equals("0", StringComparison.Ordinal)
                || optOut.Equals("false", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            Skip = "CUDA tests disabled by FRAMEFLOW_CUDA_TESTS.";
            return;
        }

        var result = Probe.Value;
        if (!result.IsReady)
            Skip = $"CUDA stack not ready: {result.Status}, missing {result.Missing}.";
    }
}
