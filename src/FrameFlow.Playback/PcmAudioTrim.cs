using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Cuts the audio a run starts with at the run's start, so that it begins where the picture does
/// (media-clips decision 8).
/// </summary>
internal static class PcmAudioTrim
{
    /// <summary>
    /// How many frames of audio that starts at <paramref name="start"/>, at
    /// <paramref name="sampleRate"/>, come before <paramref name="floor"/>. The frame at that index
    /// is the first that starts at or past it. Zero when the audio starts at or past the floor, or
    /// has no sample rate, and <see cref="long.MaxValue"/> when the count is larger than that.
    /// </summary>
    public static long FramesBefore(TimeSpan start, int sampleRate, TimeSpan floor)
    {
        if (floor <= start || sampleRate <= 0)
            return 0;

        // The difference in ticks fits an unsigned long whatever the two are. Whole seconds and
        // the rest are multiplied apart, so neither product overflows.
        const ulong perSecond = TimeSpan.TicksPerSecond;
        var ticks = unchecked((ulong)floor.Ticks - (ulong)start.Ticks);
        var seconds = ticks / perSecond;
        var rest = ticks % perSecond;
        var rate = (ulong)sampleRate;
        if (seconds > (ulong)long.MaxValue / rate)
            return long.MaxValue;
        var frames = seconds * rate + (rest * rate + perSecond - 1) / perSecond;
        return frames > long.MaxValue ? long.MaxValue : (long)frames;
    }

    /// <summary>
    /// A new buffer holding <paramref name="buffer"/>'s frames from <paramref name="frames"/> on,
    /// stamped with when the first of them starts, on <paramref name="buffer"/>'s timeline. The
    /// caller keeps its reference to
    /// <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">The buffer to cut. It must have a sample rate and channels.</param>
    /// <param name="frames">How many frames to leave out, from 1 to one fewer than it holds.</param>
    public static PcmAudioBuffer Cut(PcmAudioBuffer buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frames, buffer.FrameCount);

        var offset = frames * buffer.Channels;
        var count = buffer.SampleCount - offset;
        // Rounded up, so the cut buffer never claims to start before the frame it starts with.
        var skipped = TimeSpan.FromTicks(
            ((long)frames * TimeSpan.TicksPerSecond + buffer.SampleRate - 1) / buffer.SampleRate
        );
        return PcmAudioBuffer.Create(
            count,
            buffer.SampleRate,
            buffer.Channels,
            buffer.PresentationTime + skipped,
            buffer.Timeline,
            (Buffer: buffer, Offset: offset, Count: count),
            static (samples, cut) =>
            {
                cut.Buffer.Samples.Span.Slice(cut.Offset, cut.Count).CopyTo(samples);
                return cut.Count;
            }
        );
    }
}
