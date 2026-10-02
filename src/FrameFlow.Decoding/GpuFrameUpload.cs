// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding;

/// <summary>
/// Uploads CPU frames to a <see cref="HardwareDevice"/> (#293): each is converted to NV12 and
/// copied into a surface of a pool this object keeps on the device, so the result is a
/// <see cref="GpuVideoFrame"/> in the shape a decoder on that device produces. The shell of
/// <c>VideoOperators.ToGpu</c>.
/// </summary>
/// <remarks>
/// <para>
/// The pool is sized to the frames: rebuilt when a frame's size changes, and rounded up to even
/// dimensions, which NV12 surfaces need. It grows by a surface for each uploaded frame still held
/// downstream, as a D3D12VA decoder's does, and nothing bounds it (#419).
/// </para>
/// <para>
/// The native pool and converter are released by their handles' finalizers, since a graph
/// disposes none of its operators. An uploaded frame holds its own reference to the pool.
/// </para>
/// <para>
/// The conversion is BT.601 studio range, swscale's default for an RGB source. That is how the
/// rest of the library reads NV12 when nothing says otherwise: <see cref="GpuVideoFrame.ReadbackToCpuBgra32"/>
/// converts back with the same default, and <c>ImageToTensorOptions</c> defaults to BT.601 limited
/// range. The presenters assume BT.709. Frames carry no colour metadata to choose between them
/// (#388).
/// </para>
/// <para>Not thread-safe: one upload at a time, as an operator node calls it.</para>
/// </remarks>
internal sealed unsafe class GpuFrameUpload
{
    private readonly HardwareDevice _device;
    private BufferRefHandle? _pool;
    private int _poolWidth;
    private int _poolHeight;
    private SwsContextHandle? _sws;
    private (int Width, int Height, int Format) _swsKey;

