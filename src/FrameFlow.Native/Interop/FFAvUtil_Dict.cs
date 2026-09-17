// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// The <c>AVDictionary</c> slice of the <c>libavutil</c> P/Invoke surface.
/// </summary>
/// <remarks>
/// <para>
/// An <c>AVDictionary*</c> is how FFmpeg takes string-keyed options into a call. The one
/// caller here is <c>DemuxSessionFactory</c>, which builds a dictionary from a media
/// source's demuxer options, hands it to <c>avformat_open_input</c>, and reads back
/// whatever the demuxer did not recognise.
/// </para>
/// <para>
/// Ownership: the dictionary is the caller's on every path.
/// <c>avformat_open_input</c> consumes the entries it recognises and leaves the rest,
/// including when it fails, so <see cref="av_dict_free"/> has to run whatever happens.
/// </para>
/// </remarks>
internal static partial class FFAvUtil
{
    /// <summary>
    /// Matches any key, so <c>av_dict_get</c> with an empty key walks every entry.
    /// FFmpeg's <c>AV_DICT_IGNORE_SUFFIX</c>.
    /// </summary>
    internal const int AvDictIgnoreSuffix = 2;

    /// <summary>
    /// Sets an entry, allocating the dictionary on the first call when
    /// <paramref name="pm"/> is <see cref="nint.Zero"/>. Returns 0 or a negative AVERROR.
    /// </summary>
    /// <remarks>
    /// Both strings are copied by FFmpeg, because <paramref name="flags"/> is 0 rather
    /// than one of the <c>AV_DICT_DONT_STRDUP_*</c> values that would transfer ownership
    /// of a buffer this side allocated.
    /// </remarks>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_dict_set(
        ref nint pm,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int flags
    );

    /// <summary>
    /// Frees the dictionary and every key and value in it, and sets
    /// <paramref name="pm"/> to <see cref="nint.Zero"/>. Safe on a zero pointer.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void av_dict_free(ref nint pm);

    /// <summary>Number of entries in the dictionary. Zero for a zero pointer.</summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_dict_count(nint m);

    /// <summary>
    /// Returns the <c>AVDictionaryEntry*</c> after <paramref name="prev"/> whose key
    /// matches, or <see cref="nint.Zero"/> at the end. Pass <see cref="nint.Zero"/> for
    /// <paramref name="prev"/> to start, an empty key and
    /// <see cref="AvDictIgnoreSuffix"/> to walk everything.
    /// </summary>
    /// <remarks>
    /// The returned entry and its strings belong to the dictionary and are valid only
    /// until it is modified or freed. <c>key</c> is marshalled as UTF-8 in; the entry
    /// comes back as a raw pointer because its strings are FFmpeg's to free, not ours.
    /// </remarks>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_dict_get(
        nint m,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        nint prev,
        int flags
    );
}
