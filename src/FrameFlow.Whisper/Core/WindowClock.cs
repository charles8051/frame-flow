// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Graph.Core;

namespace FrameFlow.Whisper.Core;

/// <summary>
/// Where the samples a transcription window holds sit on the media timeline, and which timeline
/// its input stream is on. Pure.
/// </summary>
/// <param name="Start">Presentation time of the first held sample.</param>
/// <param name="Samples">Held mono samples.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Follower">
/// Follows the timeline of the window's input stream (ADR-0084). The held samples belong to the
/// timeline it follows, and so do the captions transcribed from them.
/// </param>
internal readonly record struct WindowClock(
    TimeSpan Start,
    long Samples,
    int SampleRate,
    TimelineFollower Follower = default
)
{
    /// <summary>
    /// The timeline the held samples belong to, which the captions transcribed from them carry, or
    /// <see cref="TimelineId.None"/> while the stream has named none.
    /// </summary>
    public TimelineId Timeline => Follower.Current;

    /// <summary>The presentation time the next contiguous sample would carry.</summary>
    public TimeSpan End => Start + DurationOf(Samples, SampleRate);

    /// <summary>How long <paramref name="samples"/> play at <paramref name="sampleRate"/>.</summary>
    public static TimeSpan DurationOf(long samples, int sampleRate) =>
        TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / sampleRate);
}

/// <summary>What admitting a buffer does to a window.</summary>
/// <param name="Clock">The window's clock once the buffer's samples are appended.</param>
/// <param name="Discard">
/// The held samples belong to a different stretch of the timeline and must be dropped before the
/// buffer's samples are appended.
/// </param>
internal readonly record struct WindowAdmission(WindowClock Clock, bool Discard);

/// <summary>A full window taken from the front of the held samples.</summary>
/// <param name="Start">Presentation time of the window's first sample.</param>
/// <param name="Remainder">The clock of the samples still held after it.</param>
/// <param name="Timeline">The timeline of the window, which its captions carry.</param>
internal readonly record struct ClosedWindow(TimeSpan Start, WindowClock Remainder, TimelineId Timeline);

/// <summary>
/// Keeps a transcription window on its stream's own timeline. A buffer that does not follow
/// the held samples (a seek, a loop, the next item) starts a new window at its own
/// presentation time rather than being joined to audio from somewhere else. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>A stream that names its timeline (ADR-0084).</b> The window's input keeps the player's
/// order, so it follows it with a <see cref="TimelineFollower"/>. A buffer of a timeline other than
/// the one followed starts a new window, however close its time is to where the held audio ends. A
/// buffer of the timeline followed continues the window wherever its time sits, because within one
/// timeline presentation times are continuous.
/// </para>
/// <para>
/// <b>A stream that names none.</b> A buffer on <see cref="TimelineId.None"/>, or any buffer while
/// the follower has seen no timeline, is judged by its time, as before timelines: more than
/// <see cref="DiscontinuityTolerance"/> from where the held audio ends starts a new window. The
/// first timeline the stream names is adopted without starting one.
/// </para>
/// </remarks>
internal static class WindowTimeline
{
    /// <summary>
    /// How far a buffer's presentation time may sit from where the held samples end and still
    /// count as the next audio, on a stream that names no timeline. Container rounding and
    /// resampling move it by a millisecond or two; a seek moves it by more than this, which is
    /// shorter than a syllable.
    /// </summary>
    public static readonly TimeSpan DiscontinuityTolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Admits a buffer of <paramref name="samples"/> mono samples stamped
    /// <paramref name="presentationTime"/> on <paramref name="timeline"/> into the window described
    /// by <paramref name="clock"/>, or into an empty window when it is <see langword="null"/>.
    /// </summary>
    public static WindowAdmission Admit(
        WindowClock? clock,
        TimeSpan presentationTime,
        int samples,
        int sampleRate,
        TimelineId timeline = default
    )
    {
        var follower = clock?.Follower ?? default;
        var next = follower.Follow(timeline);
        var anchored = new WindowClock(presentationTime, samples, sampleRate, next);

        // An empty window has nothing to keep in step, so it takes the buffer's own time.
        if (clock is not { Samples: > 0 } held)
            return new WindowAdmission(anchored, Discard: false);

        var appended = held with { Samples = held.Samples + samples, Follower = next };

        // A seek, a loop or the next item: the held audio is from another timeline.
        if (follower.StartsNewTimeline(timeline))
            return new WindowAdmission(anchored, Discard: true);

        // The timeline followed: its times are continuous, so the time says nothing more.
        if (timeline != TimelineId.None && timeline == follower.Current)
            return new WindowAdmission(appended, Discard: false);

        // No timeline on one side: the time decides.
        var offset = (presentationTime - held.End).Duration();
        if (offset > DiscontinuityTolerance)
            return new WindowAdmission(anchored, Discard: true);

        return new WindowAdmission(appended, Discard: false);
    }

    /// <summary>
    /// Takes one window of <paramref name="windowSamples"/> from the front of what
    /// <paramref name="clock"/> holds, or returns <see langword="null"/> when it holds less.
    /// </summary>
    public static ClosedWindow? CloseOne(WindowClock clock, int windowSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSamples);

        if (clock.Samples < windowSamples)
            return null;

        var remainder = clock with
        {
            Start = clock.Start + WindowClock.DurationOf(windowSamples, clock.SampleRate),
            Samples = clock.Samples - windowSamples,
        };
        return new ClosedWindow(clock.Start, remainder, clock.Timeline);
    }
}

/// <summary>Turns 16-bit PCM into the mono float samples Whisper reads. Pure.</summary>
internal static class WhisperSamples
{
    private const float Int16Scale = 1f / 32768f;

    /// <summary>
    /// Appends <paramref name="interleaved"/> to <paramref name="target"/> as mono floats in
    /// <c>[-1, 1]</c>, averaging the <paramref name="channels"/> of each frame.
    /// </summary>
    public static void AppendMono(List<float> target, ReadOnlySpan<short> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        var scale = Int16Scale / channels;
        for (int frame = 0; frame + channels <= interleaved.Length; frame += channels)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++)
                sum += interleaved[frame + c];
            target.Add(sum * scale);
        }
    }
}
