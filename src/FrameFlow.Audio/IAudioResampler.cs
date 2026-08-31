// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Audio;

/// <summary>
/// Resamples <see cref="PcmAudioBuffer"/> instances to a fixed target
/// sample rate and channel count, using <c>libswresample</c> under the
/// hood (the same resampler the FFmpeg audio decoder runs internally).
/// </summary>
/// <remarks>
/// <para>
/// Stateful — one instance handles one input → output configuration over
/// the lifetime of a stream. <see cref="Reset"/> drops buffered samples
/// (call after a seek). <see cref="Flush"/> drains the resampler's
/// internal latency at EOS.
/// </para>
/// <para>
/// <b>Input format negotiation:</b> swr is configured lazily on the
/// first <see cref="Process"/> call, taking sample rate and channel
/// count from the input <see cref="PcmAudioBuffer"/>. The input format
/// must remain stable thereafter — passing a buffer with different
/// rate/channels mid-stream is undefined behaviour and will throw.
/// </para>
/// <para>
/// <b>Output cardinality:</b> each call to <see cref="Process"/> may
/// return zero, one, or more samples depending on the resampler's
/// internal buffering. A small input chunk at a high downsample ratio
/// may produce an output buffer with <c>SampleCount = 0</c> — callers
/// should always check before consuming the samples.
/// </para>
/// <para>
/// <b>Threading:</b> not thread-safe. Call <see cref="Process"/>,
/// <see cref="Flush"/>, and <see cref="Reset"/> from a single thread.
/// </para>
/// </remarks>
public interface IAudioResampler : IDisposable
{
    /// <summary>Target output sample rate in Hz (configured at construction).</summary>
    int TargetSampleRate { get; }

    /// <summary>Target output channel count (1 = mono, 2 = stereo, ...).</summary>
    int TargetChannels { get; }

    /// <summary>
    /// Resamples <paramref name="input"/> into a new
    /// <see cref="PcmAudioBuffer"/> at the configured target format. The
    /// caller owns the returned buffer and must dispose it. The returned
    /// buffer's <c>SampleCount</c> may be zero — caller should check
    /// before consuming.
    /// </summary>
    /// <param name="input">
    /// Source buffer. Not consumed — the caller retains ownership and is
    /// responsible for disposing it. The resampler reads the samples
    /// during this call only.
    /// </param>
    /// <returns>
    /// A freshly-allocated output buffer at the target format, with
    /// <c>PresentationTime</c> copied from <paramref name="input"/>.
    /// Never <see langword="null"/>; check <c>SampleCount</c> for zero.
    /// </returns>
    PcmAudioBuffer Process(PcmAudioBuffer input);

    /// <summary>
    /// Drains any samples buffered inside the resampler at end-of-stream.
    /// Returns <see langword="null"/> when there's nothing to flush.
    /// </summary>
    /// <param name="finalPresentationTime">
    /// The presentation timestamp to attach to the flushed buffer.
    /// Typically <c>lastInput.PresentationTime + lastInput.Duration</c>
    /// or the stream's end PTS.
    /// </param>
    PcmAudioBuffer? Flush(TimeSpan finalPresentationTime);

    /// <summary>
    /// Discards the resampler's internal buffered samples without
    /// emitting them. Call after a seek so the next <see cref="Process"/>
    /// output reflects only post-seek input.
    /// </summary>
    void Reset();
}

/// <summary>
/// Factory for <see cref="IAudioResampler"/>. Returns a swr-backed
/// implementation; future variants (managed-only fallback, mock for
/// tests) could be added without changing consumers.
/// </summary>
public static class AudioResampler
{
    /// <summary>
    /// Creates a resampler configured for the given output format.
    /// </summary>
    /// <param name="targetSampleRate">Output sample rate in Hz (e.g. 16000, 44100, 48000).</param>
    /// <param name="targetChannels">Output channel count (1 = mono, 2 = stereo).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if either argument is non-positive.
    /// </exception>
    public static IAudioResampler Create(int targetSampleRate, int targetChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChannels);
        return new FfmpegAudioResampler(targetSampleRate, targetChannels);
    }
}
