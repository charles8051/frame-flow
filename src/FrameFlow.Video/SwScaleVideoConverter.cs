// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Video;

/// <summary>
/// <see cref="IVideoConverter"/> backed by FFmpeg's <c>libswscale</c>
/// (ADR-0037). Lazy-initializes its <c>SwsContext</c> on the first
/// <see cref="Process"/> call, observing the source frame's
/// dimensions and pixel format; rebuilds the context if any of
/// (source dims, source format, target dims, target format) change.
/// </summary>
/// <remarks>
/// Mirrors the design of <c>FfmpegAudioResampler</c> in
/// <c>FrameFlow.Audio</c>: a stateful primitive wrapped by stateless
/// pipeline operators.
/// </remarks>
internal sealed unsafe class SwScaleVideoConverter : IVideoConverter
{
    private SwsContextHandle? _sws;
    private bool _disposed;

    // The full (src dims+format, dst dims+format) shape the live _sws context was
    // built for, or null when none is built. The reuse-vs-rebuild decision over this
    // key is the pure SwsPlan.Decide predicate (§3.3); this field is the shell-owned
    // cached state it folds against.
    private SwsConfigKey? _configKey;

    public int? TargetWidth { get; }
    public int? TargetHeight { get; }
    public PixelFormat? TargetFormat { get; }

    internal SwScaleVideoConverter(int? targetWidth, int? targetHeight, PixelFormat? targetFormat)
    {
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
        TargetFormat = targetFormat;
    }

    public CpuVideoFrame Process(IVideoFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int srcWidth = source.Width;
        int srcHeight = source.Height;
        var srcFormat = source.Format;

        // Resolve effective target shape (null → inherit from source).
        int dstWidth = TargetWidth ?? srcWidth;
        int dstHeight = TargetHeight ?? srcHeight;
        var dstFormat = TargetFormat ?? srcFormat;

        if (!VideoConverter.IsSupportedOutputFormat(dstFormat))
        {
            throw new NotSupportedException(
                $"Output format {dstFormat} is not yet supported. The initial drop supports "
                    + "Bgra32 and Rgba32 only."
            );
        }

        EnsureSwsContext(srcWidth, srcHeight, srcFormat, dstWidth, dstHeight, dstFormat);

        var cpuData = source.ToCpu();

        // Output buffer is a single packed plane: 4 bpp for both
        // Bgra32 and Rgba32. Stride = width * 4 with no padding —
        // simpler than the FFmpeg natural alignment and avoids
        // surprises for sinks that assume tight packing.
        const int bytesPerPixel = 4;
        int dstStride = dstWidth * bytesPerPixel;
        long byteCount = (long)dstStride * dstHeight;
        if (byteCount > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Output frame too large for a single buffer: {dstWidth}x{dstHeight} = {byteCount} bytes."
            );
        }

        var outputBuffer = MemoryPool<byte>.Shared.Rent((int)byteCount);

        try
        {
            using var dstPin = outputBuffer.Memory.Pin();
            byte* dstData = (byte*)dstPin.Pointer;

            // Pin the source planes. For packed input (Bgra32, Rgba32)
            // PlaneU/PlaneV are empty and we just pin PlaneY. For YUV
            // we pin all three. Pinning empty ReadOnlyMemory yields a
            // null pointer, which is what swscale expects for "unused
            // plane."
            using var pinY = cpuData.PlaneY.Pin();
            using var pinU = cpuData.PlaneU.Pin();
            using var pinV = cpuData.PlaneV.Pin();

            byte** srcSlice = stackalloc byte*[4];
            srcSlice[0] = (byte*)pinY.Pointer;
            srcSlice[1] = (byte*)pinU.Pointer;
            srcSlice[2] = (byte*)pinV.Pointer;
            srcSlice[3] = null;

            int* srcStrides = stackalloc int[4];
            srcStrides[0] = cpuData.StrideY;
            srcStrides[1] = cpuData.StrideU;
            srcStrides[2] = cpuData.StrideV;
            srcStrides[3] = 0;

            // swscale wants 4 destination plane pointers / strides
            // even for packed single-plane outputs. Passing 1-element
            // arrays would let the native code read past the stack
            // buffer (cf. the same comment in VideoDecoder).
            byte** dstSlice = stackalloc byte*[4];
            dstSlice[0] = dstData;
            dstSlice[1] = null;
            dstSlice[2] = null;
            dstSlice[3] = null;

            int* dstStrides = stackalloc int[4];
            dstStrides[0] = dstStride;
            dstStrides[1] = 0;
            dstStrides[2] = 0;
            dstStrides[3] = 0;

            int rowsWritten = FFSwScale.sws_scale(
                _sws!.DangerousGetHandle(),
                srcSlice,
                srcStrides,
                0,
                srcHeight,
                dstSlice,
                dstStrides
            );

            if (rowsWritten <= 0)
            {
                throw new InvalidOperationException(
                    $"sws_scale returned {rowsWritten} rows for a {srcWidth}x{srcHeight} → "
                        + $"{dstWidth}x{dstHeight} conversion."
                );
            }
        }
        catch
        {
            outputBuffer.Dispose();
            throw;
        }

