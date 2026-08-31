// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding;

/// <summary>
/// Shared helper for converting a hardware-resident <c>AVFrame*</c> to
/// a packed CPU-side <see cref="CpuVideoFrame"/> in
/// <see cref="PixelFormat.Bgra32"/>. Used by
/// <see cref="GpuVideoFrame.ReadbackToCpuBgra32"/> (one-shot) and by
/// the <c>FrameFlow.Video</c> <c>ToCpu()</c> operator (which adds its
/// own caching layer on top).
/// </summary>
internal static unsafe class GpuFrameReadback
{
    /// <summary>
    /// Reads back a hardware-resident <c>AVFrame</c> to a managed
    /// <see cref="CpuVideoFrame"/> in tightly-packed
    /// <see cref="PixelFormat.Bgra32"/>. Two native operations
    /// internally: <c>av_hwframe_transfer_data</c> (PCIe readback to
    /// an intermediate CPU <c>AVFrame</c>, typically in NV12), then
    /// <c>sws_scale</c> to Bgra32 packed into the output buffer.
    /// </summary>
    /// <param name="sourceAvFrame">
    /// The GPU-resident <c>AVFrame*</c> to read back. Not consumed.
    /// </param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="pts">Presentation timestamp for the output frame.</param>
    /// <param name="duration">Display duration for the output frame.</param>
    /// <returns>A new <see cref="CpuVideoFrame"/> owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the native readback or scale call fails.
    /// </exception>
    internal static CpuVideoFrame ReadbackToBgra32(
        nint sourceAvFrame,
        int width,
        int height,
        TimeSpan pts,
        TimeSpan duration
    )
    {
        // Allocate the intermediate CPU AVFrame for av_hwframe_transfer_data
        // output. Format = AV_PIX_FMT_NONE asks the transfer function to pick
        // an appropriate CPU format (typically NV12).
        nint cpuPtr = FFAvUtil.av_frame_alloc();
        if (cpuPtr == nint.Zero)
            throw new InvalidOperationException("av_frame_alloc returned null.");

        using var cpuHandle = new FrameHandle(cpuPtr);

        int transferRc = FFAvUtil.av_hwframe_transfer_data(cpuPtr, sourceAvFrame, 0);
        if (transferRc < 0)
        {
            throw new InvalidOperationException(
                $"av_hwframe_transfer_data failed with code {transferRc}."
            );
        }

        // Now sws_scale the NV12 (or whatever CPU format the transfer
        // picked) → tight-packed Bgra32 into a managed buffer.
        var accessor = new AvFrameAccessor(cpuPtr);
        int swFormat = accessor.Format;

        const int bytesPerPixel = 4;
        int dstStride = width * bytesPerPixel;
        long byteCount = (long)dstStride * height;
        if (byteCount > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Readback frame too large for a single buffer: {width}x{height}."
            );
        }

        var outputBuffer = MemoryPool<byte>.Shared.Rent((int)byteCount);

        nint swsCtx = nint.Zero;
        try
        {
            swsCtx = FFSwScale.sws_getContext(
                width,
                height,
                swFormat,
                width,
                height,
                FFSwScale.AvPixFmtBgra,
                FFSwScale.SwsBilinear,
                srcFilter: nint.Zero,
                dstFilter: nint.Zero,
                param: nint.Zero
            );
            if (swsCtx == nint.Zero)
            {
                throw new InvalidOperationException(
                    $"sws_getContext returned null for {width}x{height} fmt={swFormat} → Bgra32."
                );
            }

            using var dstPin = outputBuffer.Memory.Pin();
            byte* dstData = (byte*)dstPin.Pointer;

            byte* srcPlane0 = accessor.GetDataPointer(0);
            byte* srcPlane1 = accessor.GetDataPointer(1);
            byte* srcPlane2 = accessor.GetDataPointer(2);
            int srcStride0 = accessor.GetLineSize(0);
            int srcStride1 = accessor.GetLineSize(1);
            int srcStride2 = accessor.GetLineSize(2);

            byte** srcSlice = stackalloc byte*[4];
            srcSlice[0] = srcPlane0;
            srcSlice[1] = srcPlane1;
            srcSlice[2] = srcPlane2;
            srcSlice[3] = null;

            int* srcStrides = stackalloc int[4];
            srcStrides[0] = srcStride0;
            srcStrides[1] = srcStride1;
            srcStrides[2] = srcStride2;
            srcStrides[3] = 0;

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

            int rows = FFSwScale.sws_scale(
                swsCtx,
                srcSlice,
                srcStrides,
                0,
                height,
                dstSlice,
                dstStrides
            );

            if (rows <= 0)
            {
                throw new InvalidOperationException(
                    $"sws_scale returned {rows} rows for {width}x{height} GPU readback."
                );
            }
        }
        catch
        {
            outputBuffer.Dispose();
            throw;
        }
        finally
        {
            if (swsCtx != nint.Zero)
                FFSwScale.sws_freeContext(swsCtx);
        }

        return new CpuVideoFrame(
            pixelData: outputBuffer,
            width: width,
            height: height,
            stride: dstStride,
            format: PixelFormat.Bgra32,
            presentationTime: pts,
            duration: duration
        );
    }
}
