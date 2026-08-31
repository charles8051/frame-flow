// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Face;

/// <summary>
/// EP-agnostic BlazeFace detector. Owns a host-tensor input plus the two
/// host-tensor outputs BlazeFace produces (box regressors + scores),
/// rented from a <see cref="CpuTensorPool"/>, and delegates inference to
/// any <see cref="FrameFlow.Inference.IInferenceSession"/> — CUDA, DML,
/// future TensorRT. The face analogue of
/// <c>FrameFlow.Yolo.Yolov8Detector</c>; the EP choice is a composition
/// concern at the call site (ADR-0049 §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two outputs.</b> Unlike the single-head YOLO detector, BlazeFace
/// binds two output tensors via the dictionary-form
/// <see cref="FrameFlow.Inference.IInferenceSession.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>.
/// Which output is boxes vs scores is resolved by shape (via
/// <see cref="BlazeFaceModelDescriptor.IdentifyOutputs"/>), not
/// declaration order, since community ONNX exports disagree.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> One detector is for sequential use; the gaze
/// seam runs a single instance over the <c>PRESENT</c> candidate crops.
/// </para>
/// </remarks>
public sealed partial class BlazeFaceDetector : IDisposable
{
    private readonly FrameFlow.Inference.IInferenceSession _session;
    private readonly BlazeFaceModelDescriptor _descriptor;
    private readonly CpuTensorPool _pool;
    private readonly CpuTensor<float> _inputTensor;
    private readonly CpuTensor<float> _boxTensor;
    private readonly CpuTensor<float> _scoreTensor;
    private readonly string _inputName;
    private readonly string _boxName;
    private readonly string _scoreName;
    private readonly BlazeFacePreprocessor _preprocessor;
    private readonly BlazeFacePostprocessor _postprocessor;
    private readonly ILogger<BlazeFaceDetector> _logger;

    // Reused per-call binding dictionaries — the tensors they point at are
    // fixed for the detector's lifetime, so there's no per-frame alloc.
    private readonly Dictionary<string, ICpuTensor> _inputBinding;
    private readonly Dictionary<string, ICpuTensor> _outputBinding;

    private bool _disposed;

    private double _lastPreprocessMs = double.NaN;
    private double _lastInferenceMs = double.NaN;
    private double _lastPostprocessMs = double.NaN;

    private BlazeFaceDetector(
        FrameFlow.Inference.IInferenceSession session,
        BlazeFaceModelDescriptor descriptor,
        CpuTensorPool pool,
        CpuTensor<float> inputTensor,
        CpuTensor<float> boxTensor,
        CpuTensor<float> scoreTensor,
        string inputName,
        string boxName,
        string scoreName,
        BlazeFacePreprocessor preprocessor,
        BlazeFacePostprocessor postprocessor,
        ILogger<BlazeFaceDetector> logger)
    {
        _session = session;
        _descriptor = descriptor;
        _pool = pool;
        _inputTensor = inputTensor;
        _boxTensor = boxTensor;
        _scoreTensor = scoreTensor;
        _inputName = inputName;
        _boxName = boxName;
        _scoreName = scoreName;
        _preprocessor = preprocessor;
        _postprocessor = postprocessor;
        _logger = logger;

        _inputBinding = new Dictionary<string, ICpuTensor> { [_inputName] = _inputTensor };
        _outputBinding = new Dictionary<string, ICpuTensor>
        {
            [_boxName] = _boxTensor,
            [_scoreName] = _scoreTensor,
        };
    }

