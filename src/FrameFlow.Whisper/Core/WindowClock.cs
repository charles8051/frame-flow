// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Whisper.Core;

/// <summary>
/// Where the samples a transcription window holds sit on the media timeline: the presentation
/// time of the first, and how many follow it. Pure.
/// </summary>
/// <param name="Start">Presentation time of the first held sample.</param>
/// <param name="Samples">Held samples, counted across channels as the PCM buffers count them.</param>
/// <param name="SamplesPerSecond">Sample rate times channel count.</param>
internal readonly record struct WindowClock(TimeSpan Start, long Samples, int SamplesPerSecond)
{
    /// <summary>The presentation time the next contiguous sample would carry.</summary>
    public TimeSpan End => Start + DurationOf(Samples, SamplesPerSecond);

    /// <summary>How long <paramref name="samples"/> play at <paramref name="samplesPerSecond"/>.</summary>
    public static TimeSpan DurationOf(long samples, int samplesPerSecond) =>
        TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / samplesPerSecond);
}

/// <summary>What admitting a buffer does to a window.</summary>
/// <param name="Clock">The window's clock once the buffer's samples are appended.</param>
/// <param name="Discard">
/// The held samples belong to a different stretch of the timeline and must be dropped before the
/// buffer's samples are appended.
/// </param>
internal readonly record struct WindowAdmission(WindowClock Clock, bool Discard);

/// <summary>
/// Keeps a transcription window on its stream's own timeline. A buffer that does not follow
/// the held samples (a seek, a loop, the next item) starts a new window at its own
/// presentation time rather than being joined to audio from somewhere else. Pure.
/// </summary>
internal static class WindowTimeline
{
    /// <summary>
    /// How far a buffer's presentation time may sit from where the held samples end and still
    /// count as the next audio. Resampling and timestamp rounding move it by milliseconds; a seek
    /// or a new item moves it by far more.
    /// </summary>
    public static readonly TimeSpan DiscontinuityTolerance = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Admits a buffer of <paramref name="sampleCount"/> samples stamped
    /// <paramref name="presentationTime"/> into the window described by <paramref name="clock"/>,
    /// or into an empty window when it is <see langword="null"/>.
    /// </summary>
    public static WindowAdmission Admit(
        WindowClock? clock,
        TimeSpan presentationTime,
        int sampleCount,
        int samplesPerSecond
    )
    {
        var anchored = new WindowClock(presentationTime, sampleCount, samplesPerSecond);

        // An empty window has nothing to keep in step, so it takes the buffer's own time.
        if (clock is not { Samples: > 0 } held)
            return new WindowAdmission(anchored, Discard: false);

        var offset = (presentationTime - held.End).Duration();
        if (offset > DiscontinuityTolerance)
            return new WindowAdmission(anchored, Discard: true);

        return new WindowAdmission(held with { Samples = held.Samples + sampleCount }, Discard: false);
    }

    /// <summary>
    /// Closes every full window of <paramref name="windowSamples"/> held by
    /// <paramref name="clock"/>. Returns the start time of each, in order, and the clock of the
    /// samples left over.
    /// </summary>
    public static (IReadOnlyList<TimeSpan> Starts, WindowClock Remainder) Close(
        WindowClock clock,
        int windowSamples
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSamples);

        var starts = new List<TimeSpan>();
        var duration = WindowClock.DurationOf(windowSamples, clock.SamplesPerSecond);
        while (clock.Samples >= windowSamples)
        {
            starts.Add(clock.Start);
            clock = clock with { Start = clock.Start + duration, Samples = clock.Samples - windowSamples };
        }

        return (starts, clock);
    }
}
