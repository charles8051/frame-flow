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
/// <see cref="GpuVideoFrame.ReadbackToCpuBgra32"/>, which
/// <c>FrameFlow.Video</c>'s <c>VideoOperators.ToCpu(id)</c> node calls
/// once per hardware frame.
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

            // All four planes, matching VideoDecoder.BuildManagedFrameFromCpu. No
            // four-plane format reaches here today: this converts a frame
            // transferred off the GPU, and the transfer formats are nv12 and p010.
            // Kept identical anyway so the two call sites cannot drift, and because
            // it is a no-op for the formats that do arrive.
            byte** srcSlice = stackalloc byte*[4];
            int* srcStrides = stackalloc int[4];
            for (int plane = 0; plane < 4; plane++)
            {
                srcSlice[plane] = accessor.GetDataPointer(plane);
                srcStrides[plane] = accessor.GetLineSize(plane);
            }

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
