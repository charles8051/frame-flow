// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Core;

/// <summary>
/// The offset between a container's timestamp domain and media time (#358).
/// </summary>
/// <remarks>
/// <para>
/// Containers are not required to start at zero. MPEG-TS conventionally starts at 1.4s,
/// and any container can carry an offset. Everything above the demuxer — the pacing
/// clock, seek targets, reported position, sink presentation times — works in media
/// time, which starts at zero. This value is what converts between the two.
/// </para>
/// <para>
/// It is taken from the <i>format</i> start time rather than each stream's own, which
/// matters for A/V sync: the format value is the minimum across streams, so subtracting
/// it moves every stream by the same amount and preserves the relative offset between
/// them. Zeroing each stream independently would align audio and video at the start and
/// discard a real skew.
/// </para>
/// <para>
/// Pure and FFmpeg-free by design: the <c>AV_NOPTS_VALUE</c> sentinel is checked by the
/// caller and passed as a bool, so the conversion arithmetic is a total function over
/// longs that can be exercised with nothing native loaded.
/// </para>
/// </remarks>
/// <param name="Microseconds">
/// The container's start time in microseconds, or zero when there is no offset to apply.
/// </param>
internal readonly record struct MediaTimeOrigin(long Microseconds)
{
    private const long MicrosecondsPerSecond = 1_000_000;

    /// <summary>No offset: container time and media time are the same.</summary>
    public static MediaTimeOrigin Zero { get; } = new(0);

    /// <summary>
    /// The origin for a container reporting <paramref name="microseconds"/> as its start
    /// time, or <see cref="Zero"/> when that value is absent or not positive.
    /// </summary>
    /// <remarks>
    /// A negative start time is deliberately ignored rather than applied. Subtracting it
    /// would push every timestamp later than the clock, which is the failure this type
    /// exists to prevent, and a stream that starts before zero already presents its
    /// opening frames immediately.
    /// </remarks>
    /// <param name="isKnown">
    /// Whether the container reported a start time at all — the caller tests the
    /// <c>AV_NOPTS_VALUE</c> sentinel.
    /// </param>
    /// <param name="microseconds">The reported start time.</param>
    public static MediaTimeOrigin FromContainerStartTime(bool isKnown, long microseconds) =>
        isKnown && microseconds > 0 ? new MediaTimeOrigin(microseconds) : Zero;

    /// <summary>Whether this origin leaves timestamps unchanged.</summary>
    public bool IsZero => Microseconds == 0;

    /// <summary>
    /// This origin expressed in one stream's time base, which is the unit a packet's
    /// <c>pts</c> and <c>dts</c> are in. Returns zero for a degenerate time base.
    /// </summary>
    /// <param name="timeBaseNum">Numerator of the stream time base.</param>
    /// <param name="timeBaseDen">Denominator of the stream time base.</param>
    public long InStreamUnits(int timeBaseNum, int timeBaseDen) =>
        Microseconds == 0 || timeBaseNum <= 0 || timeBaseDen <= 0
            ? 0
            : Microseconds * timeBaseDen / (timeBaseNum * MicrosecondsPerSecond);

    /// <summary>
    /// Shifts one raw container timestamp into media time.
    /// </summary>
    /// <remarks>
    /// A timestamp the container did not supply is returned untouched, so the
    /// <c>AV_NOPTS_VALUE</c> sentinel survives rather than becoming an ordinary-looking
    /// number a long way from the stream.
    /// </remarks>
    /// <param name="rawTimestamp">The timestamp as the container stated it.</param>
    /// <param name="hasTimestamp">Whether it is a real value rather than the sentinel.</param>
    /// <param name="offsetInStreamUnits">This origin from <see cref="InStreamUnits"/>.</param>
    public static long ToMediaTimestamp(
        long rawTimestamp,
        bool hasTimestamp,
        long offsetInStreamUnits
    ) => hasTimestamp ? rawTimestamp - offsetInStreamUnits : rawTimestamp;

    /// <summary>Converts an already-scaled container time to media time.</summary>
    public TimeSpan ToMediaTime(TimeSpan containerTime) =>
        containerTime - TimeSpan.FromMicroseconds(Microseconds);

    /// <summary>
    /// Converts a media-time position back to the container's domain, in microseconds,
    /// which is the unit a whole-container seek takes.
    /// </summary>
    public long ToContainerMicroseconds(TimeSpan mediaTime) =>
        checked((long)(mediaTime.TotalSeconds * MicrosecondsPerSecond) + Microseconds);
}
