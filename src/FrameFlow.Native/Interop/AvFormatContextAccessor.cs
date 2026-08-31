// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Provides safe, documented read access to the fields of a native <c>AVFormatContext</c>
/// struct that are needed for stream enumeration and packet routing.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Unsafe.AsRef{T}"/> to overlay the <c>FFmpeg.AutoGen.Abstractions.AVFormatContext</c>
/// struct definition onto the raw pointer. Field positions are guaranteed correct for FFmpeg 7.1
/// by the AutoGen binding generator, which derives them from the FFmpeg 7.1 C headers.
/// </para>
/// <para>
/// This type does not own the native pointer. The caller is responsible for keeping the
/// backing <see cref="FormatContextHandle"/> alive for the duration of any access.
/// </para>
/// </remarks>
internal readonly unsafe ref struct AvFormatContextAccessor
{
    private readonly byte* _ptr;

    internal AvFormatContextAccessor(nint fmtCtx)
    {
        _ptr = (byte*)fmtCtx;
    }

    /// <summary>Number of streams in the container.</summary>
    internal uint NbStreams
    {
        get
        {
            ref AVFormatContext ctx = ref Unsafe.AsRef<AVFormatContext>((void*)_ptr);
            return ctx.nb_streams;
        }
    }

    /// <summary>Container duration in microseconds (AV_TIME_BASE = 1_000_000).</summary>
    internal long Duration
    {
        get
        {
            ref AVFormatContext ctx = ref Unsafe.AsRef<AVFormatContext>((void*)_ptr);
            return ctx.duration;
        }
    }

    /// <summary>
    /// Returns the <c>AVStream*</c> for the stream at <paramref name="index"/>.
    /// </summary>
    internal nint GetStream(int index)
    {
        ref AVFormatContext ctx = ref Unsafe.AsRef<AVFormatContext>((void*)_ptr);
        // ctx.streams is AVStream** — an array of pointers to AVStream structs.
        AVStream** streams = ctx.streams;
        return (nint)streams[index];
    }
}

/// <summary>
/// Provides safe, documented read access to the fields of a native <c>AVStream</c>
/// struct needed for codec identification and time base discovery.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Unsafe.AsRef{T}"/> to overlay the <c>FFmpeg.AutoGen.Abstractions.AVStream</c>
/// struct definition onto the raw pointer. Field positions are guaranteed correct for FFmpeg 7.1.
/// </para>
/// <para>
/// This type does not own the native pointer. The caller is responsible for keeping the
/// backing allocation alive for the duration of any access.
/// </para>
/// </remarks>
internal readonly unsafe ref struct AvStreamAccessor
{
    private readonly byte* _ptr;

    internal AvStreamAccessor(nint stream)
    {
        _ptr = (byte*)stream;
    }

    /// <summary>Stream index within the container.</summary>
    internal int Index
    {
        get
        {
            ref AVStream s = ref Unsafe.AsRef<AVStream>((void*)_ptr);
            return s.index;
        }
    }

    /// <summary>Numerator of the stream's time base.</summary>
    internal int TimeBaseNum
    {
        get
        {
            ref AVStream s = ref Unsafe.AsRef<AVStream>((void*)_ptr);
            return s.time_base.num;
        }
    }

    /// <summary>Denominator of the stream's time base.</summary>
    internal int TimeBaseDen
    {
        get
        {
            ref AVStream s = ref Unsafe.AsRef<AVStream>((void*)_ptr);
            return s.time_base.den;
        }
    }

    /// <summary>Numerator of the average frame rate.</summary>
    internal int AvgFrameRateNum
    {
        get
        {
            ref AVStream s = ref Unsafe.AsRef<AVStream>((void*)_ptr);
            return s.avg_frame_rate.num;
        }
    }

    /// <summary>Denominator of the average frame rate.</summary>
    internal int AvgFrameRateDen
    {
        get
        {
            ref AVStream s = ref Unsafe.AsRef<AVStream>((void*)_ptr);
            return s.avg_frame_rate.den;
        }
    }

    /// <summary>Returns the <c>AVCodecParameters*</c> for this stream.</summary>
    internal nint CodecPar
    {
        get
        {
            ref AVStream s = ref Unsafe.AsRef<AVStream>((void*)_ptr);
            return (nint)s.codecpar;
        }
    }
}

