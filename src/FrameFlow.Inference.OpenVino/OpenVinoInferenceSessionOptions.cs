// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.OpenVino.Core;

namespace FrameFlow.Inference.OpenVino;

/// <summary>How an <see cref="OpenVinoInferenceSession"/> configures OpenVINO.</summary>
/// <remarks>
/// The defaults are the ones docs/investigations/2026-09-29-openvino-ep-on-intel-igpu.md measured
/// on a Gen9 Intel GPU.
/// </remarks>
public sealed record OpenVinoInferenceSessionOptions
{
    private static readonly string? DefaultCacheDirectory =
        OpenVinoProviderOptions.CacheDirectoryUnder(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private readonly string _device = "GPU";
    private readonly string? _cacheDirectory = DefaultCacheDirectory;
    private readonly OpenVinoQueueThrottle? _queueThrottle = OpenVinoQueueThrottle.Low;
    private readonly string? _precisionHint;

    /// <summary>
    /// The OpenVINO device the model compiles for: <c>GPU</c> by default, or <c>CPU</c> or
    /// <c>NPU</c>. An index chooses among several of a kind, as in <c>GPU.1</c>; <c>GPU</c> is
    /// OpenVINO's first.
    /// </summary>
    /// <remarks>
    /// The device suits the model, not the app: on the machine the investigation measured, yolov8n
    /// ran fastest on <c>GPU</c> and BlazeFace on <c>CPU</c>. The meta devices (<c>AUTO</c>,
    /// <c>HETERO</c>, <c>MULTI</c>) are not accepted.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value is not <c>CPU</c>, <c>GPU</c> or <c>NPU</c>, alone or with an index.
    /// </exception>
    public string Device
    {
        get => _device;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!OpenVinoProviderOptions.IsDevice(value))
            {
                throw new ArgumentException(
                    $"'{value}' is not an OpenVINO device this session takes: CPU, GPU or NPU, alone or with an index such as GPU.1.",
                    nameof(value));
            }

            _device = value;
        }
    }

    /// <summary>
    /// Where OpenVINO keeps compiled models, so a model it has compiled before opens from the blob
    /// (<c>CACHE_DIR</c>). Defaults to <c>FrameFlow.Inference.OpenVino\cache</c> under the user's
    /// local application data; <see langword="null"/> turns the cache off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it every open pays the full compile: 16 s for a YOLO model on the GPU, against about
    /// 4 s from the cache. The session creates the directory when it opens.
    /// </para>
    /// <para>
    /// OpenVINO holds a blob open until the process exits, so deleting the directory from the process
    /// that used it fails with access denied.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or blank.</exception>
    public string? CacheDirectory
    {
        get => _cacheDirectory;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A cache directory must not be empty; null turns the cache off.", nameof(value));
            _cacheDirectory = value;
        }
    }

    /// <summary>
    /// The GPU plugin's queue throttle, <see cref="OpenVinoQueueThrottle.Low"/> by default;
    /// <see langword="null"/> leaves OpenVINO's own. Sent only when <see cref="Device"/> is a GPU.
    /// </summary>
    /// <remarks>
    /// Unthrottled, the investigation's GPU session held a CPU thread for most of each run: 13.0 ms of
    /// CPU per run, against 5.1 ms at <see cref="OpenVinoQueueThrottle.Low"/>, with no rise in
    /// latency.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined level.</exception>
    public OpenVinoQueueThrottle? QueueThrottle
    {
        get => _queueThrottle;
        init
        {
            if (value is { } level && !Enum.IsDefined(level))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined throttle level.");
            _queueThrottle = value;
        }
    }

    /// <summary>
    /// The precision OpenVINO computes in (<c>INFERENCE_PRECISION_HINT</c>), such as <c>f32</c>;
    /// <see langword="null"/>, the default, leaves the device's own. The value goes to OpenVINO as
    /// given.
    /// </summary>
    /// <remarks>
    /// The GPU plugin computes in f16 by default, whatever the model's weights, and that is most of
    /// its speed: <c>f32</c> took yolov8n at 640 from 41.3 ms to 65.3 ms on the Gen9 GPU. Computing
    /// in f16 moves the output about as much as DirectML does on an fp16-weight model; <c>f32</c> is
    /// the opt-out where that matters more than the time.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or blank.</exception>
    public string? PrecisionHint
    {
        get => _precisionHint;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A precision hint must not be empty; null leaves the device's own.", nameof(value));
            _precisionHint = value;
        }
    }

    /// <summary>
    /// Whether the session opens a model OpenVINO can take only part of, running the rest on ONNX
    /// Runtime's CPU provider. <see langword="false"/> by default: such a model fails to open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A split model copies tensors between OpenVINO and the CPU provider at every boundary, so how
    /// fast it runs depends on where the split falls, and nothing reports that it happened. Refused,
    /// the failure comes at open, where <see cref="InferenceSessionFactoryBuilder"/> opens that model
    /// on the next provider it has and still tries OpenVINO first for the next model (#497).
    /// </para>
    /// <para>
    /// Every model the investigation tried went to OpenVINO whole, on the GPU and on the CPU device.
    /// Set this where a partial graph is known to be the faster choice.
    /// </para>
    /// </remarks>
    public bool AllowCpuFallback { get; init; }
}
