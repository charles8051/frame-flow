// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
    public (float ScaleX, float ScaleY) Preprocess(
        IVideoFrame frame,
        Span<float> destination
    )
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (destination.Length < InputElementCount)
        {
            throw new ArgumentException(
                $"Destination span has {destination.Length} elements; "
                    + $"YOLOv8 input requires at least {InputElementCount}.",
                nameof(destination)
            );
        }

        if (frame.Format is not (PixelFormat.Bgra32 or PixelFormat.Rgba32))
        {
            throw new NotSupportedException(
                $"Demo preprocessor expects Bgra32 or Rgba32 input frames; got {frame.Format}. "
                    + "Configure the playback pipeline to deliver one of these pixel formats."
            );
        }

        var cpu =
            frame.AsCpu()
            ?? throw new InvalidOperationException(
                "Demo expects CPU-resident frames; AsCpu() returned null."
            );

        ResizeAndNormalize(
            sourceBytes: cpu.PlaneY.Span,
            sourceWidth: frame.Width,
            sourceHeight: frame.Height,
            sourceStride: cpu.StrideY,
            isBgra: frame.Format == PixelFormat.Bgra32,
            destination: destination
        );

        return ((float)frame.Width / InputSize, (float)frame.Height / InputSize);
    }

    /// <summary>
    /// Inline stretched resize + BGRA/RGBA → RGB normalization + HWC → CHW transpose.
    /// Writes <c>3 · S · S</c> floats into <paramref name="destination"/> in CHW order:
    /// first S·S = R channel, then G, then B.
    /// </summary>
    private void ResizeAndNormalize(
        ReadOnlySpan<byte> sourceBytes,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        bool isBgra,
        Span<float> destination
    )
    {
        int size = InputSize;
        int channelStride = size * size;
        var rOffset = 0;
        var gOffset = channelStride;
        var bOffset = channelStride * 2;

        // BGRA: B at byte 0, G at 1, R at 2.
        // RGBA: R at byte 0, G at 1, B at 2.
        var rByte = isBgra ? 2 : 0;
        var gByte = 1;
        var bByte = isBgra ? 0 : 2;

        for (int dy = 0; dy < size; dy++)
        {
            int sy = (dy * sourceHeight) / size;
            int srcRowOffset = sy * sourceStride;

            for (int dx = 0; dx < size; dx++)
            {
                int sx = (dx * sourceWidth) / size;
                int srcPixelOffset = srcRowOffset + sx * 4;

                int destIndex = dy * size + dx;
                destination[rOffset + destIndex] = sourceBytes[srcPixelOffset + rByte] / 255f;
                destination[gOffset + destIndex] = sourceBytes[srcPixelOffset + gByte] / 255f;
                destination[bOffset + destIndex] = sourceBytes[srcPixelOffset + bByte] / 255f;
            }
        }
    }
}
