using FrameFlow.Graph;
using FrameFlow.Inference;

namespace FrameFlow.Inference.Abstractions.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IInferenceSession"/> for factory tests. Records
/// the model path it was constructed with so tests can assert per-call
/// behavior. Does not implement <c>Run</c> meaningfully — the factory
/// tests don't exercise inference.
/// </summary>
internal sealed class FakeInferenceSession : IInferenceSession
{
    public FakeInferenceSession(string modelPath) => ModelPath = modelPath;

    public string ModelPath { get; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<string> InputNames { get; } = new[] { "input" };

    public IReadOnlyList<string> OutputNames { get; } = new[] { "output" };

    public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } =
        new IReadOnlyList<long>[] { new long[] { 1, 3, 640, 640 } };

    public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } =
        new IReadOnlyList<long>[] { new long[] { 1, 84, 8400 } };

    public void Run(
        IReadOnlyDictionary<string, ICpuTensor> inputs,
        IReadOnlyDictionary<string, ICpuTensor> outputs)
    {
        throw new NotSupportedException(
            "FakeInferenceSession does not implement Run; factory tests "
                + "exercise construction + selection, not inference.");
    }

    public void Dispose() => Disposed = true;
}
