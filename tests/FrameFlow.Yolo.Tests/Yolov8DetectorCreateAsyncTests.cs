using FrameFlow.Inference;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// <see cref="Yolov8Detector.CreateAsync(Func{string, IInferenceSession}, string, CancellationToken, Microsoft.Extensions.Logging.ILoggerFactory, YoloModelDescriptor, IReadOnlyCollection{int}, IProgress{InferenceSessionProgress}, YoloThresholds)"/>
/// opens the session and warms it up off the caller's thread, and a token cancelled before the
/// open opens nothing (#626).
/// </summary>
/// <remarks>
/// The session factory waits on a gate the test opens only after <c>CreateAsync</c> has returned,
/// so the task it returns is still running when the test looks at it. The factory and the warmup
/// run also refuse to run on the caller, which <see cref="CallerContext"/> marks: inside the call,
/// on the caller's thread, or posted back to its synchronization context. Work done there throws
/// instead of waiting on a gate that would never open, so a regression fails the test rather than
/// hanging it.
/// </remarks>
public sealed class Yolov8DetectorCreateAsyncTests
{
    [Fact]
    public async Task CreateAsync_FromSessionFactory_ReturnsBeforeTheSessionOpens()
    {
        using var gate = new SemaphoreSlim(0);
        var session = new CallerCheckingSession();

        var creating = CallerContext.Call(() => Yolov8Detector.CreateAsync(
            sessionFactory: _ =>
            {
                CallerContext.ThrowIfOnTheCaller("The session factory");
                gate.Wait();
                return session;
            },
            overrideModelPath: "model.onnx"));

        CallerContext.AssertRunningUntilTheGateOpens(creating, gate);
        using var detector = await creating;
        Assert.Equal(1, session.RunCount);
    }

    [Fact]
    public async Task CreateAsync_FromFactory_ReturnsBeforeTheSessionOpens()
    {
        using var gate = new SemaphoreSlim(0);
        var session = new CallerCheckingSession();
        var factory = InferenceSessionFactoryBuilder.Create(
            ExecutionProvider.Cpu,
            new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
            {
                [ExecutionProvider.Cpu] = _ =>
                {
                    CallerContext.ThrowIfOnTheCaller("The factory's open");
                    gate.Wait();
                    return session;
                },
            });

        var creating = CallerContext.Call(() => Yolov8Detector.CreateAsync(factory, overrideModelPath: "model.onnx"));

        CallerContext.AssertRunningUntilTheGateOpens(creating, gate);
        using var detector = await creating;
        Assert.Equal(1, session.RunCount);
    }

    [Fact]
    public async Task CreateAsync_WithACancelledToken_OpensNoSession()
    {
        int opens = 0;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Yolov8Detector.CreateAsync(
            sessionFactory: _ =>
            {
                Interlocked.Increment(ref opens);
                return new CallerCheckingSession();
            },
            overrideModelPath: "model.onnx",
            ct: cancelled.Token));

        Assert.Equal(0, opens);
    }

    /// <summary>
    /// A stock-YOLOv8-shaped session with a no-op <c>Run</c> that refuses to run on the caller,
    /// where the detector's warmup would block it.
    /// </summary>
    private sealed class CallerCheckingSession : IInferenceSession
    {
        private int _runs;

        public int RunCount => Volatile.Read(ref _runs);

        public IReadOnlyList<string> InputNames { get; } = ["images"];

        public IReadOnlyList<string> OutputNames { get; } = ["output0"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 640, 640]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 84, 8400]];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            CallerContext.ThrowIfOnTheCaller("The warmup");
            Interlocked.Increment(ref _runs);
        }

        public void Dispose()
        {
        }
    }
}
