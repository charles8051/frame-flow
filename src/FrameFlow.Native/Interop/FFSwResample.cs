// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Source-generated P/Invoke declarations for <c>libswresample</c>.
/// </summary>
/// <remarks>
/// Phase 04 surface: context allocation/init/free and the core convert operation.
/// <para>
/// All pointers here are opaque SwrContext pointers or raw sample buffer pointers.
/// Callers inside <c>FrameFlow.Decoding</c> must wrap these in <see cref="SwrContextHandle"/>
/// and manage buffer lifetimes explicitly. Native pointers must never cross the
/// <c>FrameFlow.Native</c> / <c>FrameFlow.Decoding</c> boundary into higher layers (ADR-0005).
/// </para>
/// </remarks>
internal static partial class FFSwResample
{
    /// <summary>
    /// Allocates a new <c>SwrContext</c>. The returned pointer is zero/null on failure.
    /// Call <see cref="swr_free"/> to release when finished; prefer <see cref="SwrContextHandle"/>.
    /// </summary>
    [LibraryImport("swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint swr_alloc();

    /// <summary>
    /// Initialises a previously configured <c>SwrContext</c>.
    /// Returns 0 on success, a negative AVERROR code on failure.
    /// Must be called after all <c>av_opt_set_*</c> options have been applied.
    /// </summary>
    [LibraryImport("swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int swr_init(nint ctx);

    /// <summary>
    /// Frees a <c>SwrContext</c> and sets the pointer to null.
    /// Safe to call with a null <paramref name="ctx"/>.
    /// </summary>
    [LibraryImport("swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void swr_free(ref nint ctx);

    /// <summary>
    /// Converts audio samples.
    /// </summary>
    /// <param name="ctx">Initialised <c>SwrContext</c>.</param>
    /// <param name="output">
    /// Pointer to output buffer pointer array (<c>uint8_t**</c>). For interleaved output,
    /// pass a pointer to a single buffer pointer. The caller must ensure the output buffer
    /// is large enough to hold <paramref name="out_count"/> output samples per channel.
    /// </param>
    /// <param name="out_count">Maximum number of output samples per channel.</param>
    /// <param name="input">
    /// Pointer to input buffer pointer array (<c>const uint8_t**</c>). For interleaved input,
    /// this is a pointer to a single buffer pointer. For planar input, this is a pointer to
    /// an array of per-channel plane pointers. Pass <c>frame.extended_data</c> directly.
    /// Pass <see cref="nint.Zero"/> with <paramref name="in_count"/> = 0 to flush.
    /// </param>
    /// <param name="in_count">Number of input samples per channel. Zero flushes the resampler.</param>
    /// <returns>
    /// The number of samples output per channel on success, or a negative AVERROR code on failure.
    /// </returns>
    [LibraryImport("swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int swr_convert(
        nint ctx,
        ref nint output,
        int out_count,
        nint input,
        int in_count
    );

    /// <summary>
    /// Returns the delay (in samples at the input sample rate) currently buffered in the resampler.
    /// Used to calculate the maximum output sample count needed for a flush.
    /// </summary>
    [LibraryImport("swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial long swr_get_delay(nint ctx, long base_rate);
}