        return new CpuVideoFrame(
            pixelData: outputBuffer,
            width: dstWidth,
            height: dstHeight,
            stride: dstStride,
            format: dstFormat,
            presentationTime: source.Pts,
            duration: source.Duration
        );
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sws?.Dispose();
        _sws = null;
        _configKey = null;
    }

    // -------------------------------------------------------------------------
    // SwsContext management
    // -------------------------------------------------------------------------

    private void EnsureSwsContext(
        int srcWidth,
        int srcHeight,
        PixelFormat srcFormat,
        int dstWidth,
        int dstHeight,
        PixelFormat dstFormat
    )
    {
        var requested = new SwsConfigKey(
            srcWidth,
            srcHeight,
            srcFormat,
            dstWidth,
            dstHeight,
            dstFormat
        );

        // Pure decision (§3.3): a live, valid context built for the same key is reusable.
        // An invalidated handle is treated as "no context" by clearing the cached key first,
        // so the predicate sees the real situation.
        if (_sws is null || _sws.IsInvalid)
            _configKey = null;

        if (SwsPlan.Decide(_configKey, requested) == SwsPlanDecision.Reuse)
            return;

        // Rebuild: dispose the stale context, then build one for the requested shape.
        _sws?.Dispose();
        _sws = null;
        _configKey = null;

        int srcAvFormat = MapToAvPixelFormat(srcFormat);
        int dstAvFormat = MapToAvPixelFormat(dstFormat);

        nint ctx = FFSwScale.sws_getContext(
            srcWidth,
            srcHeight,
            srcAvFormat,
            dstWidth,
            dstHeight,
            dstAvFormat,
            FFSwScale.SwsBilinear,
            srcFilter: nint.Zero,
            dstFilter: nint.Zero,
            param: nint.Zero
        );

        if (ctx == nint.Zero)
        {
            throw new InvalidOperationException(
                $"sws_getContext returned null for {srcWidth}x{srcHeight} {srcFormat} → "
                    + $"{dstWidth}x{dstHeight} {dstFormat}."
            );
        }

        _sws = new SwsContextHandle(ctx);
        _configKey = requested;
    }

    private static int MapToAvPixelFormat(PixelFormat format) =>
        format switch
        {
            PixelFormat.Bgra32 => FFSwScale.AvPixFmtBgra,
            PixelFormat.Rgba32 => FFSwScale.AvPixFmtRgba,
            PixelFormat.Yuv420P => FFSwScale.AvPixFmtYuv420P,
            PixelFormat.Nv12 => FFSwScale.AvPixFmtNv12,
            PixelFormat.Yuyv422 => FFSwScale.AvPixFmtYuyv422,
            PixelFormat.Uyvy422 => FFSwScale.AvPixFmtUyvy422,
            _ => throw new ArgumentException(
                $"Pixel format {format} is not mapped to an AVPixelFormat.",
                nameof(format)
            ),
        };
}