    /// <summary>
    /// Builds a BlazeFace detector around the supplied inference session.
    /// Takes ownership of <paramref name="session"/> — disposing the
    /// detector disposes the session. Validates the session's I/O against
    /// <paramref name="descriptor"/> (default: the front-128 model) and
    /// warms up the EP before returning.
    /// </summary>
    public static BlazeFaceDetector Create(
        FrameFlow.Inference.IInferenceSession session,
        ILoggerFactory? loggerFactory = null,
        BlazeFaceModelDescriptor? descriptor = null,
        BlazeFacePostprocessor? postprocessor = null,
        IProgress<FrameFlow.Inference.InferenceSessionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var log = factory.CreateLogger<BlazeFaceDetector>();

        CpuTensorPool? pool = null;
        CpuTensor<float>? inputTensor = null;
        CpuTensor<float>? boxTensor = null;
        CpuTensor<float>? scoreTensor = null;
        try
        {
            // Resolve + validate inside the guarded region so a shape mismatch
            // disposes the session Create took ownership of, rather than
            // leaking it. FromSession auto-detects the input layout (NCHW vs
            // the MediaPipe-native NHWC) and validates the two outputs.
            var shape = descriptor;
            if (shape is null)
                shape = BlazeFaceModelDescriptor.FromSession(session);
            else
                shape.ValidateSession(session);

            var (boxIdx, scoreIdx) = shape.IdentifyOutputs(session.OutputShapes);
            string inputName = session.InputNames[0];
            string boxName = session.OutputNames[boxIdx];
            string scoreName = session.OutputNames[scoreIdx];
            LogModelShape(log, shape.InputSize, shape.NumBoxes, shape.NumKeypoints, shape.InputLayout);

            pool = new CpuTensorPool();
            inputTensor = pool.Rent<float>(InputTensorShape(shape));
            boxTensor = pool.Rent<float>(new TensorShape(1, shape.NumBoxes, shape.NumCoords));
            scoreTensor = pool.Rent<float>(new TensorShape(1, shape.NumBoxes, 1));

            var detector = new BlazeFaceDetector(
                session,
                shape,
                pool,
                inputTensor,
                boxTensor,
                scoreTensor,
                inputName,
                boxName,
                scoreName,
                new BlazeFacePreprocessor(shape.InputSize, shape.InputLayout),
                postprocessor ?? new BlazeFacePostprocessor(shape),
                log);

            // Ownership transferred; null the locals so the finally doesn't double-dispose.
            session = null!;
            pool = null;
            inputTensor = null;
            boxTensor = null;
            scoreTensor = null;

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
            boxTensor?.Dispose();
            scoreTensor?.Dispose();
            pool?.Dispose();
            session?.Dispose();
        }
    }

    /// <summary>
    /// Builds a detector from a model path and a session factory.
    /// BlazeFace ships no runtime downloader (ADR-0051): the caller passes
    /// its bundled / pre-seeded model path.
    /// </summary>
    public static async Task<BlazeFaceDetector> CreateAsync(
        FrameFlow.Inference.IInferenceSessionFactory factory,
        string modelPath,
        ILoggerFactory? loggerFactory = null,
        BlazeFaceModelDescriptor? descriptor = null,
        BlazeFacePostprocessor? postprocessor = null,
        IProgress<FrameFlow.Inference.InferenceSessionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        var loggers = loggerFactory ?? NullLoggerFactory.Instance;
        var log = loggers.CreateLogger<BlazeFaceDetector>();

        var sw = Stopwatch.StartNew();
        LogSessionConstructionStarted(log, modelPath);
        FrameFlow.Inference.IInferenceSession session;
        try
        {
            session = factory.Open(modelPath, progress);
        }
        catch (Exception ex)
        {
            LogSessionConstructionFailed(log, modelPath, ex);
            throw;
        }
        sw.Stop();
        LogSessionConstructed(log, sw.Elapsed.TotalMilliseconds);

        // Create() is CPU/GPU work (validation + warmup); keep the async
        // signature uniform with the YOLO detector without blocking a pool
        // thread on it.
        return await Task.FromResult(
            Create(session, loggers, descriptor, postprocessor, progress)).ConfigureAwait(false);
    }

    private static TensorShape InputTensorShape(BlazeFaceModelDescriptor shape)
        => shape.InputLayout == BlazeFaceInputLayout.Nchw
            ? new TensorShape(1, 3, shape.InputSize, shape.InputSize)
            : new TensorShape(1, shape.InputSize, shape.InputSize, 3);

