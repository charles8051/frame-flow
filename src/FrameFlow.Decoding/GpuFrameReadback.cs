// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Core;
using FrameFlow.Decoding.Internal;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
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
    /// <see cref="PixelFormat.Bgra32"/>. <c>av_hwframe_transfer_data</c>
    /// reads it back to an intermediate CPU <c>AVFrame</c>, in NV12 or
    /// P010, and <see cref="BgraConversion"/> converts that to Bgra32 the
    /// way the software decoder converts the same stream (#792).
    /// </summary>
    /// <param name="sourceAvFrame">
    /// The GPU-resident <c>AVFrame*</c> to read back. Not consumed.
    /// </param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="pts">Presentation timestamp for the output frame.</param>
    /// <param name="duration">Display duration for the output frame.</param>
    /// <param name="sampleAspectRatio">The pixel shape to carry (#542). Unknown, the default, is square.</param>
    /// <param name="rotation">The turn to display the frame upright (#542).</param>
    /// <param name="timeline">The timeline the frame read back belongs to (ADR-0084).</param>
    /// <returns>A new <see cref="CpuVideoFrame"/> owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the native readback or scale call fails.
    /// </exception>
    internal static CpuVideoFrame ReadbackToBgra32(
        nint sourceAvFrame,
        int width,
        int height,
        TimeSpan pts,
        TimeSpan duration,
        SampleAspectRatio sampleAspectRatio = default,
        VideoRotation rotation = VideoRotation.None,
        TimelineId timeline = default
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

        FrameCopyMetrics.Record(FrameCopySite.ReadbackDownload);

        // Convert as the software decoder converts the same stream (#792): NV12 chroma is split
        // into the planes it would have produced, and the range and the matrix (#796) come from
        // the hardware frame, since the transfer copies neither.
        var hardware = new AvFrameAccessor(sourceAvFrame);
        var input = BgraConversionInput.For(
            new AvFrameAccessor(cpuPtr).Format,
            hardware.ColorRange,
            hardware.ColorSpace
        );

        nint swsCtx = BgraConversion.CreateContext(width, height, input);
        if (swsCtx == nint.Zero)
        {
            throw new InvalidOperationException(
                $"sws_getContext returned null for {width}x{height} {input} to Bgra32."
            );
        }

        try
        {
            // A failed scale throws out of the fill, and the factory returns the storage.
            return CpuVideoFrame.Create(
                PixelFormat.Bgra32,
                width,
                height,
                pts,
                duration,
                timeline,
                (SwsCtx: swsCtx, Input: input, Source: cpuPtr),
                static (planes, s) => ScaleInto(planes, s.SwsCtx, s.Input, s.Source),
                sampleAspectRatio: sampleAspectRatio,
                rotation: rotation
            );
        }
        finally
        {
            FFSwScale.sws_freeContext(swsCtx);
        }
    }

    private static void ScaleInto(CpuVideoFramePlanes planes, nint swsCtx, BgraConversionInput input, nint source)
    {
        // Through BgraConversion, as the decoder does (#724, #792).
        int rows = BgraConversion.Convert(swsCtx, input, source, planes.Y, planes.Width, planes.StrideY);

        if (rows <= 0)
        {
            throw new InvalidOperationException(
                $"sws_scale returned {rows} rows for {planes.Width}x{planes.Height} GPU readback."
            );
        }

        FrameCopyMetrics.Record(FrameCopySite.ReadbackConvert);
    }
}
