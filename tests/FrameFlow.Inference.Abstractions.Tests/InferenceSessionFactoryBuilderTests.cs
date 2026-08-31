using FrameFlow.Inference;
using FrameFlow.Inference.Abstractions.Tests.Fakes;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Tests for <see cref="InferenceSessionFactoryBuilder"/> /
/// <see cref="IInferenceSessionFactory"/>: EP probe order, fallback
/// behavior, ActiveProvider caching, argument validation. The fake
/// providers either return a <see cref="FakeInferenceSession"/> or
/// throw — that's enough to drive every branch of the factory's
/// selection logic without a real ORT runtime.
/// </summary>
public class InferenceSessionFactoryBuilderTests
{
    // ── Argument validation ────────────────────────────────────────────

    [Fact]
    public void Create_NullProviders_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InferenceSessionFactoryBuilder.Create(
                preferred: ExecutionProvider.Cpu,
                providers: null!));
    }

    [Fact]
    public void Create_EmptyProviders_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            InferenceSessionFactoryBuilder.Create(
                preferred: ExecutionProvider.Cpu,
                providers: new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>()));
        Assert.Equal("providers", ex.ParamName);
    }

    [Fact]
    public void Create_PreferredNotRegistered_Throws()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.DirectML] = path => new FakeInferenceSession(path),
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            InferenceSessionFactoryBuilder.Create(
                preferred: ExecutionProvider.Cuda,
                providers: providers));
        Assert.Equal("preferred", ex.ParamName);
        Assert.Contains("Cuda", ex.Message);
    }

    // ── EP selection: happy paths ──────────────────────────────────────

    [Fact]
    public void Open_PreferredSucceeds_ReturnsPreferredSession_AndCaches()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = path => new FakeInferenceSession($"cuda:{path}"),
            [ExecutionProvider.DirectML] = path => new FakeInferenceSession($"dml:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        Assert.Null(factory.ActiveProvider);

        using var session = (FakeInferenceSession)factory.Open("model.onnx");

        Assert.Equal("cuda:model.onnx", session.ModelPath);
        Assert.Equal(ExecutionProvider.Cuda, factory.ActiveProvider);
    }

    [Fact]
    public void Open_PreferredFails_FallsBackToNext()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ =>
                throw new InvalidOperationException("CUDA not available on this host"),
            [ExecutionProvider.DirectML] = path => new FakeInferenceSession($"dml:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        using var session = (FakeInferenceSession)factory.Open("model.onnx");

        Assert.Equal("dml:model.onnx", session.ModelPath);
        Assert.Equal(ExecutionProvider.DirectML, factory.ActiveProvider);
    }

    [Fact]
    public void Open_PreferredAndFirstFallbackFail_FallsBackToCpu()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ => throw new InvalidOperationException("CUDA missing"),
            [ExecutionProvider.DirectML] = _ => throw new InvalidOperationException("DML failed"),
            [ExecutionProvider.Cpu] = path => new FakeInferenceSession($"cpu:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        using var session = (FakeInferenceSession)factory.Open("model.onnx");

        Assert.Equal("cpu:model.onnx", session.ModelPath);
        Assert.Equal(ExecutionProvider.Cpu, factory.ActiveProvider);
    }

    // ── EP selection: all fail ─────────────────────────────────────────

    [Fact]
    public void Open_AllProvidersFail_Throws_WithAggregatedFailures()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ => throw new InvalidOperationException("cuda boom"),
            [ExecutionProvider.DirectML] = _ => throw new DllNotFoundException("dml dll missing"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Open("model.onnx"));

        Assert.Contains("All execution providers failed", ex.Message);
        Assert.Contains("Cuda", ex.Message);
        Assert.Contains("DirectML", ex.Message);
        Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(2, ((AggregateException)ex.InnerException!).InnerExceptions.Count);
        Assert.Null(factory.ActiveProvider);
    }

    // ── Cached provider on repeat calls ────────────────────────────────

    [Fact]
    public void Open_CalledTwice_ReusesActiveProvider_NoReProbe()
    {
        int cudaConstructions = 0;
        int dmlConstructions = 0;
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = path =>
            {
                cudaConstructions++;
                throw new InvalidOperationException("CUDA out of memory");
            },
            [ExecutionProvider.DirectML] = path =>
            {
                dmlConstructions++;
                return new FakeInferenceSession($"dml:{path}");
            },
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        using var first = factory.Open("a.onnx");
        using var second = factory.Open("b.onnx");

        Assert.Equal(ExecutionProvider.DirectML, factory.ActiveProvider);
        // CUDA tried once on the first Open's probe pass; not retried on the cached path.
        Assert.Equal(1, cudaConstructions);
        // DML called twice — once for the first Open, once for the cached second Open.
        Assert.Equal(2, dmlConstructions);
    }

    // ── Fallback order: default and custom ─────────────────────────────

    [Fact]
    public void DefaultFallback_FollowsExecutionProviderEnumOrder()
    {
        // Preferred=Cuda; both Cpu and DirectML registered. Default
        // fallback walks ExecutionProvider declaration order: Cpu (0)
        // before DirectML (1). With Cuda failing, the next attempt
        // should be Cpu.
        var attempts = new List<ExecutionProvider>();
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ =>
            {
                attempts.Add(ExecutionProvider.Cuda);
                throw new InvalidOperationException("nope");
            },
            [ExecutionProvider.DirectML] = path =>
            {
                attempts.Add(ExecutionProvider.DirectML);
                return new FakeInferenceSession(path);
            },
            [ExecutionProvider.Cpu] = path =>
            {
                attempts.Add(ExecutionProvider.Cpu);
                return new FakeInferenceSession(path);
            },
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        using var session = factory.Open("model.onnx");

        Assert.Equal(new[] { ExecutionProvider.Cuda, ExecutionProvider.Cpu }, attempts);
        Assert.Equal(ExecutionProvider.Cpu, factory.ActiveProvider);
    }

    [Fact]
    public void CustomFallback_RespectsOrder_AndSkipsUnregisteredProviders()
    {
        var attempts = new List<ExecutionProvider>();
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ =>
            {
                attempts.Add(ExecutionProvider.Cuda);
                throw new InvalidOperationException("nope");
            },
            [ExecutionProvider.DirectML] = path =>
            {
                attempts.Add(ExecutionProvider.DirectML);
                return new FakeInferenceSession(path);
            },
            // Note: Cpu deliberately NOT registered.
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers,
            fallbackOrder: new[] { ExecutionProvider.Cpu, ExecutionProvider.DirectML });

        using var session = factory.Open("model.onnx");

        // Cpu skipped (not registered); DirectML taken.
        Assert.Equal(new[] { ExecutionProvider.Cuda, ExecutionProvider.DirectML }, attempts);
        Assert.Equal(ExecutionProvider.DirectML, factory.ActiveProvider);
    }

    // ── Argument validation: Open ──────────────────────────────────────

    [Fact]
    public void Open_NullOrEmptyModelPath_Throws()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cpu] = path => new FakeInferenceSession(path),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cpu,
            providers: providers);

        Assert.Throws<ArgumentNullException>(() => factory.Open(null!));
        Assert.Throws<ArgumentException>(() => factory.Open(string.Empty));
    }

    // ── Progress reporting (opt-in) ────────────────────────────────────

    [Fact]
    public void Open_NullProgress_BehavesLikeParameterlessOverload()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cpu] = path => new FakeInferenceSession($"cpu:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cpu,
            providers: providers);

        // The IProgress overload with a null reporter is byte-for-byte the
        // parameterless Open(): same session, same caching, no throw.
        using var session = (FakeInferenceSession)factory.Open("model.onnx", progress: null);

        Assert.Equal("cpu:model.onnx", session.ModelPath);
        Assert.Equal(ExecutionProvider.Cpu, factory.ActiveProvider);
    }

    [Fact]
    public void Open_WithProgress_PreferredSucceeds_ReportsProbingThenOpening()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = path => new FakeInferenceSession($"cuda:{path}"),
            [ExecutionProvider.DirectML] = path => new FakeInferenceSession($"dml:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);
        var progress = new RecordingProgress<InferenceSessionProgress>();

        using var session = factory.Open("model.onnx", progress);

        // One probe of the preferred EP, then the session-open milestone for it.
        Assert.Equal(
            new[]
            {
                (InferenceSessionPhase.ProbingProvider, ExecutionProvider.Cuda),
                (InferenceSessionPhase.OpeningSession, ExecutionProvider.Cuda),
            },
            progress.Reports.Select(r => (r.Phase, r.Provider!.Value)).ToArray());
    }

    [Fact]
    public void Open_WithProgress_Fallback_ReportsProbePerEp_ThenOpeningForWinner()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ => throw new InvalidOperationException("no CUDA"),
            [ExecutionProvider.DirectML] = path => new FakeInferenceSession($"dml:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);
        var progress = new RecordingProgress<InferenceSessionProgress>();

        using var session = factory.Open("model.onnx", progress);

        // Probing fires once per EP tried (failed + winner); OpeningSession
        // only for the EP a session was actually constructed with.
        Assert.Equal(
            new[]
            {
                (InferenceSessionPhase.ProbingProvider, ExecutionProvider.Cuda),
                (InferenceSessionPhase.ProbingProvider, ExecutionProvider.DirectML),
                (InferenceSessionPhase.OpeningSession, ExecutionProvider.DirectML),
            },
            progress.Reports.Select(r => (r.Phase, r.Provider!.Value)).ToArray());
    }

    [Fact]
    public void Open_WithProgress_AllProvidersFail_ReportsProbePerEp_NoOpening()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ => throw new InvalidOperationException("no CUDA"),
            [ExecutionProvider.DirectML] = _ => throw new DllNotFoundException("no DML"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);
        var progress = new RecordingProgress<InferenceSessionProgress>();

        Assert.Throws<InvalidOperationException>(() => factory.Open("model.onnx", progress));

        // Both EPs probed; nothing opened (no construct succeeded).
        Assert.Equal(
            new[]
            {
                (InferenceSessionPhase.ProbingProvider, ExecutionProvider.Cuda),
                (InferenceSessionPhase.ProbingProvider, ExecutionProvider.DirectML),
            },
            progress.Reports.Select(r => (r.Phase, r.Provider!.Value)).ToArray());
        Assert.DoesNotContain(
            progress.Reports,
            r => r.Phase == InferenceSessionPhase.OpeningSession);
    }

    [Fact]
    public void Open_WithProgress_CachedPath_ReportsCachedEpOnly_NoReProbe()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cuda] = _ => throw new InvalidOperationException("no CUDA"),
            [ExecutionProvider.DirectML] = path => new FakeInferenceSession($"dml:{path}"),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cuda,
            providers: providers);

        var cold = new RecordingProgress<InferenceSessionProgress>();
        using var first = factory.Open("a.onnx", cold);

        var warm = new RecordingProgress<InferenceSessionProgress>();
        using var second = factory.Open("b.onnx", warm);

        // The warm Open uses the cached EP directly: it reports the cached
        // provider's Probing → Opening pair, but does *not* re-probe the
        // failed Cuda EP (which the cold Open tried first).
        Assert.Equal(
            new[]
            {
                (InferenceSessionPhase.ProbingProvider, ExecutionProvider.DirectML),
                (InferenceSessionPhase.OpeningSession, ExecutionProvider.DirectML),
            },
            warm.Reports.Select(r => (r.Phase, r.Provider!.Value)).ToArray());
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> that records reports in call
    /// order. Unlike <see cref="System.Progress{T}"/> it does not marshal
    /// to a synchronization context, so reports are observable immediately
    /// and deterministically in tests.
    /// </summary>
    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }
}
