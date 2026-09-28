using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Cpu.Tests;

/// <summary>
/// A session disposed while a run is in progress keeps its native state until the run ends, and
/// refuses a run afterwards (#432). The run is held open through the scope a derived session takes
/// around its own runs, so nothing here waits on a thread.
/// </summary>
public sealed class DisposeDuringRunTests
{
    [Fact]
    public void DisposedDuringARun_TheSessionIsFreedWhenTheRunEnds()
    {
        var session = new RecordingSession();

        using (session.HoldRun())
        {
            session.Dispose();
            Assert.False(session.Released);
        }

        Assert.True(session.Released);
    }

    [Fact]
    public void DisposedWithNoRun_TheSessionIsFreedAtOnce_AndRefusesARun()
    {
        var session = new RecordingSession();
        var pool = new CpuTensorPool();

        session.Dispose();

        Assert.True(session.Released);
        Assert.Throws<ObjectDisposedException>(
            () => session.Run(pool.Rent<float>(new TensorShape(1, 4)), pool.Rent<float>(new TensorShape(1, 4))));
    }

    /// <summary>
    /// A run disposed after it started still runs its steps: they belong to the run's scope rather
    /// than taking another, which a disposed session would refuse.
    /// </summary>
    [Fact]
    public void ARunInProgress_CompletesItsStepsAfterDispose()
    {
        var session = new RecordingSession();
        var pool = new CpuTensorPool();
        var input = pool.Rent<float>(new TensorShape(1, 4));
        var output = pool.Rent<float>(new TensorShape(1, 4));
        float[] values = [1f, -2f, 3.5f, 0f];
        values.AsSpan().CopyTo(input.Span);

        using (session.HoldRun())
        {
            session.Dispose();
            session.RunStep(input, output);
        }

        Assert.Equal(values.Select(v => -v), output.Span.ToArray());
        Assert.True(session.Released);
    }

    [Fact]
    public void ARunStepOutsideARun_IsRefused()
    {
        using var session = new RecordingSession();
        var pool = new CpuTensorPool();

        Assert.Throws<InvalidOperationException>(
            () => session.RunStep(pool.Rent<float>(new TensorShape(1, 4)), pool.Rent<float>(new TensorShape(1, 4))));
    }

    private sealed class RecordingSession() : OrtInferenceSessionBase(OnnxModel.Negate(1, 4), new SessionOptions())
    {
        public bool Released { get; private set; }

        public IDisposable HoldRun() => BeginRun();

        public void RunStep(ICpuTensor input, ICpuTensor output) =>
            RunWithHostOutputs(
                new Dictionary<string, ICpuTensor> { ["x"] = input },
                new Dictionary<string, ICpuTensor> { ["y"] = output },
                bindDeviceInputs: null);

        protected override void DisposeProviderResources() => Released = true;
    }
}
