// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Core;

/// <summary>The kinds of threading a decoder implementation supports (its <c>AVCodec.capabilities</c>).</summary>
[Flags]
internal enum CodecThreadSupport
{
    /// <summary>The decoder runs on one thread.</summary>
    None = 0,

    /// <summary>Frames decode in parallel (<c>AV_CODEC_CAP_FRAME_THREADS</c>).</summary>
    Frame = 1,

    /// <summary>Slices of one frame decode in parallel (<c>AV_CODEC_CAP_SLICE_THREADS</c>).</summary>
    Slice = 2,

    /// <summary>
    /// The decoder runs its own threads and only reads the count (<c>AV_CODEC_CAP_OTHER_THREADS</c>,
    /// libdav1d).
    /// </summary>
    Other = 4,
}

/// <summary>The threading kinds a context is opened with. The values are FFmpeg's <c>FF_THREAD_*</c>.</summary>
[Flags]
internal enum DecoderThreadKinds
{
    /// <summary>No FFmpeg-managed threading.</summary>
    None = 0,

    /// <summary><c>FF_THREAD_FRAME</c>.</summary>
    Frame = 1,

    /// <summary><c>FF_THREAD_SLICE</c>.</summary>
    Slice = 2,
}

/// <summary>What a codec context's <c>thread_count</c> and <c>thread_type</c> are set to before it opens.</summary>
internal readonly record struct DecoderThreadSettings(int Count, DecoderThreadKinds Kinds);

/// <summary>
/// How many threads a video decoder opens with, and of which kind (#788). Pure.
/// </summary>
/// <remarks>
/// <para>
/// A context opened through the API defaults to one thread, so software decode used one core.
/// A software context now uses frame and slice threading where the decoder supports them.
/// </para>
/// <para>
/// Frame threading holds up to <c>Count - 1</c> frames in flight, so the first frame after an open
/// or a seek needs that many more packets in. Every frame still comes out, in order: a flush
/// (seek) discards those in flight and the end-of-stream drain returns them. The per-packet discard
/// level (<see cref="PrerollDiscard"/>) still applies per packet, because FFmpeg copies
/// <c>skip_frame</c> to a frame thread when it hands that thread the packet. Slice threading alone
/// adds no delay, but an H.264 stream encoded as one slice a frame, the common case, gains nothing
/// from it. The automatic count is capped at <see cref="AutomaticCap"/> to bound the frames in
/// flight and their memory.
/// </para>
/// <para>
/// A hardware context is left as FFmpeg allocated it: the hwaccel decides its own threading.
/// </para>
/// </remarks>
internal static class DecoderThreading
{
    /// <summary>The option value that lets the decoder choose the count.</summary>
    public const int Automatic = 0;

    /// <summary>The most threads <see cref="Automatic"/> chooses.</summary>
    public const int AutomaticCap = 8;

    /// <summary>The settings for a context, or <see langword="null"/> to leave it as allocated.</summary>
    /// <param name="hardware">Whether the context decodes on a hardware accelerator.</param>
    /// <param name="support">The threading the decoder implementation supports.</param>
    /// <param name="requested">
    /// The caller's thread count: <see cref="Automatic"/>, 1 for one thread, or more for that many.
    /// </param>
    /// <param name="processorCount">The logical processors the process may use.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requested"/> is negative.</exception>
    public static DecoderThreadSettings? For(
        bool hardware,
        CodecThreadSupport support,
        int requested,
        int processorCount
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requested);

        if (hardware)
            return null;

        int count = requested == Automatic ? Math.Clamp(processorCount, 1, AutomaticCap) : requested;
        if (count == 1 || support == CodecThreadSupport.None)
            return new DecoderThreadSettings(1, DecoderThreadKinds.None);

        var kinds = DecoderThreadKinds.None;
        if (support.HasFlag(CodecThreadSupport.Frame))
            kinds |= DecoderThreadKinds.Frame;
        if (support.HasFlag(CodecThreadSupport.Slice))
            kinds |= DecoderThreadKinds.Slice;

        return new DecoderThreadSettings(count, kinds);
    }
}
