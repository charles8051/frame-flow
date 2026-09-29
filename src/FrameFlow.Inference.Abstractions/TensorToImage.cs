// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Runtime.InteropServices;
using FrameFlow.Inference.Core;
using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// Turns a model's image-shaped output tensor into packed 8-bit pixels: undo the normalization,
/// reorder the layout and channels, clamp and round, in one pass, as
/// <see cref="TensorToImageOptions"/> says. The reverse of <see cref="ImageToTensor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each model's decoder configures one <see cref="TensorToImageOptions"/> and calls
/// <see cref="ToFrame"/> per run, or
/// <see cref="Write(ReadOnlySpan{float}, TensorToImageOptions, Span{byte}, int)"/> to fill a buffer of its own.
/// </para>
/// <para>
/// Each value becomes <c>(value − Offset) / Scale</c> for its channel's normalization, computed in
/// float as <c>(value − Offset) · (1 / Scale)</c>, then is clamped to 0 to 255 and rounded to the
/// nearest integer, ties to even. A value within float error of a tie can round either way. NaN becomes 0. Alpha is 255. The conversion is
/// vectorised where the hardware allows.
/// </para>
/// <para>
/// A depth map whose range changes from frame to frame can pass its range per call, as
/// <c>options with { Normalization = TensorNormalization.Range(min, max) }</c>.
/// </para>
/// </remarks>
public static class TensorToImage
{
    /// <summary>
    /// Writes <paramref name="tensor"/> into <paramref name="destination"/> as
    /// <see cref="TensorToImageOptions.Height"/> rows of <paramref name="stride"/> bytes. It writes
    /// the first <c>4 · Width</c> bytes of each row and leaves the rest of the row as it was.
    /// </summary>
    /// <exception cref="NotSupportedException">The options ask for a format other than Bgra32 or Rgba32.</exception>
    /// <exception cref="ArgumentException">
    /// The options name an undefined mode, a normalization with no inverse, or a per-channel
    /// normalization for a single-channel tensor; <paramref name="tensor"/> does not hold exactly
    /// <see cref="TensorToImageOptions.ElementCount"/> floats; or <paramref name="destination"/> is
    /// too short for the rows.
    /// </exception>
    public static void Write(
        ReadOnlySpan<float> tensor,
        TensorToImageOptions options,
        Span<byte> destination,
        int stride)
        => Write(tensor, options, destination, stride, TensorToImagePath.Auto);

    /// <summary>
    /// Creates a <see cref="CpuVideoFrame"/> in <see cref="TensorToImageOptions.Format"/> from
    /// <paramref name="tensor"/>. The frame owns its pixels, so the tensor can be reused once this
    /// returns.
    /// </summary>
    /// <param name="tensor">A 32-bit float tensor of <see cref="TensorToImageOptions.ElementCount"/> elements.</param>
    /// <param name="options">The tensor's layout and normalization, and the frame's format.</param>
    /// <param name="presentationTime">The frame's presentation timestamp.</param>
    /// <param name="duration">How long the frame is displayed.</param>
    /// <param name="pool">Where the frame's storage comes from. Defaults to <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <returns>The frame, holding one reference.</returns>
    /// <exception cref="NotSupportedException">The options ask for a format other than Bgra32 or Rgba32.</exception>
    /// <exception cref="ArgumentException">
    /// The options name an undefined mode, a normalization with no inverse, or a per-channel
    /// normalization for a single-channel tensor; or the tensor is not 32-bit float or does not hold
    /// exactly <see cref="TensorToImageOptions.ElementCount"/> elements.
    /// </exception>
    public static CpuVideoFrame ToFrame(
        ICpuTensor tensor,
        TensorToImageOptions options,
        TimeSpan presentationTime,
        TimeSpan duration,
        ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        ValidateOptions(options);

        // Checked before the frame rents its storage.
        if (tensor.Dtype != DType.Float32)
        {
            throw new ArgumentException($"TensorToImage reads Float32 tensors; got {tensor.Dtype}.", nameof(tensor));
        }

        if (tensor.ByteCount != (long)sizeof(float) * options.ElementCount)
        {
            throw new ArgumentException(
                $"The tensor holds {tensor.ByteCount} bytes; a {options.Channels}-channel "
                    + $"{options.Width}x{options.Height} image needs {(long)sizeof(float) * options.ElementCount}.",
                nameof(tensor));
        }

        return CpuVideoFrame.Create(
            options.Format,
            options.Width,
            options.Height,
            presentationTime,
            duration,
            (Tensor: tensor, Options: options),
            static (planes, state) => Write(
                MemoryMarshal.Cast<byte, float>(state.Tensor.Bytes.Span),
                state.Options,
                planes.Y,
                planes.StrideY,
                TensorToImagePath.Auto),
            pool);
    }

    /// <summary><see cref="Write(ReadOnlySpan{float}, TensorToImageOptions, Span{byte}, int)"/> on a chosen path.</summary>
    internal static void Write(
        ReadOnlySpan<float> tensor,
        TensorToImageOptions options,
        Span<byte> destination,
        int stride,
        TensorToImagePath path)
    {
        ValidateOptions(options);

        if (tensor.Length != options.ElementCount)
        {
            throw new ArgumentException(
                $"The tensor holds {tensor.Length} floats; a {options.Channels}-channel "
                    + $"{options.Width}x{options.Height} image needs {options.ElementCount}.",
                nameof(tensor));
        }

        // The kernel slices rows at multiples of the stride, so the buffer has to be what it says it is.
        long rowBytes = 4L * options.Width;
        if (stride < rowBytes || destination.Length < (long)(options.Height - 1) * stride + rowBytes)
        {
            throw new ArgumentException(
                $"{options.Height} rows of {rowBytes} bytes with a stride of {stride} do not fit in "
                    + $"{destination.Length} bytes.",
                nameof(destination));
        }

        TensorToImageKernel.Run(tensor, options, destination, stride, path);
    }

    /// <summary>
    /// Refuses options that name an undefined mode, a format other than Bgra32 or Rgba32, or a
    /// normalization with no finite inverse, and a single-channel tensor whose channels are
    /// normalized differently.
    /// </summary>
    internal static void ValidateOptions(TensorToImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Format is not (PixelFormat.Bgra32 or PixelFormat.Rgba32))
        {
            throw new NotSupportedException($"TensorToImage writes Bgra32 or Rgba32; the options ask for {options.Format}.");
        }

        if (!Enum.IsDefined(options.Layout) || !Enum.IsDefined(options.ChannelOrder))
        {
            throw new ArgumentException($"The options name an undefined mode: {options}.", nameof(options));
        }

        var normalization = options.Normalization;
        if (!Invertible(normalization.Red) || !Invertible(normalization.Green) || !Invertible(normalization.Blue))
        {
            throw new ArgumentException(
                $"Each channel's normalization needs a finite, non-zero scale with a finite inverse; got {normalization}.",
                nameof(options));
        }

        if (options.Channels == 1 && (normalization.Red != normalization.Green || normalization.Red != normalization.Blue))
        {
            throw new ArgumentException(
                $"A single-channel tensor is written as grey, so its normalization needs the same scale and "
                    + $"offset on every channel; got {normalization}.",
                nameof(options));
        }

        static bool Invertible((float Scale, float Offset) channel)
        {
            return float.IsFinite(channel.Scale) && channel.Scale != 0f && float.IsFinite(channel.Offset)
                && float.IsFinite(TensorToImageKernel.Reciprocal(channel.Scale));
        }
    }
}
