// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.Core;
using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// Turns a region of a video frame into a model's input tensor: crop, fit, sample, normalize and
/// lay out, in one pass, as <see cref="ImageToTensorOptions"/> says.
/// </summary>
/// <remarks>
/// <para>
/// Each model's preprocessor configures one <see cref="ImageToTensorOptions"/> and calls
/// <see cref="Write(IVideoFrame, RotatedRect, ImageToTensorOptions, Span{float})"/> per frame with that frame's crop. The tensor stays inside the model's
/// operator; what is shared is this code, not a tensor.
/// </para>
/// <para>
/// CPU frames in <see cref="PixelFormat.Bgra32"/> or <see cref="PixelFormat.Rgba32"/> only. An
/// unrotated crop takes a vectorised path; a rotated one is sampled a pixel at a time.
/// </para>
/// </remarks>
public static class ImageToTensor
{
    /// <summary>
    /// Samples <paramref name="crop"/> of <paramref name="frame"/> into
    /// <paramref name="destination"/>, which must hold at least
    /// <see cref="ImageToTensorOptions.ElementCount"/> floats.
    /// </summary>
    /// <returns>The mapping from tensor coordinates back to frame pixels.</returns>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    /// <exception cref="ArgumentException">
    /// The crop is not finite or has no area, or <paramref name="destination"/> is too short.
    /// </exception>
    public static TensorTransform Write(
        IVideoFrame frame,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        bool bgra = frame.Format switch
        {
            PixelFormat.Bgra32 => true,
            PixelFormat.Rgba32 => false,
            _ => throw new NotSupportedException(
                $"ImageToTensor reads Bgra32 or Rgba32 frames; got {frame.Format}."),
        };

        var cpu = frame.AsCpu()
            ?? throw new InvalidOperationException(
                "ImageToTensor reads CPU frames, and AsCpu() returned null. Download the frame to the CPU first.");

        return Write(
            cpu.PlaneY.Span, frame.Width, frame.Height, cpu.StrideY, bgra, crop, options, destination,
            ImageToTensorPath.Auto);
    }

    /// <summary>
    /// <see cref="Write(IVideoFrame, RotatedRect, ImageToTensorOptions, Span{float})"/> over a
    /// packed 32-bit image, on a chosen path.
    /// </summary>
    internal static TensorTransform Write(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        bool bgra,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<float> destination,
        ImageToTensorPath path)
    {
        Validate(crop, options);

        if (destination.Length < options.ElementCount)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} floats; a {options.Width}x{options.Height} "
                    + $"tensor needs {options.ElementCount}.",
                nameof(destination));
        }

        // The kernel reads without bounds checks, so the image has to be what it says it is.
        if (width <= 0 || height <= 0 || stride < 4L * width
            || pixels.Length < (long)(height - 1) * stride + 4L * width)
        {
            throw new ArgumentException(
                $"A {width}x{height} image with a stride of {stride} does not fit in {pixels.Length} bytes.",
                nameof(pixels));
        }

        var plan = ImageToTensorPlan.Create(crop, options.Width, options.Height, options.Fit);
        ImageToTensorKernel.Run(pixels, width, height, stride, bgra, plan, options, destination, path);
        return plan.Transform;
    }

    /// <summary>
    /// Refuses a crop that is not finite or has no area, and options that name an undefined mode.
    /// Shared with the device-side stage, which takes the same crop and options.
    /// </summary>
    internal static void Validate(RotatedRect crop, ImageToTensorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!float.IsFinite(crop.CenterX) || !float.IsFinite(crop.CenterY) || !float.IsFinite(crop.Rotation)
            || !float.IsFinite(crop.Width) || !float.IsFinite(crop.Height)
            || crop.Width <= 0 || crop.Height <= 0)
        {
            throw new ArgumentException(
                $"The crop must be finite with a positive width and height; got {crop}.", nameof(crop));
        }

        if (!Enum.IsDefined(options.Fit) || !Enum.IsDefined(options.Sampling)
            || !Enum.IsDefined(options.Layout) || !Enum.IsDefined(options.ChannelOrder))
        {
            throw new ArgumentException($"The options name an undefined mode: {options}.", nameof(options));
        }
    }
}
