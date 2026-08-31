// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Yolo;

/// <summary>
/// EP-agnostic YOLOv8 detector. Owns a host-tensor input + output pair
/// (rented from a <see cref="CpuTensorPool"/>) and delegates inference
/// to any <see cref="FrameFlow.Inference.IInferenceSession"/>
/// implementation — CUDA, DirectML, future TensorRT, etc. Per
/// ADR-0049 §5, this replaces the separate
/// <c>FrameFlow.Yolo.Cuda.CudaYolov8Detector</c> and
/// <c>FrameFlow.Yolo.Dml.DmlYolov8Detector</c>; the EP choice is now a
/// composition concern at the call site.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction.</b> Callers build the appropriate
/// <see cref="FrameFlow.Inference.IInferenceSession"/> (e.g.
/// <c>new FrameFlow.Inference.Cuda.CudaInferenceSession(modelPath)</c>
/// or <c>new FrameFlow.Inference.Dml.DmlInferenceSession(modelPath)</c>)
/// and pass it to <see cref="CreateAsync"/>. The detector takes
/// ownership; <see cref="Dispose"/> tears down the session.
/// </para>
/// <para>
/// <b>Hot path.</b> Per frame: preprocess (writes pixels into the input
/// tensor's bytes), <see cref="FrameFlow.Inference.IInferenceSession.Run"/>
/// (EP stages internally), postprocess (reads detections from the
/// output tensor's bytes). No explicit host↔device transfers — the EP
/// handles staging per ADR-0049 §3.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> A single detector instance is for sequential
/// use. Multi-consumer workloads instantiate per-consumer detectors.
/// </para>
/// </remarks>
public sealed partial class Yolov8Detector : IDisposable
{
    private readonly FrameFlow.Inference.IInferenceSession _session;
    private readonly CpuTensorPool _pool;
    private readonly CpuTensor<float> _inputTensor;
    private readonly CpuTensor<float> _outputTensor;
    private readonly Yolov8Preprocessor _preprocessor;
    private readonly Yolov8Postprocessor _postprocessor;
    private readonly ILogger<Yolov8Detector> _logger;
    private bool _disposed;

    // Per-stage wall-clock timing of the most recent Detect() call.
    // Volatile so a UI / diagnostics thread can poll without a lock.
    // NaN until the first detection completes.
    private double _lastPreprocessMs = double.NaN;
    private double _lastInferenceMs = double.NaN;
    private double _lastPostprocessMs = double.NaN;

    private Yolov8Detector(
        FrameFlow.Inference.IInferenceSession session,
        CpuTensorPool pool,
        CpuTensor<float> inputTensor,
        CpuTensor<float> outputTensor,
        Yolov8Preprocessor preprocessor,
        Yolov8Postprocessor postprocessor,
        ILogger<Yolov8Detector> logger
    )
    {
        _session = session;
        _pool = pool;
        _inputTensor = inputTensor;
        _outputTensor = outputTensor;
        _preprocessor = preprocessor;
        _postprocessor = postprocessor;
        _logger = logger;
    }

