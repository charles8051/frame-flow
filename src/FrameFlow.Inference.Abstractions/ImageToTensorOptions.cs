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

        if (3L * width * height > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, $"A {width}x{height} tensor has more floats than an array can hold.");
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

    /// <summary>The memory layout. Defaults to <see cref="TensorLayout.Nchw"/>.</summary>
    public TensorLayout Layout { get; init; } = TensorLayout.Nchw;

    /// <summary>The colour channel order. Defaults to <see cref="TensorChannelOrder.Rgb"/>.</summary>
    public TensorChannelOrder ChannelOrder { get; init; } = TensorChannelOrder.Rgb;

    /// <summary>
    /// The 8-bit sample written in every channel of a letterbox bar, normalized like any other.
    /// Unused by <see cref="ImageFit.Stretch"/>.
    /// </summary>
    public byte PadValue { get; init; }

    /// <summary>The number of floats the tensor holds: <c>3 · Width · Height</c>.</summary>
    public int ElementCount => 3 * Width * Height;
}
