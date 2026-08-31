// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Provides safe, documented read access to fields of a native <c>AVFrame</c> struct
/// via its pointer.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Unsafe.AsRef{T}"/> to overlay the <c>FFmpeg.AutoGen.Abstractions.AVFrame</c>
/// struct definition onto the raw pointer. Field positions are guaranteed correct for FFmpeg 7.1
/// by the AutoGen binding generator, which derives them from the FFmpeg 7.1 C headers.
/// </para>
/// <para>
/// Key field positions in the AutoGen binding (FFmpeg 7.1):
/// <code>
///   data[8]           — offset 0   (8 × 8-byte plane pointers)
///   linesize[8]       — offset 64  (8 × 4-byte strides)
///   extended_data**   — offset 96
///   width             — offset 104
///   height            — offset 108
///   nb_samples        — offset 112
///   format            — offset 116
///   pts (int64)       — offset 136
///   time_base         — offset 152
///   sample_rate       — offset 192
///   ch_layout         — offset 408 (AVChannelLayout: order=+0, nb_channels=+4)
/// </code>
/// </para>
/// <para>
/// This type does not own the native pointer; it is a temporary view used only within
/// the decoder's decode cycle. The caller is responsible for keeping the backing
/// <see cref="FrameHandle"/> alive for the duration of any access.
/// </para>
/// </remarks>
internal readonly unsafe ref struct AvFrameAccessor
{
    private readonly byte* _ptr;

    /// <summary>
    /// Initializes the accessor over the given <c>AVFrame*</c>.
    /// </summary>
    /// <param name="framePtr">Non-null pointer to a live <c>AVFrame</c>.</param>
    internal AvFrameAccessor(nint framePtr)
    {
        _ptr = (byte*)framePtr;
    }

    /// <summary>Width of the frame in pixels.</summary>
    internal int Width
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.width;
        }
    }

    /// <summary>Height of the frame in pixels.</summary>
    internal int Height
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.height;
        }
    }

    /// <summary>
    /// Pixel/sample format as an FFmpeg integer.
    /// For video: <c>AVPixelFormat</c>. For audio: <c>AVSampleFormat</c>.
    /// </summary>
    internal int Format
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.format;
        }
    }

    /// <summary>
    /// Presentation timestamp in the stream's time base.
    /// Returns <see cref="FFAvUtil.AvNoPtsValue"/> if no PTS is available.
    /// </summary>
    internal long Pts
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.pts;
        }
    }

    /// <summary>Number of audio samples per channel in this frame.</summary>
    internal int NbSamples
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.nb_samples;
        }
    }

    /// <summary>Audio sample rate in Hz.</summary>
    internal int SampleRate
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.sample_rate;
        }
    }

    /// <summary>Number of audio channels.</summary>
    internal int NbChannels
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.ch_layout.nb_channels;
        }
    }

    /// <summary>
    /// Numerator of the frame's time base (<c>AVRational.num</c>).
    /// </summary>
    internal int TimeBaseNum
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.time_base.num;
        }
    }

    /// <summary>
    /// Denominator of the frame's time base (<c>AVRational.den</c>).
    /// </summary>
    internal int TimeBaseDen
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return f.time_base.den;
        }
    }

    /// <summary>
    /// Returns the data pointer for the plane at <paramref name="planeIndex"/>.
    /// Valid plane indices are 0–7 (AV_NUM_DATA_POINTERS).
    /// For packed formats such as BGRA, plane 0 holds all pixel data.
    /// </summary>
    internal byte* GetDataPointer(int planeIndex)
    {
        ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
        // data is a fixed-size array of 8 byte* — use unsafe fixed access via the struct ref.
        // The byte_ptr8 type in AutoGen is a blittable fixed-buffer equivalent.
        fixed (AVFrame* fp = &f)
        {
            // data[planeIndex] — each element is a byte* (8 bytes on x64).
            return ((byte**)&fp->data)[planeIndex];
        }
    }

    /// <summary>
    /// Returns the stride (bytes per row) for the plane at <paramref name="planeIndex"/>.
    /// </summary>
    internal int GetLineSize(int planeIndex)
    {
        ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
        fixed (AVFrame* fp = &f)
        {
            // linesize is a fixed-size array of 8 ints following the data pointers.
            return ((int*)&fp->linesize)[planeIndex];
        }
    }

    /// <summary>
    /// For a D3D11VA hardware frame, walks <c>hw_frames_ctx → device_ctx → hwctx
    /// (AVD3D11VADeviceContext) → device</c> and returns the underlying
    /// <c>ID3D11Device*</c> as a raw pointer. This is the decode device the frame's
    /// texture lives on — a <b>stable per-decoder identity</b>: every frame from one
    /// decoder reports the same pointer, and a new decoder (e.g. a player swap onto a
    /// warm sink) reports a different one. A zero-copy presenter uses it to detect that
    /// its color-converter is pinned to a now-disposed decode device and rebuild
    /// (ADR-0064). Returns <see cref="nint.Zero"/> if any link in the chain is null
    /// (not a hardware frame, or not D3D11VA).
    /// </summary>
    internal nint GetD3D11DevicePointer()
    {
        ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
        var framesCtxRef = f.hw_frames_ctx;
        if (framesCtxRef is null)
            return nint.Zero;

        var framesCtx = (AVHWFramesContext*)framesCtxRef->data;
        if (framesCtx is null)
            return nint.Zero;

        var deviceCtx = framesCtx->device_ctx;
        if (deviceCtx is null)
            return nint.Zero;

        var d3d11 = (AVD3D11VADeviceContext*)deviceCtx->hwctx;
        if (d3d11 is null)
            return nint.Zero;

        return (nint)d3d11->device;
    }

    /// <summary>
    /// Returns the <c>extended_data</c> pointer for use with <c>swr_convert</c>.
    /// For planar audio, each element of the pointed array is a pointer to a channel plane.
    /// For packed formats, <c>extended_data[0]</c> equals <c>data[0]</c>.
    /// </summary>
    internal nint ExtendedData
    {
        get
        {
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)_ptr);
            return (nint)f.extended_data;
        }
    }

    /// <summary>
    /// Converts the frame's PTS to a <see cref="TimeSpan"/> using the stream time base.
    /// Returns <see cref="TimeSpan.Zero"/> if PTS is <see cref="FFAvUtil.AvNoPtsValue"/> or
    /// if the time base denominator is zero.
    /// </summary>
    /// <param name="streamTimeBaseNum">Numerator of the stream's time base (e.g. 1).</param>
    /// <param name="streamTimeBaseDen">Denominator of the stream's time base (e.g. 90000).</param>
    internal TimeSpan ComputePresentationTime(int streamTimeBaseNum, int streamTimeBaseDen)
    {
        long pts = Pts;

        if (pts == FFAvUtil.AvNoPtsValue || streamTimeBaseDen == 0)
            return TimeSpan.Zero;

        long microseconds = pts * (long)streamTimeBaseNum * FFAvUtil.AvTimeBase / streamTimeBaseDen;
        return TimeSpan.FromMicroseconds(microseconds);
    }
}
