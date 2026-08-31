// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// The substrate-level decoded audio buffer — a chunk of consecutive
/// audio frames (samples per channel) flowing through a Crossbar
/// pipeline. Sibling of <see cref="IFrame"/>: both compose through the
/// same pipeline runtime, but audio buffers carry no Width/Height and
/// would distort the <see cref="IFrame"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Naming.</b> "Buffer", not "Frame" — in audio terminology a
/// <em>frame</em> is one sample per channel (a stereo frame is two
/// samples), which is exactly what <see cref="FrameCount"/> counts.
/// The container holding many such audio frames is universally called
/// a buffer (Web Audio <c>AudioBuffer</c>, CoreAudio
/// <c>AudioBufferList</c>, FFmpeg's <c>AVFrame</c> in its
/// audio-carrying mode). The interface name follows that convention so
/// audio engineers reading the code don't have to translate.
/// </para>
/// <para>
/// <b>Generic substrate, specific shape.</b> The substrate's graph
/// primitives (<see cref="GraphChain{T}"/>, <see cref="Consumer{TIn}"/>,
/// <see cref="SinkNode{TIn}"/>) are constrained to
/// <see cref="IRefCounted"/> at the wrapper level and
/// <see cref="IDisposable"/> at the payload level, so an audio chain
/// is a <c>GraphChain&lt;PcmAudioBufferRef&gt;</c> with no further
/// substrate changes. Consumers compose
/// <c>graph.Pipeline(audioSource).Then(resample).To(audioSink.AsSinkNode())</c>
/// exactly as they would for video.
/// </para>
/// <para>
/// <b>Ownership.</b> Refcounted, matching <see cref="ITensor"/>'s
/// pattern: each <see cref="AddRef"/> requires a balancing
/// <see cref="IDisposable.Dispose"/>; the underlying buffer releases
/// only when all references have disposed. This makes audio fan-out
/// (e.g. simultaneous playback + real-time analyzer) a first-class
/// operation at the substrate. FrameFlow's <c>PcmAudioBlock</c>
/// migration formally amends FrameFlow ADR-0012's single-owner stance
/// for audio buffers.
/// </para>
/// <para>
/// <b>What lives here vs. on a refinement.</b> This interface carries
/// only the metadata every audio consumer needs (timing, rate,
/// channels, format, domain). Raw byte / typed-sample access lives on
/// a CPU-specific refinement (future <c>ICpuAudioBuffer</c>); device-
/// pointer access lives on a GPU-specific refinement (future
/// <c>ICudaAudioBuffer</c> if a GPU audio producer ever materializes —
/// rare, but the seam is the same shape as <c>ICudaTensor</c>).
/// </para>
/// </remarks>
public interface IAudioBuffer : IDisposable
{
    /// <summary>Presentation timestamp of the first audio frame in this buffer.</summary>
    TimeSpan Timestamp { get; }

    /// <summary>
    /// Wall-clock duration of this buffer. Typically equals
    /// <c>FrameCount / SampleRate</c> seconds, but stored rather than
    /// derived so a producer with variable-rate audio (or post-resample
    /// boundary alignment) can carry the canonical value.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>Sample rate in Hz (e.g. 44100, 48000).</summary>
    int SampleRate { get; }

    /// <summary>Number of audio channels (1 = mono, 2 = stereo, 6 = 5.1, etc.).</summary>
    int ChannelCount { get; }

    /// <summary>
    /// Number of audio frames in this buffer — i.e. samples per channel,
    /// matching FFmpeg's <c>AVFrame.nb_samples</c> convention. Total
    /// scalar samples is <c>FrameCount × ChannelCount</c>.
    /// </summary>
    int FrameCount { get; }

    /// <summary>Per-sample numeric type and channel-interleaving layout.</summary>
    AudioSampleFormat SampleFormat { get; }

    /// <summary>Memory domain where the audio buffer resides.</summary>
    FrameMemoryDomain MemoryDomain { get; }

    /// <summary>
    /// Atomically adds one reference and returns the same instance for
    /// fluent usage. Each <see cref="AddRef"/> requires a balancing
    /// <see cref="IDisposable.Dispose"/>; the underlying buffer
    /// releases only when all references have disposed.
    /// </summary>
    /// <returns>This buffer instance.</returns>
    /// <exception cref="ObjectDisposedException">
    /// The buffer's reference count is already zero.
    /// </exception>
    IAudioBuffer AddRef();
}
