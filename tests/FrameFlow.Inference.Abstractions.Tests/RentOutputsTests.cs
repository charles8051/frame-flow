using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// <see cref="InferenceSessionExtensions.RentOutputs{T}"/> rents a session's outputs from their
/// declared shapes into a set <c>Run</c> takes and one dispose returns (#485).
/// </summary>
public sealed class RentOutputsTests
{
    private static readonly Dictionary<string, int> Batch3 = new() { ["batch"] = 3 };

    [Fact]
    public void OneTensorPerOutput_AtItsDeclaredShape()
    {
        var session = Detector();
        using var pool = new CpuTensorPool();

        using var outputs = session.RentOutputs<float>(pool, Batch3);

        Assert.Equal(["boxes", "scores"], outputs.Keys.Order());
        Assert.Equal(new TensorShape(3, 4), outputs["boxes"].Shape);
        Assert.Equal(new TensorShape(3), outputs["scores"].Shape);
        Assert.Equal(DType.Float32, outputs["scores"].Dtype);
        Assert.Equal(2, pool.Outstanding);
    }

    [Fact]
    public void RunWritesIntoThem_AndTheyReadBackTyped()
    {
        var session = Detector();
        using var pool = new CpuTensorPool();
        using var outputs = session.RentOutputs<float>(pool, Batch3);

        session.Run(new Dictionary<string, ICpuTensor>(), outputs);

        Assert.Equal([1f, 2f, 3f], outputs["scores"].ReadOnlySpan.ToArray());
        Assert.Equal(Enumerable.Range(1, 12).Select(v => (float)v), outputs["boxes"].ReadOnlySpan.ToArray());
    }

    [Fact]
    public void Disposing_ReturnsEveryTensorToThePool()
    {
        using var pool = new CpuTensorPool();
        var outputs = Detector().RentOutputs<float>(pool, Batch3);

        outputs.Dispose();

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(2, pool.TotalReturned);
    }

    [Fact]
    public void AShapeThatDoesNotResolve_RentsNothing()
    {
        using var pool = new CpuTensorPool();

        var error = Assert.Throws<ArgumentException>(() => Detector().RentOutputs<float>(pool));

        Assert.Contains("'boxes'", error.Message);
        Assert.Equal(0, pool.TotalRented);
    }

    /// <summary>The second output is 16 GiB of floats, past what a pool buffer can hold.</summary>
    [Fact]
    public void ARentThatFails_ReturnsTheTensorsAlreadyRented()
    {
        var session = new ShapedSession(("small", [2], [""]), ("huge", [65536, 65536], ["", ""]));
        using var pool = new CpuTensorPool();

        Assert.Throws<ArgumentException>(() => session.RentOutputs<float>(pool));

        Assert.Equal(1, pool.TotalRented);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void ASessionThatDoesNotNameItsDimensions_NamesNone()
    {
        IInferenceSession session = new UnnamedSession();

        Assert.Equal([["", "", ""], [""]], session.OutputDimensionNames);
    }

    [Fact]
    public void WithNoNames_OnlyFixedShapesRent()
    {
        IInferenceSession session = new UnnamedSession();
        using var pool = new CpuTensorPool();

        var error = Assert.Throws<NotSupportedException>(() => session.RentOutputs<float>(pool, Batch3));

        Assert.Contains("'count'", error.Message);
    }

    [Fact]
    public void ASecondDispose_DoesNothing()
    {
        using var pool = new CpuTensorPool();
        var outputs = Detector().RentOutputs<float>(pool, Batch3);

        outputs.Dispose();
        outputs.Dispose();

        Assert.Equal(2, pool.TotalReturned);
    }

    [Fact]
    public void ATensorWithAReferenceTaken_OutlivesTheSet()
    {
        using var pool = new CpuTensorPool();
        var outputs = Detector().RentOutputs<float>(pool, Batch3);
        var scores = outputs["scores"];
        scores.AddRef();

        outputs.Dispose();

        Assert.Equal(1, pool.Outstanding);
        scores.Dispose();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void AfterDispose_TheSetRefusesLookups()
    {
        using var pool = new CpuTensorPool();
        var outputs = Detector().RentOutputs<float>(pool, Batch3);

        outputs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => outputs["scores"]);
        Assert.Throws<ObjectDisposedException>(() => outputs.GetEnumerator());
        Assert.Throws<ObjectDisposedException>(() => outputs.TryGetValue("scores", out _));
    }

    [Fact]
    public void ATensorThatThrowsOnDispose_DoesNotStopTheRest()
    {
        using var pool = new CpuTensorPool();
        var first = pool.Rent<float>(new TensorShape(1));
        var second = pool.Rent<float>(new TensorShape(1));
        var outputs = new SessionOutputs<CpuTensor<float>>([new("a", first), new("b", second)]);
        first.Dispose();

        var error = Assert.Throws<AggregateException>(outputs.Dispose);

        Assert.IsType<ObjectDisposedException>(Assert.Single(error.InnerExceptions));
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void TwoTensorsWithOneName_AreRefused()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1));

        Assert.Throws<ArgumentException>(() => new SessionOutputs<CpuTensor<float>>([new("a", tensor), new("a", tensor)]));
    }

    private static ShapedSession Detector() =>
        new(("boxes", [-1, 4], ["batch", ""]), ("scores", [-1], ["batch"]));

    /// <summary>
    /// Declares its outputs' shapes and names; a run writes 1, 2, 3... into each output it is given.
    /// </summary>
    private sealed class ShapedSession(params (string Name, long[] Shape, string[] DimensionNames)[] outputs)
        : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = [];

        public IReadOnlyList<string> OutputNames { get; } = [.. outputs.Select(o => o.Name)];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [.. outputs.Select(o => (IReadOnlyList<long>)o.Shape)];

        public IReadOnlyList<IReadOnlyList<string>> OutputDimensionNames { get; } =
            [.. outputs.Select(o => (IReadOnlyList<string>)o.DimensionNames)];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            foreach (var (_, tensor) in outputs)
            {
                var values = ((CpuTensor<float>)tensor).Span;
                for (int i = 0; i < values.Length; i++)
                    values[i] = i + 1;
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A session that leaves <see cref="IInferenceSession.OutputDimensionNames"/> to the default.</summary>
    private sealed class UnnamedSession : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = [];

        public IReadOnlyList<string> OutputNames { get; } = ["boxes", "count"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [new long[] { 1, 10, 4 }, new long[] { -1 }];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
