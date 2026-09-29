// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// The tensor a model takes as input, and how <see cref="ImageToTensor"/> fills it from a frame.
/// One value per model; the crop varies per call.
/// </summary>
public sealed record ImageToTensorOptions
{
    /// <summary>A tensor of <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    public ImageToTensorOptions(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be positive.");
        }

        // Two positive ints fit in a long; a third factor might not.
        if ((long)width * height > Array.MaxLength / 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, $"A {width}x{height} tensor has more elements than an array can hold.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>The tensor's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The tensor's height in pixels.</summary>
    public int Height { get; }

    /// <summary>How a crop whose aspect differs from the tensor's is fitted. Defaults to <see cref="ImageFit.Stretch"/>.</summary>
    public ImageFit Fit { get; init; } = ImageFit.Stretch;

    /// <summary>How the frame is read at each tensor pixel. Defaults to <see cref="ImageSampling.Bilinear"/>.</summary>
    public ImageSampling Sampling { get; init; } = ImageSampling.Bilinear;

    /// <summary>How samples become tensor values. Defaults to <see cref="TensorNormalization.ZeroToOne"/>.</summary>
    public TensorNormalization Normalization { get; init; } = TensorNormalization.ZeroToOne;

    /// <summary>
    /// The tensor's element type: <see cref="DType.Float32"/>, the default, <see cref="DType.Float16"/>
    /// or <see cref="DType.UInt8"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="DType.Float16"/> element is the <see cref="DType.Float32"/> value converted to
    /// <see cref="Half"/>, rounding to nearest with ties to even.
    /// </para>
    /// <para>
    /// A <see cref="DType.UInt8"/> element is the sample itself, so <see cref="Normalization"/> must be
    /// <c>TensorNormalization.Range(0, 255)</c>, under which the <see cref="DType.Float32"/> value is
    /// the sample. Nearest sampling copies the frame's byte. A bilinear sample is the
    /// <see cref="DType.Float32"/> value rounded to nearest with ties to even, clamped to 0 to 255.
    /// A bar or a padded pixel is <see cref="PadValue"/>.
    /// </para>
    /// </remarks>
    public DType Dtype { get; init; } = DType.Float32;

    /// <summary>The memory layout. Defaults to <see cref="TensorLayout.Nchw"/>.</summary>
    public TensorLayout Layout { get; init; } = TensorLayout.Nchw;

    /// <summary>The colour channel order. Defaults to <see cref="TensorChannelOrder.Rgb"/>.</summary>
    public TensorChannelOrder ChannelOrder { get; init; } = TensorChannelOrder.Rgb;

    /// <summary>
    /// The 8-bit sample written in every channel of a letterbox bar, and outside the frame with
    /// <see cref="ImageBorder.Pad"/>, normalized like any other.
    /// </summary>
    public byte PadValue { get; init; }

    /// <summary>What a crop reads past the frame's edge. Defaults to <see cref="ImageBorder.Replicate"/>.</summary>
    public ImageBorder Border { get; init; } = ImageBorder.Replicate;

    /// <summary>
    /// The matrix a YUV frame's samples are converted to RGB with. Defaults to
    /// <see cref="YuvMatrix.Bt601"/>. Read by stages that take YUV frames; <see cref="ImageToTensor"/>
    /// takes RGB frames and does not use it.
    /// </summary>
    public YuvMatrix YuvMatrix { get; init; } = YuvMatrix.Bt601;

    /// <summary>
    /// The range a YUV frame's samples cover. Defaults to <see cref="YuvRange.Limited"/>. Read by
    /// stages that take YUV frames; <see cref="ImageToTensor"/> takes RGB frames and does not use it.
    /// </summary>
    public YuvRange YuvRange { get; init; } = YuvRange.Limited;

    /// <summary>The number of elements the tensor holds: <c>3 · Width · Height</c>.</summary>
    public int ElementCount => 3 * Width * Height;
}