    /// <summary>Runs one inference to absorb the EP's cold-start cost before the first real frame.</summary>
    public void Warmup()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();
        _session.Run(_inputBinding, _outputBinding);
        sw.Stop();
        LogWarmupCompleted(_logger, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Detects faces inside <paramref name="roi"/> of <paramref name="frame"/>.
    /// Pass <see cref="FaceRoi.Full"/> for whole-frame detection, or the
    /// tracked person box to search only where a face can be. Not
    /// thread-safe.
    /// </summary>
    public List<FaceDetection> Detect(IVideoFrame frame, FaceRoi roi)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var swPre = Stopwatch.StartNew();
        _preprocessor.Preprocess(frame, roi, _inputTensor.Span);
        swPre.Stop();

        var swRun = Stopwatch.StartNew();
        _session.Run(_inputBinding, _outputBinding);
        swRun.Stop();

        var swPost = Stopwatch.StartNew();
        var faces = _postprocessor.Decode(_boxTensor.ReadOnlySpan, _scoreTensor.ReadOnlySpan, roi);
        swPost.Stop();

        double preMs = swPre.Elapsed.TotalMilliseconds;
        double runMs = swRun.Elapsed.TotalMilliseconds;
        double postMs = swPost.Elapsed.TotalMilliseconds;
        Volatile.Write(ref _lastPreprocessMs, preMs);
        Volatile.Write(ref _lastInferenceMs, runMs);
        Volatile.Write(ref _lastPostprocessMs, postMs);
        LogDetectionBreakdown(_logger, faces.Count, preMs, runMs, postMs, preMs + runMs + postMs);
        return faces;
    }

    /// <summary>Whole-frame convenience overload.</summary>
    public List<FaceDetection> Detect(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Detect(frame, FaceRoi.Full(frame));
    }

    /// <summary>Wall-clock ms of the preprocess stage of the most recent <see cref="Detect(IVideoFrame, FaceRoi)"/>. CPU work.</summary>
    public double LastPreprocessMs => Volatile.Read(ref _lastPreprocessMs);

    /// <summary>Wall-clock ms of the inference stage (the only EP-device stage) of the most recent detect.</summary>
    public double LastInferenceMs => Volatile.Read(ref _lastInferenceMs);

    /// <summary>Wall-clock ms of the postprocess (decode + NMS) stage of the most recent detect. CPU work.</summary>
    public double LastPostprocessMs => Volatile.Read(ref _lastPostprocessMs);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inputTensor.Dispose();
        _boxTensor.Dispose();
        _scoreTensor.Dispose();
        _session.Dispose();
        _pool.Dispose();
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Constructing ORT inference session from {ModelPath}…")]
    private static partial void LogSessionConstructionStarted(ILogger logger, string modelPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "ORT inference session ready ({ElapsedMs:F0} ms)")]
    private static partial void LogSessionConstructed(ILogger logger, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "ORT inference session construction failed: model={ModelPath}")]
    private static partial void LogSessionConstructionFailed(ILogger logger, string modelPath, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "DetectFaces: {FaceCount} face(s) | pre {PreprocessMs:F1} ms + run {InferenceMs:F1} ms + post {PostprocessMs:F1} ms = {TotalMs:F1} ms")]
    private static partial void LogDetectionBreakdown(
        ILogger logger, int faceCount, double preprocessMs, double inferenceMs, double postprocessMs, double totalMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "BlazeFace detector warmup completed in {ElapsedMs:F0} ms (ORT-EP cold-start cost absorbed before first real Detect())")]
    private static partial void LogWarmupCompleted(ILogger logger, double elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "BlazeFace model shape: input {InputSize}px {Layout}, {NumBoxes} boxes, {NumKeypoints} keypoints")]
    private static partial void LogModelShape(
        ILogger logger, int inputSize, int numBoxes, int numKeypoints, BlazeFaceInputLayout layout);
}
