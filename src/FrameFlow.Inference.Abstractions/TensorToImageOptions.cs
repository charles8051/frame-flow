// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// The image-shaped tensor a model returns, and how <see cref="TensorToImage"/> turns it into
/// packed 8-bit pixels. One value per model.
/// </summary>
/// <remarks>
/// The image has the tensor's width and height: each tensor pixel becomes one image pixel.
/// </remarks>
public sealed record TensorToImageOptions
{
    private readonly int _channels = 3;

    /// <summary>A tensor of <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    public TensorToImageOptions(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be positive.");
        }

        // The image is four bytes a pixel, so it is the larger of the two; the tensor is at most
        // three floats a pixel and fits whenever the image does.
        if ((long)width * height > Array.MaxLength / 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, $"A {width}x{height} image has more bytes than an array can hold.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>The tensor's width in pixels, and the image's.</summary>
    public int Width { get; }

    /// <summary>The tensor's height in pixels, and the image's.</summary>
    public int Height { get; }

    /// <summary>
    /// The number of channels in the tensor: 3 for a colour image, 1 for a single-channel one
    /// such as a mask, which is written as grey. Defaults to 3.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is neither 1 nor 3.</exception>
    public int Channels
    {
        get => _channels;
        init => _channels = value is 1 or 3
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Channels), value, "A tensor image has 1 or 3 channels.");
    }

    /// <summary>
    /// The normalization the model's output is in, which <see cref="TensorToImage"/> undoes.
    /// Defaults to <see cref="TensorNormalization.ZeroToOne"/>. A single-channel tensor needs the
    /// same scale and offset on every channel.
    /// </summary>
    public TensorNormalization Normalization { get; init; } = TensorNormalization.ZeroToOne;

    /// <summary>
    /// The memory layout. Defaults to <see cref="TensorLayout.Nchw"/>. A single-channel tensor
    /// is laid out the same either way.
    /// </summary>
    public TensorLayout Layout { get; init; } = TensorLayout.Nchw;

    /// <summary>
    /// The colour channel order. Defaults to <see cref="TensorChannelOrder.Rgb"/>. Unused by a
    /// single-channel tensor.
    /// </summary>
    public TensorChannelOrder ChannelOrder { get; init; } = TensorChannelOrder.Rgb;

    /// <summary>
    /// The pixel format written: <see cref="PixelFormat.Bgra32"/> or <see cref="PixelFormat.Rgba32"/>,
    /// with alpha 255. Defaults to <see cref="PixelFormat.Bgra32"/>.
    /// </summary>
    public PixelFormat Format { get; init; } = PixelFormat.Bgra32;

    /// <summary>The number of floats the tensor holds: <c>Channels · Width · Height</c>.</summary>
    public int ElementCount => Channels * Width * Height;
}
