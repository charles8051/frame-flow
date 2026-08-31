using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// Verifies the opt-in <see cref="InferenceSessionProgress"/> reporter is
/// threaded through <see cref="Yolov8Detector.CreateAsync"/>: the detector
/// reports <see cref="InferenceSessionPhase.Warmup"/> around its warmup
/// inference, and the factory-backed overload surfaces the full
/// probe → open → warmup sequence. A no-op fake session stands in for a
/// real ORT-EP session so the test needs neither a model file nor native
/// runtime.
/// </summary>
public sealed class Yolov8DetectorProgressTests
{
    [Fact]
    public async Task CreateAsync_FromSessionFactory_WithProgress_ReportsWarmup()
    {
        var progress = new RecordingProgress<InferenceSessionProgress>();
        var session = new WarmupCountingSession();

        using var detector = await Yolov8Detector.CreateAsync(
            sessionFactory: _ => session,
            overrideModelPath: "model.onnx",
            progress: progress);

        // The session-factory-delegate overload does not report EP probe /
        // session-open phases (that is the IInferenceSessionFactory
        // overload's concern). The only phase Create itself reports is the
        // warmup it drives.
        Assert.Equal(
            new[] { InferenceSessionPhase.Warmup },
            progress.Reports.Select(r => r.Phase).ToArray());
        // Warmup actually ran the session once.
        Assert.Equal(1, session.RunCount);
    }

    [Fact]
    public async Task CreateAsync_FromFactory_WithProgress_ReportsProbeOpenWarmupInOrder()
    {
        var providers = new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
        {
            [ExecutionProvider.Cpu] = _ => new WarmupCountingSession(),
        };
        var factory = InferenceSessionFactoryBuilder.Create(
            preferred: ExecutionProvider.Cpu,
            providers: providers);
        var progress = new RecordingProgress<InferenceSessionProgress>();

        using var detector = await Yolov8Detector.CreateAsync(
            factory,
            overrideModelPath: "model.onnx",
            progress: progress);

        // End-to-end: the factory reports the EP probe + session-open, then
        // the detector reports warmup — in that order.
        Assert.Equal(
            new[]
            {
                InferenceSessionPhase.ProbingProvider,
                InferenceSessionPhase.OpeningSession,
                InferenceSessionPhase.Warmup,
            },
            progress.Reports.Select(r => r.Phase).ToArray());
        // Probe + open carry the selected EP; warmup carries none (Create
        // sees only the session, not the factory's ActiveProvider).
        Assert.Equal(ExecutionProvider.Cpu, progress.Reports[0].Provider);
        Assert.Equal(ExecutionProvider.Cpu, progress.Reports[1].Provider);
        Assert.Null(progress.Reports[2].Provider);
        Assert.Equal(ExecutionProvider.Cpu, factory.ActiveProvider);
    }

    [Fact]
    public async Task CreateAsync_NullProgress_Succeeds()
    {
        // No progress arg ⇒ the pre-existing path; must still build a detector.
        using var detector = await Yolov8Detector.CreateAsync(
            sessionFactory: _ => new WarmupCountingSession(),
            overrideModelPath: "model.onnx");

        Assert.NotNull(detector);
    }

    /// <summary>
    /// Minimal <see cref="IInferenceSession"/> with a no-op <c>Run</c> and a
    /// valid stock-YOLOv8 head shape so <c>YoloModelDescriptor.FromSession</c>
    /// and the detector's warmup both succeed without a real model.
    /// </summary>
    private sealed class WarmupCountingSession : IInferenceSession
    {
        public int RunCount { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<string> InputNames { get; } = ["images"];

        public IReadOnlyList<string> OutputNames { get; } = ["output0"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } =
            [[1, 3, 640, 640]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } =
            [[1, 84, 8400]];

        public void Run(
            IReadOnlyDictionary<string, ICpuTensor> inputs,
            IReadOnlyDictionary<string, ICpuTensor> outputs) => RunCount++;

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> recording reports in call
    /// order — no synchronization-context marshalling, so reports are
    /// observable immediately in tests.
    /// </summary>
    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }
}
