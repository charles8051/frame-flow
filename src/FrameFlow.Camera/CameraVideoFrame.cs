// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// Adapts a <see cref="ICameraFrame"/> to <see cref="IVideoFrame"/> so live
/// camera capture can flow through the same pipeline as decoded video
/// frames. Companion to <see cref="CameraFrameAdapter"/>: the latter
/// implements the substrate's <c>IFrame</c>+<c>IRefCounted</c> contract;
/// this implements FrameFlow.Media's richer <c>IVideoFrame</c> (with
/// pixel-format metadata, plane access, etc.) so existing video sinks
/// and operators consume camera frames without modification.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero-copy view.</b> <see cref="AsCpu"/> returns a
/// <see cref="CpuFrameData"/> over the camera frame's <c>ContiguousBuffer</c>
/// directly — no pixel copy. The view is valid until the wrapper is
/// disposed; sinks that need an independent lifetime call
/// <see cref="VideoFrameExtensions.CloneCpu"/> which performs the deep
/// copy.
/// </para>
/// <para>
/// <b>Pixel format.</b> V1 maps a handful of common camera formats
/// (<see cref="CameraPixelFormat.Bgra32"/>, <see cref="CameraPixelFormat.Rgba32"/>,
/// <see cref="CameraPixelFormat.Nv12"/>) to their FrameFlow.Media
/// counterparts. Other camera-side formats (MJPEG, planar YUV variants
/// other than NV12) need an upstream conversion operator before they
/// reach this wrapper. The demo example opens the camera in BGRA32
/// where possible.
/// </para>
/// <para>
/// <b>Ref counting.</b> Each <see cref="CameraVideoFrame"/> instance
/// holds exactly one ref on the inner <see cref="ICameraFrame"/>. The
/// wrapper does not track its own refcount separately —
/// <see cref="AddRef"/> calls through to the camera frame's
/// <c>AddRef</c> and returns this same instance, and <see cref="Dispose"/>
/// disposes the camera frame's ref. The discipline lines up with the
/// IVideoFrame contract (one ref per consumer).
/// </para>
/// </remarks>
public sealed class CameraVideoFrame : IVideoFrame
{
    private ICameraFrame? _inner;

    /// <summary>Constructs an adapter that adopts one ref on <paramref name="inner"/>.</summary>
    public CameraVideoFrame(ICameraFrame inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public int Width => Inner.Width;

    /// <inheritdoc />
    public int Height => Inner.Height;

    /// <inheritdoc />
    public TimeSpan Pts => Inner.Timestamp;

    /// <summary>
    /// Display duration. Camera frames are instantaneous samples with
    /// no inherent duration; reported as <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan Duration => TimeSpan.Zero;

    /// <inheritdoc />
    public PixelFormat Format => MapPixelFormat(Inner.PixelFormat);

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    /// <inheritdoc />
    public IVideoFrame AddRef()
    {
        Inner.AddRef();
        return this;
    }

    /// <inheritdoc />
    public CpuFrameData? AsCpu()
    {
        var frame = _inner;
        if (frame is null)
            return null;
        return BuildView(frame);
    }

    /// <inheritdoc />
    public CpuFrameData ToCpu()
    {
        return BuildView(Inner);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var frame = Interlocked.Exchange(ref _inner, null);
        frame?.Dispose();
    }

    private ICameraFrame Inner =>
        _inner ?? throw new ObjectDisposedException(nameof(CameraVideoFrame));

    private static CpuFrameData BuildView(ICameraFrame frame)
    {
        // Use the camera's own GetPlane() rather than re-deriving strides
        // from Width × bpp — the producer (MF / V4L / AVF backend) is the
        // authoritative source for stride, including padding that we
        // can't know about (USB capture often pads rows to a power-of-2
        // boundary). Mis-stating stride here yields the "purple boxes"
        // failure mode where swscale reads sideways through the buffer.
        var plane0 = frame.GetPlane(0);

        return frame.PixelFormat switch
        {
            // Semi-planar YUV 4:2:0: plane 0 = Y, plane 1 = interleaved
            // UV. swscale's NV12 input expects exactly this two-plane
            // shape — plane Y in srcSlice[0] / strideY, interleaved UV
            // in srcSlice[1] / strideU. srcSlice[2] is unused for NV12.
            CameraPixelFormat.Nv12 => BuildNv12View(frame, plane0),

            // Packed single-plane formats: BGRA32, RGBA32, ARGB32,
            // Bgr24/Rgb24, YUYV/UYVY 4:2:2, Gray8/Gray16. All have
            // exactly one plane; U/V slots stay empty.
            _ => new CpuFrameData(
                PlaneY: plane0.Buffer,
                PlaneU: ReadOnlyMemory<byte>.Empty,
                PlaneV: ReadOnlyMemory<byte>.Empty,
                StrideY: plane0.Stride,
                StrideU: 0,
                StrideV: 0,
                Width: frame.Width,
                Height: frame.Height),
        };
    }

    private static CpuFrameData BuildNv12View(ICameraFrame frame, CameraPlane y)
    {
        // NV12 is semi-planar: plane 0 holds Y, plane 1 holds interleaved
        // U/V at half height. Some backends only expose ContiguousBuffer
        // (PlaneCount=1) for NV12 — in that case we recover the UV plane
        // by slicing into the contiguous buffer at Y's plane size.
        if (frame.PlaneCount >= 2)
        {
            var uv = frame.GetPlane(1);
            return new CpuFrameData(
                PlaneY: y.Buffer,
                PlaneU: uv.Buffer,
                PlaneV: ReadOnlyMemory<byte>.Empty,
                StrideY: y.Stride,
                StrideU: uv.Stride,
                StrideV: 0,
                Width: frame.Width,
                Height: frame.Height);
        }

        // Single-plane NV12 surface — derive UV by slicing the contiguous
        // buffer. Y occupies (stride × height) bytes; UV starts there and
        // also has full row stride (interleaved Cb/Cr).
        var contiguous = frame.ContiguousBuffer;
        var yByteCount = y.Stride * frame.Height;
        if (contiguous.Length < yByteCount)
        {
            throw new InvalidOperationException(
                $"NV12 frame's ContiguousBuffer ({contiguous.Length} bytes) is smaller than "
                + $"the Y plane requires ({yByteCount} bytes for {y.Stride} stride × {frame.Height} rows). "
                + "Backend appears to be reporting an inconsistent stride.");
        }
        return new CpuFrameData(
            PlaneY: contiguous[..yByteCount],
            PlaneU: contiguous[yByteCount..],
            PlaneV: ReadOnlyMemory<byte>.Empty,
            StrideY: y.Stride,
            StrideU: y.Stride, // interleaved UV uses full row stride
            StrideV: 0,
            Width: frame.Width,
            Height: frame.Height);
    }

    private static PixelFormat MapPixelFormat(CameraPixelFormat cf) =>
        cf switch
        {
            CameraPixelFormat.Bgra32 => PixelFormat.Bgra32,
            CameraPixelFormat.Rgba32 => PixelFormat.Rgba32,
            CameraPixelFormat.Nv12 => PixelFormat.Nv12,
            CameraPixelFormat.Yuy2 => PixelFormat.Yuyv422,
            CameraPixelFormat.Uyvy => PixelFormat.Uyvy422,
            _ => throw new NotSupportedException(
                $"Camera pixel format {cf} has no FrameFlow.Media analog. Constrain the "
                + "camera session to a supported format (Bgra32 / Rgba32 / Nv12 / Yuy2 / Uyvy) "
                + "via the fluent CameraSession.For(device).AllowOnlyPixelFormats(...) builder."),
        };
}
