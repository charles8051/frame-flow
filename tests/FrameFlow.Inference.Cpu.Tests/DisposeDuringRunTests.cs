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

    private sealed class RecordingSession() : OrtInferenceSessionBase(OnnxModel.Negate(1, 4), new SessionOptions())
    {
        public bool Released { get; private set; }

        public IDisposable HoldRun() => BeginRun();

        protected override void DisposeProviderResources() => Released = true;
    }
}
