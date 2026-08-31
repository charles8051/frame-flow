// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Write access to the fields of a native <c>AVCodecContext</c> that an
/// encoder must configure before <c>avcodec_open2</c>, plus read access to the
/// fields the muxer needs afterwards.
/// </summary>
/// <remarks>
/// Overlays the <c>FFmpeg.AutoGen.Abstractions.AVCodecContext</c> struct onto a
/// raw pointer via <see cref="Unsafe.AsRef{T}"/> (ADR-0017), mirroring the
/// read-only accessors in <c>AvFrameAccessor</c> / <c>AvFormatContextAccessor</c>.
/// Does not own the pointer; the caller keeps the backing
/// <see cref="CodecContextHandle"/> alive for the duration of any access.
/// </remarks>
internal readonly unsafe ref struct AvCodecContextWriter
{
    private readonly byte* _ptr;

    internal AvCodecContextWriter(nint codecCtx)
    {
        _ptr = (byte*)codecCtx;
    }

    private ref AVCodecContext Ctx => ref Unsafe.AsRef<AVCodecContext>((void*)_ptr);

    /// <summary>Sets the coded picture width in pixels.</summary>
    internal int Width
    {
        set => Ctx.width = value;
    }

    /// <summary>Sets the coded picture height in pixels.</summary>
    internal int Height
    {
        set => Ctx.height = value;
    }

    /// <summary>Sets the pixel format as an FFmpeg <c>AVPixelFormat</c> integer.</summary>
    internal int PixelFormat
    {
        set => Ctx.pix_fmt = (AVPixelFormat)value;
    }

    /// <summary>Sets the 64-bit target bitrate in bits per second.</summary>
    internal long BitRate
    {
        set => Ctx.bit_rate = value;
    }

    /// <summary>Sets the group-of-pictures size (keyframe interval in frames).</summary>
    internal int GopSize
    {
        set => Ctx.gop_size = value;
    }

    /// <summary>
    /// Sets the maximum number of B-frames. Must be 0 for libopenh264, which
    /// implements Baseline/Constrained-Baseline only and rejects B-frames at
    /// open time.
    /// </summary>
    internal int MaxBFrames
    {
        set => Ctx.max_b_frames = value;
    }

    /// <summary>Sets the colour range as an FFmpeg <c>AVColorRange</c> integer.</summary>
    internal int ColorRange
    {
        set => Ctx.color_range = (AVColorRange)value;
    }

    /// <summary>Sets the encoder time base (the unit of packet PTS/DTS).</summary>
    internal void SetTimeBase(int num, int den)
    {
        ref AVCodecContext c = ref Ctx;
        c.time_base.num = num;
        c.time_base.den = den;
    }

    /// <summary>Sets the nominal frame rate hint.</summary>
    internal void SetFrameRate(int num, int den)
    {
        ref AVCodecContext c = ref Ctx;
        c.framerate.num = num;
        c.framerate.den = den;
    }

    /// <summary>Bitwise-ORs additional codec flags (e.g. global header).</summary>
    internal void AddFlags(int flags)
    {
        ref AVCodecContext c = ref Ctx;
        c.flags |= flags;
    }

    /// <summary>Numerator of the configured encoder time base.</summary>
    internal int TimeBaseNum => Ctx.time_base.num;

    /// <summary>Denominator of the configured encoder time base.</summary>
    internal int TimeBaseDen => Ctx.time_base.den;
}

/// <summary>
/// Access to the output-side fields of a native <c>AVFormatContext</c>: the
/// output format's flags, the AVIO context pointer (<c>pb</c>), and stream
/// enumeration.
/// </summary>
internal readonly unsafe ref struct AvOutputFormatContextAccessor
{
    private readonly byte* _ptr;

    internal AvOutputFormatContextAccessor(nint fmtCtx)
    {
        _ptr = (byte*)fmtCtx;
    }

    private ref AVFormatContext Ctx => ref Unsafe.AsRef<AVFormatContext>((void*)_ptr);

    /// <summary>
    /// Reads the <c>flags</c> field of the selected output format
    /// (<c>AVOutputFormat.flags</c>) — e.g. <c>AVFMT_GLOBALHEADER</c>,
    /// <c>AVFMT_NOFILE</c>.
    /// </summary>
    internal int OutputFormatFlags
    {
        get
        {
            ref AVFormatContext ctx = ref Ctx;
            var ofmt = ctx.oformat;
            if (ofmt is null)
                return 0;
            return ofmt->flags;
        }
    }

    /// <summary>Gets or sets the AVIO context pointer (<c>pb</c>).</summary>
    internal nint Pb
    {
        get => (nint)Ctx.pb;
        set => Ctx.pb = (AVIOContext*)value;
    }
}