    /// <summary>
    /// Builds a YOLOv8 detector around the supplied inference session.
    /// The detector takes ownership of <paramref name="session"/> —
    /// disposing the detector disposes the session.
    /// </summary>
    /// <param name="session">EP-specific ORT session that runs the model.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="descriptor">Optional explicit model shape; auto-inferred from the session when null.</param>
    /// <param name="classFilter">Optional set of class ids to keep; null keeps all.</param>
    /// <param name="progress">
    /// Optional reporter for load sub-phases. <see cref="Create"/> reports
    /// <see cref="FrameFlow.Inference.InferenceSessionPhase.Warmup"/> around
    /// the warmup inference. <c>null</c> ⇒ identical to the previous
    /// behaviour (no reporting).
    /// </param>
    public static Yolov8Detector Create(
        FrameFlow.Inference.IInferenceSession session,
        ILoggerFactory? loggerFactory = null,
        YoloModelDescriptor? descriptor = null,
        IReadOnlyCollection<int>? classFilter = null,
        IProgress<FrameFlow.Inference.InferenceSessionProgress>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var log = factory.CreateLogger<Yolov8Detector>();

        // Auto-infer the model shape from the session when the caller didn't
        // pin one (ADR-0050 §2). Stock 640 / 80-COCO models resolve to the
        // shape the detector used before; smaller-input and reduced-class
        // models self-configure. Throws loudly on a head we can't read
        // (ADR-0050 §5).
        var shape = descriptor ?? YoloModelDescriptor.FromSession(session);
        LogModelShape(log, shape.InputSize, shape.ClassCount, shape.AnchorCount);

        CpuTensorPool? pool = null;
        CpuTensor<float>? inputTensor = null;
        CpuTensor<float>? outputTensor = null;
        try
        {
            pool = new CpuTensorPool();
            inputTensor = pool.Rent<float>(
                new TensorShape(1, 3, shape.InputSize, shape.InputSize)
            );
            outputTensor = pool.Rent<float>(
                new TensorShape(1, shape.OutputChannelCount, shape.AnchorCount)
            );

            var detector = new Yolov8Detector(
                session,
                pool,
                inputTensor,
                outputTensor,
                new Yolov8Preprocessor(shape.InputSize),
                new Yolov8Postprocessor(shape, classFilter),
                log
            );
            // Ownership of pool/session/tensors has transferred to detector.
            // Null the locals so the outer `finally` doesn't double-dispose.
            session = null!;
            pool = null;
            inputTensor = null;
            outputTensor = null;

            // Warm up the inference session before returning. The first
            // session.Run() against a fresh ORT-EP session is *expensive*:
            //
            //   * ORT-CUDA: cuDNN tactic search + per-op JIT compile +
            //     cuBLAS handle init. 1-3 seconds on cold cache, ~250 ms
            //     when the kernel cache is warm. Bypasses GPU pacing.
            //   * ORT-DML : D3D12 PSO compile + DML operator graph build.
            //     ~100-300 ms typical; less visible but still real.
            //   * ORT-TensorRT (future): engine build can dominate at
            //     model load; a warmup Run() also flushes per-shape JIT.
            //
            // Running an inference here moves that cost into construction
            // — where the caller is already in "loading models…" mode —
            // instead of paying it on the first real frame. Without
            // warmup, multi-pane decode-rate demos (Multicast) exhibit a
            // visible "pane 2 stays black for ~2s while panes 1/3 race
            // ahead" symptom on cold start, because pane 2's first
            // Detect() inherits the full JIT latency. With warmup the
            // first real Detect() is fast (cached) and all panes track
            // together.
            //
            // Wrapped in try/catch so a Warmup failure (e.g., model and
            // EP incompatible in some way the session-construct path
            // didn't catch) doesn't leak detector resources.
            try
            {
                progress?.Report(new FrameFlow.Inference.InferenceSessionProgress(
                    FrameFlow.Inference.InferenceSessionPhase.Warmup));
                detector.Warmup();
            }
            catch
            {
                detector.Dispose();
                throw;
            }
            return detector;
        }
        finally
        {
            inputTensor?.Dispose();
            outputTensor?.Dispose();
            pool?.Dispose();
            session?.Dispose();
        }
    }

