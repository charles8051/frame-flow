// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference;
using FrameFlow.Media;

namespace FrameFlow.Yolo;

/// <summary>
/// CPU-side YOLOv8 input preprocessing: a BGRA / RGBA video frame at
/// arbitrary resolution becomes a normalized RGB tensor of shape
/// <c>[1, 3, S, S]</c> in CHW layout (S = <see cref="InputSize"/>),
/// ready to feed the model.
/// </summary>
/// <remarks>
/// <para>
/// The pixel work is <see cref="ImageToTensor"/>'s: the whole frame,
/// stretched, nearest-neighbour, <c>[0, 1]</c>, NCHW, RGB.
/// </para>
/// <para>
/// V1 uses a simple stretched resize (no letterboxing), which slightly
/// distorts non-square inputs. Letterboxing improves detection on
/// extreme aspect ratios but adds complexity to the postprocessing's
/// coordinate mapping; deferred until the demo evolves past
/// proof-of-concept.
/// </para>
/// <para>
/// <b>Backend-agnostic.</b> The preprocessor writes into a caller-
/// supplied <see cref="Span{Single}"/>. CUDA-backed callers point the
/// span at a host staging buffer and then upload it to a
/// <c>CudaTensor&lt;float&gt;</c>; CPU/DML-backed callers point the
/// span directly at a <c>CpuTensor&lt;float&gt;.Span</c>, skipping the
/// intermediate buffer.
/// </para>
/// <para>
/// <b>Input size is per-instance (ADR-0050 §1).</b> The side length is
/// set at construction from the model's descriptor rather than a
/// compile-time constant, so smaller-input models (416, 320) share this
/// code. The scale factors returned by <see cref="Preprocess"/> are
/// computed against the configured size.
/// </para>
/// </remarks>
public sealed class Yolov8Preprocessor
{
    private readonly ImageToTensorOptions _options;

    /// <summary>Model input image side length in pixels (multiple of 32).</summary>
    public int InputSize { get; }

    /// <summary>Total elements in the input tensor (1 · 3 · S · S).</summary>
    public int InputElementCount => 3 * InputSize * InputSize;

    /// <summary>Builds a preprocessor for a square model input of <paramref name="inputSize"/> px.</summary>
    public Yolov8Preprocessor(int inputSize = 640)
    {
        if (inputSize <= 0 || inputSize % 32 != 0)
        {
            throw new ArgumentException(
                $"InputSize must be a positive multiple of 32; got {inputSize}.",
                nameof(inputSize));
        }
        InputSize = inputSize;
        _options = new ImageToTensorOptions(inputSize, inputSize)
        {
            Sampling = ImageSampling.Nearest,
            Normalization = TensorNormalization.ZeroToOne,
        };
    }

    /// <summary>
    /// Preprocesses <paramref name="frame"/> and writes the result into
    /// <paramref name="destination"/>, which must have at least
    /// <see cref="InputElementCount"/> elements.
    /// </summary>
    /// <returns>
    /// (scaleX, scaleY): factors to multiply model-space coordinates by
    /// to get source-space pixel coordinates. With stretched resize,
    /// scaleX = source.Width / S, scaleY = source.Height / S.
    /// </returns>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    public (float ScaleX, float ScaleY) Preprocess(
        IVideoFrame frame,
        Span<float> destination
    )
    {
        ArgumentNullException.ThrowIfNull(frame);

        ImageToTensor.Write(frame, RotatedRect.Whole(frame), _options, destination);

        return ((float)frame.Width / InputSize, (float)frame.Height / InputSize);
    }
}