/// <summary>
/// Read/write access to the fields of a native <c>AVStream</c> needed during
/// muxing: index, codec-parameters pointer, and time base.
/// </summary>
internal readonly unsafe ref struct AvStreamWriter
{
    private readonly byte* _ptr;

    internal AvStreamWriter(nint stream)
    {
        _ptr = (byte*)stream;
    }

    private ref AVStream Stream => ref Unsafe.AsRef<AVStream>((void*)_ptr);

    /// <summary>Stream index within the container (assigned by the muxer).</summary>
    internal int Index => Stream.index;

    /// <summary>The stream's <c>AVCodecParameters*</c>.</summary>
    internal nint CodecPar => (nint)Stream.codecpar;

    /// <summary>Sets the stream time base (the unit muxed packet timestamps use).</summary>
    internal void SetTimeBase(int num, int den)
    {
        ref AVStream s = ref Stream;
        s.time_base.num = num;
        s.time_base.den = den;
    }

    /// <summary>Numerator of the stream time base (authoritative after write_header).</summary>
    internal int TimeBaseNum => Stream.time_base.num;

    /// <summary>Denominator of the stream time base (authoritative after write_header).</summary>
    internal int TimeBaseDen => Stream.time_base.den;
}

/// <summary>
/// Write access to the fields of the encoder's reusable source
/// <c>AVFrame</c>, plus read access to its data planes for filling via
/// <c>sws_scale</c>.
/// </summary>
internal readonly unsafe ref struct AvFrameWriter
{
    private readonly byte* _ptr;

    internal AvFrameWriter(nint framePtr)
    {
        _ptr = (byte*)framePtr;
    }

    private ref AVFrame Frame => ref Unsafe.AsRef<AVFrame>((void*)_ptr);

    /// <summary>Sets the frame width in pixels.</summary>
    internal int Width
    {
        set => Frame.width = value;
    }

    /// <summary>Sets the frame height in pixels.</summary>
    internal int Height
    {
        set => Frame.height = value;
    }

    /// <summary>Sets the frame pixel/sample format as an FFmpeg integer.</summary>
    internal int Format
    {
        set => Frame.format = value;
    }

    /// <summary>Sets the presentation timestamp (in the encoder time base).</summary>
    internal long Pts
    {
        set => Frame.pts = value;
    }

    /// <summary>Returns the data pointer for plane <paramref name="planeIndex"/> (0–7).</summary>
    internal byte* GetDataPointer(int planeIndex)
    {
        ref AVFrame f = ref Frame;
        fixed (AVFrame* fp = &f)
        {
            return ((byte**)&fp->data)[planeIndex];
        }
    }

    /// <summary>Returns the stride (bytes per row) for plane <paramref name="planeIndex"/>.</summary>
    internal int GetLineSize(int planeIndex)
    {
        ref AVFrame f = ref Frame;
        fixed (AVFrame* fp = &f)
        {
            return ((int*)&fp->linesize)[planeIndex];
        }
    }
}

/// <summary>
/// Read/write access to the routing and timestamp fields of an encoded
/// <c>AVPacket</c>, plus access to its data buffer for copying into managed
/// memory.
/// </summary>
internal readonly unsafe ref struct AvEncodedPacketAccessor
{
    private readonly byte* _ptr;

    internal AvEncodedPacketAccessor(nint packet)
    {
        _ptr = (byte*)packet;
    }

    private ref AVPacket Pkt => ref Unsafe.AsRef<AVPacket>((void*)_ptr);

    /// <summary>Presentation timestamp (in the encoder time base).</summary>
    internal long Pts
    {
        get => Pkt.pts;
        set => Pkt.pts = value;
    }

    /// <summary>Decompression timestamp (in the encoder time base).</summary>
    internal long Dts
    {
        get => Pkt.dts;
        set => Pkt.dts = value;
    }

    /// <summary>Packet duration (in the encoder time base).</summary>
    internal long Duration
    {
        get => Pkt.duration;
        set => Pkt.duration = value;
    }

    /// <summary>Destination stream index in the output container.</summary>
    internal int StreamIndex
    {
        get => Pkt.stream_index;
        set => Pkt.stream_index = value;
    }

    /// <summary>Compressed payload size in bytes.</summary>
    internal int Size => Pkt.size;

    /// <summary>Packet flags (e.g. <c>AV_PKT_FLAG_KEY</c>).</summary>
    internal int Flags
    {
        get => Pkt.flags;
        set => Pkt.flags = value;
    }

    /// <summary>Pointer to the compressed payload (<c>size</c> bytes).</summary>
    internal byte* Data => Pkt.data;

    /// <summary>Copies <paramref name="source"/> into the packet's payload buffer (which must already be at least <c>source.Length</c> bytes).</summary>
    internal void WriteData(ReadOnlySpan<byte> source)
    {
        ref AVPacket p = ref Pkt;
        source.CopyTo(new Span<byte>(p.data, p.size));
    }

    /// <summary>Copies the packet's compressed payload into a new managed array.</summary>
    internal byte[] CopyData()
    {
        ref AVPacket p = ref Pkt;
        int size = p.size;
        if (size <= 0 || p.data is null)
            return [];
        var managed = new byte[size];
        new ReadOnlySpan<byte>(p.data, size).CopyTo(managed);
        return managed;
    }
}
