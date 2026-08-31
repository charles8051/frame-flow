// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Video;

/// <summary>
/// Converts <see cref="IVideoFrame"/> instances to a fixed target
/// dimensions and/or pixel format using <c>libswscale</c> under the
/// hood (the same scaler the FFmpeg video decoder uses for its
/// internal readback).
/// </summary>
/// <remarks>
/// <para>
/// <b>Configurable knobs.</b> Each of width, height, and format can
/// be specified or left <see langword="null"/> to inherit from the
/// source frame. At least one must be specified — a converter with
/// all three null wouldn't have a job to do.
/// </para>
/// <para>
/// <b>Stateful.</b> The first <see cref="Process"/> call observes the
/// source frame's dimensions and format, then allocates an
/// <c>SwsContext</c>. Subsequent calls reuse the context when the
/// effective (input, output) shape matches; if the source dimensions
/// or format change mid-stream, the context is rebuilt.
/// </para>
/// <para>
/// <b>Output format support — initial scope.</b> The current drop
/// supports packed single-plane output formats —
/// <see cref="PixelFormat.Bgra32"/> and <see cref="PixelFormat.Rgba32"/>.
/// Multi-plane outputs (YUV420P, NV12) are deferred to a follow-up;
/// passing them as the target throws <see cref="NotSupportedException"/>.
/// Input format is unrestricted — any format <c>libswscale</c>
/// accepts.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe. Call <see cref="Process"/> from
/// a single thread (the typical pipeline-operator pattern).
/// </para>
/// </remarks>
public interface IVideoConverter : IDisposable
{
    /// <summary>
    /// Target output width in pixels. <see langword="null"/> means
    /// "same as source." Configured at construction.
    /// </summary>
    int? TargetWidth { get; }

    /// <summary>
    /// Target output height in pixels. <see langword="null"/> means
    /// "same as source." Configured at construction.
    /// </summary>
    int? TargetHeight { get; }

    /// <summary>
    /// Target output pixel format. <see langword="null"/> means
    /// "same as source." Configured at construction.
    /// </summary>
    PixelFormat? TargetFormat { get; }

    /// <summary>
    /// Converts <paramref name="source"/> to a freshly-allocated
    /// <see cref="CpuVideoFrame"/> at the configured target shape.
    /// The caller owns the returned frame and must dispose it. The
    /// source frame is <b>not</b> consumed — the caller retains
    /// ownership.
    /// </summary>
    /// <param name="source">
    /// Input frame. Read once during this call; not disposed.
    /// </param>
    /// <returns>
    /// A new CPU-resident frame at the configured target shape, with
    /// <c>PresentationTime</c> and <c>Duration</c> copied from
    /// <paramref name="source"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the source's pixel format isn't supported by
    /// swscale, or the target shape is invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when swscale's internal allocation or conversion call
    /// fails (very rare; typically indicates a corrupt source frame).
    /// </exception>
    CpuVideoFrame Process(IVideoFrame source);
}

/// <summary>
/// Factory for <see cref="IVideoConverter"/>. Returns a swscale-
/// backed implementation; future variants (managed-only fallback,
/// mock for tests) could be added without changing consumers.
/// </summary>
public static class VideoConverter
{
    /// <summary>
    /// Creates a converter configured to produce output frames at
    /// the given target dimensions and/or format. Any parameter left
    /// <see langword="null"/> inherits from the source frame at
    /// process time.
    /// </summary>
    /// <param name="targetWidth">
    /// Target output width in pixels, or <see langword="null"/> for
    /// "same as source."
    /// </param>
    /// <param name="targetHeight">
    /// Target output height in pixels, or <see langword="null"/> for
    /// "same as source."
    /// </param>
    /// <param name="targetFormat">
    /// Target output pixel format, or <see langword="null"/> for
    /// "same as source."
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when all three parameters are <see langword="null"/> —
    /// the converter would have no work to do.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either dimension is non-positive.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="targetFormat"/> is a multi-plane
    /// format. The initial drop supports only packed single-plane
    /// outputs (<see cref="PixelFormat.Bgra32"/>,
    /// <see cref="PixelFormat.Rgba32"/>).
    /// </exception>
    public static IVideoConverter Create(
        int? targetWidth = null,
        int? targetHeight = null,
        PixelFormat? targetFormat = null
    )
    {
        if (targetWidth is null && targetHeight is null && targetFormat is null)
        {
            throw new ArgumentException(
                "At least one of targetWidth, targetHeight, or targetFormat must be specified."
            );
        }

        if (targetWidth is { } w)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(w);
        if (targetHeight is { } h)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(h);

        if (targetFormat is { } f && !IsSupportedOutputFormat(f))
        {
            throw new NotSupportedException(
                $"Output format {f} is not yet supported. The initial drop supports packed single-plane "
                    + "formats (Bgra32, Rgba32) only. Multi-plane output (YUV420P, NV12) is deferred."
            );
        }

        return new SwScaleVideoConverter(targetWidth, targetHeight, targetFormat);
    }

    internal static bool IsSupportedOutputFormat(PixelFormat format) =>
        format is PixelFormat.Bgra32 or PixelFormat.Rgba32;
}
