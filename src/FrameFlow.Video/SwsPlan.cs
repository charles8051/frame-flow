// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Video;

/// <summary>
/// The full set of knobs an FFmpeg <c>SwsContext</c> is built for: the source
/// geometry and pixel format, and the destination geometry and pixel format. A
/// value, threaded as the cached "what the current context was configured for"
/// alongside the native handle the shell owns.
/// </summary>
/// <remarks>
/// <para>
/// This is the input vocabulary of the pure reconfigure predicate
/// (<see cref="SwsPlan.Decide"/>). It carries <b>zero</b> FFmpeg references — the
/// <see cref="PixelFormat"/> is the managed enum, not an <c>AVPixelFormat</c> int —
/// so the reuse-vs-rebuild decision can be exercised with nothing native loaded
/// (§3.3). The managed→<c>AVPixelFormat</c> mapping, and the <c>sws_getContext</c>
/// call, live in the shell.
/// </para>
/// </remarks>
/// <param name="SrcWidth">Source frame width in pixels.</param>
/// <param name="SrcHeight">Source frame height in pixels.</param>
/// <param name="SrcFormat">Source pixel format.</param>
/// <param name="DstWidth">Destination frame width in pixels.</param>
/// <param name="DstHeight">Destination frame height in pixels.</param>
/// <param name="DstFormat">Destination pixel format.</param>
internal readonly record struct SwsConfigKey(
    int SrcWidth,
    int SrcHeight,
    PixelFormat SrcFormat,
    int DstWidth,
    int DstHeight,
    PixelFormat DstFormat
);

/// <summary>What the shell should do with its cached <c>SwsContext</c> for a requested conversion.</summary>
internal enum SwsPlanDecision
{
    /// <summary>The cached context already matches the request; keep using it.</summary>
    Reuse,

    /// <summary>The request differs (or there is no usable cached context); dispose and rebuild.</summary>
    Rebuild,
}

/// <summary>
/// The pure "does the <c>SwsContext</c> need rebuilding?" predicate (§3.3), lifted out
/// of <see cref="SwScaleVideoConverter.EnsureSwsContext"/>'s native call and its six
/// mutable cache writes.
/// </summary>
/// <remarks>
/// <para>
/// Reusing a stale <c>SwsContext</c> across an incompatible frame is unsafe — a stream
/// can change pixel format or dimensions mid-stream, and the target geometry/format can
/// differ from the source. The decision of whether the cached context still fits is a
/// total function over (current cached key) vs (requested key): same shape → reuse,
/// any field different → rebuild. Welding it to <c>sws_getContext</c> + the cache
/// mutation meant the only coverage was an FFmpeg-gated round-trip that asserted output
/// width alone. As a value transform it is exhaustively unit-testable with no FFmpeg.
/// </para>
/// <para>
/// Mirrors the clock-and-state-passed-in shape of <c>LoopStallEvaluator</c> /
/// <c>DecodeProtocol</c>: state (the cached key) and IO (the native context) are the
/// shell's; only the decision is here.
/// </para>
/// </remarks>
internal static class SwsPlan
{
    /// <summary>
    /// Decide whether a context configured for <paramref name="current"/> can be reused for
    /// <paramref name="requested"/>, or must be rebuilt.
    /// </summary>
    /// <param name="current">
    /// The key the shell's live context was last built for, or <see langword="null"/> when the
    /// shell holds no usable context (never built, disposed, or invalidated). A null current
    /// always forces a <see cref="SwsPlanDecision.Rebuild"/>.
    /// </param>
    /// <param name="requested">The conversion the current frame needs.</param>
    /// <returns>
    /// <see cref="SwsPlanDecision.Reuse"/> iff a context exists and every field of
    /// <paramref name="current"/> equals <paramref name="requested"/>; otherwise
    /// <see cref="SwsPlanDecision.Rebuild"/>.
    /// </returns>
    public static SwsPlanDecision Decide(SwsConfigKey? current, SwsConfigKey requested) =>
        current is { } key && key == requested
            ? SwsPlanDecision.Reuse
            : SwsPlanDecision.Rebuild;
}