/// <summary>
/// Provides safe, documented read access to the fields of a native
/// <c>AVCodecParameters</c> struct needed for codec context initialisation and
/// metadata extraction.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Unsafe.AsRef{T}"/> to overlay the <c>FFmpeg.AutoGen.Abstractions.AVCodecParameters</c>
/// struct definition onto the raw pointer. Field positions are guaranteed correct for FFmpeg 7.1.
/// </para>
/// </remarks>
internal readonly unsafe ref struct AvCodecParAccessor
{
    private readonly byte* _ptr;

    internal AvCodecParAccessor(nint codecPar)
    {
        _ptr = (byte*)codecPar;
    }

    /// <summary>Media type (AVMEDIA_TYPE_VIDEO = 0, AVMEDIA_TYPE_AUDIO = 1).</summary>
    internal int CodecType
    {
        get
        {
            ref AVCodecParameters p = ref Unsafe.AsRef<AVCodecParameters>((void*)_ptr);
            return (int)p.codec_type;
        }
    }

    /// <summary>Codec ID as an FFmpeg <c>AVCodecID</c> integer.</summary>
    internal int CodecId
    {
        get
        {
            ref AVCodecParameters p = ref Unsafe.AsRef<AVCodecParameters>((void*)_ptr);
            return (int)p.codec_id;
        }
    }

    /// <summary>Width in pixels (video streams only).</summary>
    internal int Width
    {
        get
        {
            ref AVCodecParameters p = ref Unsafe.AsRef<AVCodecParameters>((void*)_ptr);
            return p.width;
        }
    }

    /// <summary>Height in pixels (video streams only).</summary>
    internal int Height
    {
        get
        {
            ref AVCodecParameters p = ref Unsafe.AsRef<AVCodecParameters>((void*)_ptr);
            return p.height;
        }
    }

    /// <summary>Sample rate in Hz (audio streams only).</summary>
    internal int SampleRate
    {
        get
        {
            ref AVCodecParameters p = ref Unsafe.AsRef<AVCodecParameters>((void*)_ptr);
            return p.sample_rate;
        }
    }

    /// <summary>Number of channels (audio streams only).</summary>
    internal int NbChannels
    {
        get
        {
            ref AVCodecParameters p = ref Unsafe.AsRef<AVCodecParameters>((void*)_ptr);
            return p.ch_layout.nb_channels;
        }
    }
}

/// <summary>
/// Provides safe, documented read access to the routing fields of a native
/// <c>AVPacket</c> — specifically the stream index and timestamps used for
/// packet demuxing.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Unsafe.AsRef{T}"/> to overlay the <c>FFmpeg.AutoGen.Abstractions.AVPacket</c>
/// struct definition onto the raw pointer. Field positions are guaranteed correct for FFmpeg 7.1.
/// </para>
/// </remarks>
internal readonly unsafe ref struct AvPacketAccessor
{
    private readonly byte* _ptr;

    internal AvPacketAccessor(nint packet)
    {
        _ptr = (byte*)packet;
    }

    /// <summary>Presentation timestamp for this packet.</summary>
    internal long Pts
    {
        get
        {
            ref AVPacket pkt = ref Unsafe.AsRef<AVPacket>((void*)_ptr);
            return pkt.pts;
        }
    }

    /// <summary>Decompression timestamp for this packet.</summary>
    internal long Dts
    {
        get
        {
            ref AVPacket pkt = ref Unsafe.AsRef<AVPacket>((void*)_ptr);
            return pkt.dts;
        }
    }

    /// <summary>Stream index this packet belongs to.</summary>
    internal int StreamIndex
    {
        get
        {
            ref AVPacket pkt = ref Unsafe.AsRef<AVPacket>((void*)_ptr);
            return pkt.stream_index;
        }
    }

    /// <summary>Size of the compressed data in bytes.</summary>
    internal int Size
    {
        get
        {
            ref AVPacket pkt = ref Unsafe.AsRef<AVPacket>((void*)_ptr);
            return pkt.size;
        }
    }
}