    public GpuFrameUpload(HardwareDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>The device frames are uploaded to.</summary>
    public HardwareDevice Device => _device;

    /// <summary>
    /// Uploads <paramref name="frame"/>, which is in CPU memory, and returns the uploaded frame.
    /// The input is not consumed.
    /// </summary>
    /// <exception cref="ArgumentException">The frame is not in CPU memory.</exception>
    /// <exception cref="NotSupportedException">
    /// The frame's pixel format has no conversion to NV12, or the device's pools cannot hold NV12.
    /// </exception>
    /// <exception cref="InvalidOperationException">An FFmpeg call failed.</exception>
    public GpuVideoFrame Upload(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var cpu = frame.AsCpu()
            ?? throw new ArgumentException($"A {frame.MemoryDomain} frame is not in CPU memory.", nameof(frame));

        int width = frame.Width, height = frame.Height;
        EnsurePool(width, height);

        nint software = FFAvUtil.av_frame_alloc();
        if (software == nint.Zero)
            throw new InvalidOperationException("av_frame_alloc returned null.");
        using var softwareHandle = new FrameHandle(software);

        ref AVFrame target = ref Unsafe.AsRef<AVFrame>((void*)software);
        target.format = FFSwScale.AvPixFmtNv12;
        target.width = _poolWidth;
        target.height = _poolHeight;
        Check(FFAvUtil.av_frame_get_buffer(software, 0), "av_frame_get_buffer");
        ConvertToNv12(cpu, frame.Format, width, height, software);

        nint hardware = FFAvUtil.av_frame_alloc();
        if (hardware == nint.Zero)
            throw new InvalidOperationException("av_frame_alloc returned null.");
        try
        {
            Check(FFAvUtil.av_hwframe_get_buffer(_pool!.DangerousGetHandle(), hardware, 0), "av_hwframe_get_buffer");
            Check(FFAvUtil.av_hwframe_transfer_data(hardware, software, 0), "av_hwframe_transfer_data");
            FrameCopyMetrics.Record(FrameCopySite.Upload);
            var uploaded = GpuVideoFrame.FromOwnedAvFrame(
                hardware, width, height, PixelFormat.Nv12, frame.Pts, frame.Duration, _device.Backend,
                frame.SampleAspectRatio, frame.Rotation);
            hardware = nint.Zero;
            return uploaded;
        }
        finally
        {
            if (hardware != nint.Zero)
                FFAvUtil.av_frame_free(ref hardware);
        }
    }

    /// <summary>A pool of NV12 surfaces on the device, at least <paramref name="width"/> by <paramref name="height"/>.</summary>
    private void EnsurePool(int width, int height)
    {
        int poolWidth = (width + 1) & ~1, poolHeight = (height + 1) & ~1;
        if (_pool is not null && poolWidth == _poolWidth && poolHeight == _poolHeight)
            return;

        int hardwareFormat = HardwareFormat();
        nint device = _device.Borrow();
        nint pool;
        try
        {
            pool = FFAvUtil.av_hwframe_ctx_alloc(device);
        }
        finally
        {
            // The pool holds a reference of its own.
            FFAvUtil.av_buffer_unref(ref device);
        }

        if (pool == nint.Zero)
            throw new InvalidOperationException("av_hwframe_ctx_alloc returned null.");

        var poolHandle = new BufferRefHandle(pool);
        try
        {
            var context = (AVHWFramesContext*)((AVBufferRef*)pool)->data;
            context->format = (AVPixelFormat)hardwareFormat;
            context->sw_format = (AVPixelFormat)FFSwScale.AvPixFmtNv12;
            context->width = poolWidth;
            context->height = poolHeight;
            // Zero surfaces up front: the pool allocates one when every surface it has is held.
            context->initial_pool_size = 0;
            Check(FFAvUtil.av_hwframe_ctx_init(pool), "av_hwframe_ctx_init");
        }
        catch
        {
            poolHandle.Dispose();
            throw;
        }

        _pool?.Dispose();
        _pool = poolHandle;
        _poolWidth = poolWidth;
        _poolHeight = poolHeight;
    }

    /// <summary>The device's surface format for a frames pool, from its constraints, once NV12 is confirmed to fit.</summary>
    private int HardwareFormat()
    {
        nint device = _device.Borrow();
        nint constraints = nint.Zero;
        try
        {
            constraints = FFAvUtil.av_hwdevice_get_hwframe_constraints(device, nint.Zero);
            if (constraints == nint.Zero)
                throw new NotSupportedException($"A {_device.Backend} device reports no frames constraints.");

            var c = (AVHWFramesConstraints*)constraints;
            if (c->valid_hw_formats is null || *c->valid_hw_formats == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new NotSupportedException($"A {_device.Backend} device reports no surface format.");
            if (!Lists(c->valid_sw_formats, FFSwScale.AvPixFmtNv12))
                throw new NotSupportedException($"A {_device.Backend} device's pools cannot hold NV12.");
            return (int)*c->valid_hw_formats;
        }
        finally
        {
            if (constraints != nint.Zero)
                FFAvUtil.av_hwframe_constraints_free(ref constraints);
            FFAvUtil.av_buffer_unref(ref device);
        }
    }

    /// <summary>True when the <c>AV_PIX_FMT_NONE</c>-terminated list names <paramref name="format"/>, or when there is no list.</summary>
    private static bool Lists(AVPixelFormat* formats, int format)
    {
        if (formats is null)
            return true;
        for (; *formats != AVPixelFormat.AV_PIX_FMT_NONE; formats++)
        {
            if ((int)*formats == format)
                return true;
        }

        return false;
    }

    /// <summary>Converts <paramref name="cpu"/> into the NV12 <paramref name="software"/> frame with <c>sws_scale</c>.</summary>
    private void ConvertToNv12(CpuFrameData cpu, PixelFormat format, int width, int height, nint software)
    {
        int source = SourceFormat(format);
        if (_sws is null || _swsKey != (width, height, source))
        {
            nint context = FFSwScale.sws_getContext(
                width, height, source, width, height, FFSwScale.AvPixFmtNv12, FFSwScale.SwsBilinear,
                srcFilter: nint.Zero, dstFilter: nint.Zero, param: nint.Zero);
            if (context == nint.Zero)
                throw new InvalidOperationException($"sws_getContext returned null for {width}x{height} {format} to NV12.");
            _sws?.Dispose();
            _sws = new SwsContextHandle(context);
            _swsKey = (width, height, source);
        }

        using var pinY = cpu.PlaneY.Pin();
        using var pinU = cpu.PlaneU.Pin();
        using var pinV = cpu.PlaneV.Pin();
        byte** sourceSlice = stackalloc byte*[4] { (byte*)pinY.Pointer, (byte*)pinU.Pointer, (byte*)pinV.Pointer, null };
        int* sourceStrides = stackalloc int[4] { cpu.StrideY, cpu.StrideU, cpu.StrideV, 0 };

        var target = new AvFrameAccessor(software);
        byte** targetSlice = stackalloc byte*[4] { target.GetDataPointer(0), target.GetDataPointer(1), null, null };
        int* targetStrides = stackalloc int[4] { target.GetLineSize(0), target.GetLineSize(1), 0, 0 };

        int rows = FFSwScale.sws_scale(
            _sws.DangerousGetHandle(), sourceSlice, sourceStrides, 0, height, targetSlice, targetStrides);
        if (rows <= 0)
            throw new InvalidOperationException($"sws_scale returned {rows} rows converting {width}x{height} {format} to NV12.");
        FrameCopyMetrics.Record(FrameCopySite.UploadConvert);
    }

    private static int SourceFormat(PixelFormat format) =>
        format switch
        {
            PixelFormat.Bgra32 => FFSwScale.AvPixFmtBgra,
            PixelFormat.Rgba32 => FFSwScale.AvPixFmtRgba,
            PixelFormat.Yuv420P => FFSwScale.AvPixFmtYuv420P,
            PixelFormat.Nv12 => FFSwScale.AvPixFmtNv12,
            PixelFormat.Yuyv422 => FFSwScale.AvPixFmtYuyv422,
            PixelFormat.Uyvy422 => FFSwScale.AvPixFmtUyvy422,
            _ => throw new NotSupportedException($"A {format} frame has no conversion to NV12 here."),
        };

    private static void Check(int result, string call)
    {
        if (result < 0)
            throw new InvalidOperationException($"{call} failed with code {result}.");
    }
}
