// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Whisper.Core;

/// <summary>
/// Where the samples a transcription window holds sit on the media timeline: the presentation
/// time of the first, and how many follow it. Pure.
/// </summary>
/// <param name="Start">Presentation time of the first held sample.</param>
/// <param name="Samples">Held mono samples.</param>
/// <param name="SampleRate">Samples per second.</param>
internal readonly record struct WindowClock(TimeSpan Start, long Samples, int SampleRate)
{
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
internal readonly record struct ClosedWindow(TimeSpan Start, WindowClock Remainder);

/// <summary>
/// Keeps a transcription window on its stream's own timeline. A buffer that does not follow
/// the held samples (a seek, a loop, the next item) starts a new window at its own
/// presentation time rather than being joined to audio from somewhere else. Pure.
/// </summary>
internal static class WindowTimeline
{
    /// <summary>
    /// How far a buffer's presentation time may sit from where the held samples end and still
    /// count as the next audio. Container rounding and resampling move it by a millisecond or two;
    /// a seek moves it by more than this, which is shorter than a syllable.
    /// </summary>
    public static readonly TimeSpan DiscontinuityTolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Admits a buffer of <paramref name="samples"/> mono samples stamped
    /// <paramref name="presentationTime"/> into the window described by <paramref name="clock"/>,
    /// or into an empty window when it is <see langword="null"/>.
    /// </summary>
    public static WindowAdmission Admit(
        WindowClock? clock,
        TimeSpan presentationTime,
        int samples,
        int sampleRate
    )
    {
        var anchored = new WindowClock(presentationTime, samples, sampleRate);

        // An empty window has nothing to keep in step, so it takes the buffer's own time.
        if (clock is not { Samples: > 0 } held)
            return new WindowAdmission(anchored, Discard: false);

        var offset = (presentationTime - held.End).Duration();
        if (offset > DiscontinuityTolerance)
            return new WindowAdmission(anchored, Discard: true);

        return new WindowAdmission(held with { Samples = held.Samples + samples }, Discard: false);
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
        return new ClosedWindow(clock.Start, remainder);
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
