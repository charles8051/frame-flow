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
/// <para>
/// There is one <c>Write</c> per <see cref="ImageToTensorOptions.Dtype"/>, and each refuses options
/// that name another.
/// </para>
/// </remarks>
public static class ImageToTensor
{
    /// <summary>
    /// Samples <paramref name="crop"/> of <paramref name="frame"/> into
    /// <paramref name="destination"/>, which must hold at least
    /// <see cref="ImageToTensorOptions.ElementCount"/> floats, for options whose
    /// <see cref="ImageToTensorOptions.Dtype"/> is <see cref="DType.Float32"/>.
    /// </summary>
    /// <returns>The mapping from tensor coordinates back to frame pixels.</returns>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    /// <exception cref="ArgumentException">
    /// The crop is not finite or has no area, the options name another element type, or
    /// <paramref name="destination"/> is too short.
    /// </exception>
    public static TensorTransform Write(
        IVideoFrame frame,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<float> destination)
        => WriteFrame(frame, crop, options, destination);

    /// <summary>
    /// <see cref="Write(IVideoFrame, RotatedRect, ImageToTensorOptions, Span{float})"/> for options
    /// whose <see cref="ImageToTensorOptions.Dtype"/> is <see cref="DType.Float16"/>.
    /// </summary>
    /// <returns>The mapping from tensor coordinates back to frame pixels.</returns>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    /// <exception cref="ArgumentException">
    /// The crop is not finite or has no area, the options name another element type, or
    /// <paramref name="destination"/> is too short.
    /// </exception>
    public static TensorTransform Write(
        IVideoFrame frame,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<Half> destination)
        => WriteFrame(frame, crop, options, destination);

    /// <summary>
    /// <see cref="Write(IVideoFrame, RotatedRect, ImageToTensorOptions, Span{float})"/> for options
    /// whose <see cref="ImageToTensorOptions.Dtype"/> is <see cref="DType.UInt8"/>.
    /// </summary>
    /// <returns>The mapping from tensor coordinates back to frame pixels.</returns>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    /// <exception cref="ArgumentException">
    /// The crop is not finite or has no area, the options name another element type or a
    /// normalization other than <c>TensorNormalization.Range(0, 255)</c>, or
    /// <paramref name="destination"/> is too short.
    /// </exception>
    public static TensorTransform Write(
        IVideoFrame frame,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<byte> destination)
        => WriteFrame(frame, crop, options, destination);

    /// <summary>
    /// The mapping <see cref="Write(IVideoFrame, RotatedRect, ImageToTensorOptions, Span{float})"/>
    /// returns for <paramref name="crop"/>, without reading a frame. It depends only on the crop,
    /// the tensor's size and <see cref="ImageToTensorOptions.Fit"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The crop is not finite or has no area.</exception>
    public static TensorTransform Transform(RotatedRect crop, ImageToTensorOptions options)
    {
        Validate(crop, options);
        return ImageToTensorPlan.Create(crop, options.Width, options.Height, options.Fit).Transform;
    }

    private static TensorTransform WriteFrame<T>(
        IVideoFrame frame,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<T> destination)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        // The domain first: a GPU frame's format is its surface's, which says nothing about why
        // this cannot read it (#430).
        var cpu = frame.AsCpu()
            ?? throw new InvalidOperationException(
                $"ImageToTensor reads CPU frames, and this {frame.MemoryDomain} frame has no CPU view. "
                    + "Download it with ToCpu first, or prepare the input on the GPU.");

        bool bgra = frame.Format switch
        {
            PixelFormat.Bgra32 => true,
            PixelFormat.Rgba32 => false,
            _ => throw new NotSupportedException(
                $"ImageToTensor reads Bgra32 or Rgba32 frames; got {frame.Format}."),
        };

        return Write(
            cpu.PlaneY.Span, frame.Width, frame.Height, cpu.StrideY, bgra, crop, options, destination,
            ImageToTensorPath.Auto);
    }

    /// <summary>
    /// <see cref="Write(IVideoFrame, RotatedRect, ImageToTensorOptions, Span{float})"/> over a
    /// packed 32-bit image, on a chosen path, into a tensor of <typeparamref name="T"/>.
    /// </summary>
    internal static TensorTransform Write<T>(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        bool bgra,
        RotatedRect crop,
        ImageToTensorOptions options,
        Span<T> destination,
        ImageToTensorPath path)
        where T : unmanaged
    {
        Validate(crop, options);

        if (options.Dtype.ClrType() != typeof(T))
        {
            throw new ArgumentException(
                $"The options ask for a {options.Dtype} tensor and the destination holds {typeof(T).Name}.",
                nameof(destination));
        }

        if (destination.Length < options.ElementCount)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} elements; a {options.Width}x{options.Height} "
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
    /// Refuses a crop that is not finite or has no area, and the options
    /// <see cref="ValidateOptions"/> refuses. Shared with the device-side stage, which takes the
    /// same crop and options.
    /// </summary>
    internal static void Validate(RotatedRect crop, ImageToTensorOptions options)
    {
        ValidateOptions(options);

        if (!float.IsFinite(crop.CenterX) || !float.IsFinite(crop.CenterY) || !float.IsFinite(crop.Rotation)
            || !float.IsFinite(crop.Width) || !float.IsFinite(crop.Height)
            || crop.Width <= 0 || crop.Height <= 0)
        {
            throw new ArgumentException(
                $"The crop must be finite with a positive width and height; got {crop}.", nameof(crop));
        }
    }

    /// <summary>
    /// Refuses options that name an undefined mode, an element type other than Float32, Float16 or
    /// UInt8, or a UInt8 tensor with a normalization other than <c>Range(0, 255)</c>. A device-side
    /// stage checks them when it is built.
    /// </summary>
    internal static void ValidateOptions(ImageToTensorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.Fit) || !Enum.IsDefined(options.Sampling)
            || !Enum.IsDefined(options.Layout) || !Enum.IsDefined(options.ChannelOrder)
            || !Enum.IsDefined(options.YuvMatrix) || !Enum.IsDefined(options.YuvRange)
            || !Enum.IsDefined(options.Border))
        {
            throw new ArgumentException($"The options name an undefined mode: {options}.", nameof(options));
        }

        if (options.Dtype is not (DType.Float32 or DType.Float16 or DType.UInt8))
        {
            throw new ArgumentException(
                $"ImageToTensor writes Float32, Float16 or UInt8 tensors; the options ask for {options.Dtype}.",
                nameof(options));
        }

        // A byte holds a sample, not a normalized value, so the one normalization it carries
        // exactly is the one that leaves the samples as they are.
        if (options.Dtype == DType.UInt8 && options.Normalization != TensorNormalization.Range(0, 255))
        {
            throw new ArgumentException(
                "A UInt8 tensor holds the samples themselves, so its normalization must be "
                    + $"TensorNormalization.Range(0, 255); got {options.Normalization}.",
                nameof(options));
        }
    }
}