    /// <summary>
    /// Runs one inference on the pre-allocated input / output tensors
    /// to absorb the ORT execution provider's cold-start cost (CUDA
    /// JIT + cuDNN tactic search, DML PSO compile, TensorRT per-shape
    /// JIT) before the first real frame arrives.
    /// </summary>
    /// <remarks>
    /// Called automatically from <see cref="Create"/>; consumers
    /// don't normally invoke it directly. Tensor contents are
    /// whatever <see cref="CpuTensorPool"/> returned (stale or zero)
    /// — the cold-start work is driven by input *shape*, not content,
    /// so the Run is meaningful even with arbitrary data.
    /// </remarks>
    public void Warmup()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();
        _session.Run(_inputTensor, _outputTensor);
        sw.Stop();
        LogWarmupCompleted(_logger, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Builds a detector from a model path and a
    /// <see cref="FrameFlow.Inference.IInferenceSessionFactory"/>.
    /// Honours the factory's EP preference + fallback chain (the factory's
    /// first <c>Open()</c> call drives EP selection; observable via
    /// <see cref="FrameFlow.Inference.IInferenceSessionFactory.ActiveProvider"/>).
    /// </summary>
    /// <param name="factory">EP-resolving session factory.</param>
    /// <param name="overrideModelPath">Optional path override; if null, downloads.</param>
    /// <param name="ct">Cancellation token for the model download.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="descriptor">Optional explicit model shape; auto-inferred from the session when null.</param>
    /// <param name="classFilter">Optional set of class ids to keep; null keeps all.</param>
    /// <param name="progress">
    /// Optional reporter for load sub-phases. Threaded into
    /// <see cref="FrameFlow.Inference.IInferenceSessionFactory.Open(string, IProgress{FrameFlow.Inference.InferenceSessionProgress})"/>
    /// (which reports EP probe / session-open phases) and into the warmup
    /// phase. <c>null</c> ⇒ identical to the previous behaviour.
    /// </param>
    public static Task<Yolov8Detector> CreateAsync(
        FrameFlow.Inference.IInferenceSessionFactory factory,
        string? overrideModelPath = null,
        CancellationToken ct = default,
        ILoggerFactory? loggerFactory = null,
        YoloModelDescriptor? descriptor = null,
        IReadOnlyCollection<int>? classFilter = null,
        IProgress<FrameFlow.Inference.InferenceSessionProgress>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        return CreateAsync(
            sessionFactory: path => factory.Open(path, progress),
            overrideModelPath: overrideModelPath,
            ct: ct,
            loggerFactory: loggerFactory,
            descriptor: descriptor,
            classFilter: classFilter,
            progress: progress);
    }

    /// <summary>
    /// Builds a detector from a model path and a session factory.
    /// Downloads the YOLOv8 model if not cached locally and overridden.
    /// </summary>
    /// <param name="sessionFactory">
    /// Function that constructs the EP-specific inference session given
    /// a model path. Typical: <c>path => new CudaInferenceSession(path)</c>
    /// or <c>path => new DmlInferenceSession(path)</c>. New consumers
    /// should prefer the
    /// <see cref="CreateAsync(FrameFlow.Inference.IInferenceSessionFactory, string?, CancellationToken, ILoggerFactory?)"/>
    /// overload, which centralises EP selection and fallback.
    /// </param>
    /// <param name="overrideModelPath">Optional path override; if null, downloads.</param>
    /// <param name="ct">Cancellation token for the model download.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="descriptor">Optional explicit model shape; auto-inferred from the session when null.</param>
    /// <param name="classFilter">Optional set of class ids to keep; null keeps all.</param>
    /// <param name="progress">
    /// Optional reporter for the warmup sub-phase (see
    /// <see cref="FrameFlow.Inference.InferenceSessionPhase.Warmup"/>).
    /// EP probe / session-open phases are the supplied
    /// <paramref name="sessionFactory"/>'s concern in this overload — pass
    /// the <see cref="IInferenceSessionFactory"/> overload to get those
    /// reported too. <c>null</c> ⇒ identical to the previous behaviour.
    /// </param>
    public static async Task<Yolov8Detector> CreateAsync(
        Func<string, FrameFlow.Inference.IInferenceSession> sessionFactory,
        string? overrideModelPath = null,
        CancellationToken ct = default,
        ILoggerFactory? loggerFactory = null,
        YoloModelDescriptor? descriptor = null,
        IReadOnlyCollection<int>? classFilter = null,
        IProgress<FrameFlow.Inference.InferenceSessionProgress>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var log = factory.CreateLogger<Yolov8Detector>();

        string modelPath;
        if (overrideModelPath is not null)
        {
            modelPath = overrideModelPath;
            LogUsingOverrideModel(log, modelPath);
        }
        else
        {
            modelPath = await Yolov8ModelDownloader
                .EnsureModelAvailableAsync(ct, logger: log)
                .ConfigureAwait(false);
        }

        var sessionWatch = Stopwatch.StartNew();
        LogSessionConstructionStarted(log, modelPath);
        FrameFlow.Inference.IInferenceSession session;
        try
        {
            session = sessionFactory(modelPath);
        }
        catch (Exception ex)
        {
            LogSessionConstructionFailed(log, modelPath, ex);
            throw;
        }
        sessionWatch.Stop();
        LogSessionConstructed(log, sessionWatch.Elapsed.TotalMilliseconds);

        return Create(session, factory, descriptor, classFilter, progress);
    }

    /// <summary>
    /// Runs detection on a single video frame. Not thread-safe.
    /// </summary>
    /// <remarks>
    /// The three stages are timed separately. Only
    /// <see cref="FrameFlow.Inference.IInferenceSession.Run"/> executes on
    /// the EP device; <c>Preprocess</c> (pixel resize + NCHW layout into
    /// the input tensor) and <c>Decode</c> (NMS over the candidate boxes)
    /// are CPU work. The split is what distinguishes a GPU-bound host from
    /// a CPU-bound one; profiling an Intel HD 620 under DirectML is what
    /// motivated this instrumentation. Per-stage values are
    /// exposed via <see cref="LastPreprocessMs"/> /
    /// <see cref="LastInferenceMs"/> / <see cref="LastPostprocessMs"/> and
    /// emitted on a Debug-level log line.
    /// </remarks>
    public List<Detection> Detect(IVideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Preprocess writes directly into the input tensor's Span<float>.
        // No host→device staging — the EP handles that internally.
        var swPre = Stopwatch.StartNew();
        var (scaleX, scaleY) = _preprocessor.Preprocess(frame, _inputTensor.Span);
        swPre.Stop();

        var swRun = Stopwatch.StartNew();
        _session.Run(_inputTensor, _outputTensor);
        swRun.Stop();

        var swPost = Stopwatch.StartNew();
        var detections = _postprocessor.Decode(_outputTensor.ReadOnlySpan, scaleX, scaleY);
        swPost.Stop();

        var preMs = swPre.Elapsed.TotalMilliseconds;
        var runMs = swRun.Elapsed.TotalMilliseconds;
        var postMs = swPost.Elapsed.TotalMilliseconds;
        Volatile.Write(ref _lastPreprocessMs, preMs);
        Volatile.Write(ref _lastInferenceMs, runMs);
        Volatile.Write(ref _lastPostprocessMs, postMs);
        LogDetectionBreakdown(
            _logger,
            detections.Count,
            preMs,
            runMs,
            postMs,
            preMs + runMs + postMs
        );
        return detections;
    }

    /// <summary>
    /// Wall-clock duration (ms) of the preprocess stage of the most recent
    /// <see cref="Detect"/> call. CPU work. <see cref="double.NaN"/> before
    /// the first detection. Volatile read — safe to poll cross-thread.
    /// </summary>
    public double LastPreprocessMs => Volatile.Read(ref _lastPreprocessMs);

    /// <summary>
    /// Wall-clock duration (ms) of the inference stage
    /// (<see cref="FrameFlow.Inference.IInferenceSession.Run"/>) of the most
    /// recent <see cref="Detect"/> call. The only stage that runs on the EP
    /// device. <see cref="double.NaN"/> before the first detection.
    /// </summary>
    public double LastInferenceMs => Volatile.Read(ref _lastInferenceMs);

    /// <summary>
    /// Wall-clock duration (ms) of the postprocess (NMS decode) stage of the
    /// most recent <see cref="Detect"/> call. CPU work.
    /// <see cref="double.NaN"/> before the first detection.
    /// </summary>
    public double LastPostprocessMs => Volatile.Read(ref _lastPostprocessMs);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inputTensor.Dispose();
        _outputTensor.Dispose();
        _session.Dispose();
        _pool.Dispose();
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Using override model path: {ModelPath}"
    )]
    private static partial void LogUsingOverrideModel(ILogger logger, string modelPath);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Constructing ORT inference session from {ModelPath}…"
    )]
    private static partial void LogSessionConstructionStarted(ILogger logger, string modelPath);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ORT inference session ready ({ElapsedMs:F0} ms)"
    )]
    private static partial void LogSessionConstructed(ILogger logger, double elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ORT inference session construction failed: model={ModelPath}"
    )]
    private static partial void LogSessionConstructionFailed(
        ILogger logger,
        string modelPath,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Detect: {DetectionCount} object(s) | pre {PreprocessMs:F1} ms + run {InferenceMs:F1} ms + post {PostprocessMs:F1} ms = {TotalMs:F1} ms"
    )]
    private static partial void LogDetectionBreakdown(
        ILogger logger,
        int detectionCount,
        double preprocessMs,
        double inferenceMs,
        double postprocessMs,
        double totalMs
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "YOLOv8 detector warmup completed in {ElapsedMs:F0} ms (ORT-EP cold-start cost absorbed before first real Detect())"
    )]
    private static partial void LogWarmupCompleted(ILogger logger, double elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "YOLO model shape: input {InputSize}px, {ClassCount} class(es), {AnchorCount} anchors"
    )]
    private static partial void LogModelShape(
        ILogger logger,
        int inputSize,
        int classCount,
        int anchorCount
    );
}
