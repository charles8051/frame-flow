// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// A decoded PCM audio block whose sample data is backed by pooled memory.
/// Implements Crossbar's <see cref="Crossbar.IAudioBuffer"/> substrate
/// interface with reference counting so a single buffer can be safely
/// observed (audio sink playback) and consumed (resampler, ASR) without
/// duplicating the underlying memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refcounted ownership (amending ADR-0012).</b> A freshly-constructed
/// buffer starts at refcount 1. Each <see cref="AddRef"/> increments;
/// each <see cref="Dispose"/> decrements. When the count reaches zero
/// the wrapped <see cref="IMemoryOwner{T}"/> is disposed, returning the
/// pooled buffer to <see cref="System.Buffers.MemoryPool{T}.Shared"/>.
/// This is the audio counterpart to <see cref="IVideoFrame.AddRef"/>
/// and formally amends ADR-0012's single-owner stance for audio buffers
/// per Crossbar's <see cref="IAudioBuffer"/> contract.
/// </para>
/// <para>
/// <b>Why refcount.</b> Audio fan-out is a routine pattern — the same
/// buffer wants to reach speakers <i>and</i> a real-time analyzer
/// <i>and</i> a transcription pipeline. With a single-owner contract,
/// every fan-out point requires either a buffer clone (extra memcpy,
/// pool churn) or an asymmetric "caller-retains" surface
/// (<c>IAudioSink.WriteAsync</c>, the workaround we're retiring).
/// Refcounting collapses both into a single, generic operator:
/// <c>buffer.AddRef()</c> on the tap, <c>PresentAsync</c> on the sink.
/// </para>
/// <para>
/// <b>Legacy surface preserved.</b> The pre-migration property names
/// (<see cref="SampleCount"/>, <see cref="Channels"/>,
/// <see cref="PresentationTime"/>, <see cref="Samples"/>) are kept
/// alongside the <see cref="IAudioBuffer"/> surface
/// (<see cref="FrameCount"/>, <see cref="ChannelCount"/>,
/// <see cref="Timestamp"/>) to avoid a 30-file rename pass. New code
/// should prefer the substrate names; existing consumers keep
/// compiling unchanged.
/// </para>
/// <para>
/// <b>Format.</b> Samples are signed 16-bit, interleaved
/// (<see cref="AudioSampleFormat.Int16"/>) — that's what the decoder
/// produces and what the audio sinks consume. Non-S16 inputs are
/// converted at decode time. <see cref="MemoryDomain"/> is always
/// <see cref="FrameMemoryDomain.Cpu"/>.
/// </para>
/// </remarks>
public sealed class PcmAudioBuffer : IAudioBuffer
{
    // ── Backing state ─────────────────────────────────────────────
    private int _refCount = 1;

    /// <summary>
    /// The pooled memory owner for the raw PCM sample data. Disposed
    /// (returned to the pool) when the buffer's refcount reaches zero.
    /// </summary>
    public IMemoryOwner<short> SampleData { get; }

    /// <summary>
    /// Total scalar PCM samples in <see cref="SampleData"/>
    /// (interleaved layout — <see cref="FrameCount"/> &times;
    /// <see cref="ChannelCount"/>). May be less than
    /// <c>SampleData.Memory.Length</c> when the pool returns an
    /// oversized buffer.
    /// </summary>
    public int SampleCount { get; }

    /// <summary>Sample rate in Hz (e.g. 44100, 48000).</summary>
    public int SampleRate { get; }

    /// <summary>
    /// Number of audio channels (1 = mono, 2 = stereo, …). Legacy
    /// alias for <see cref="ChannelCount"/>; both return the same
    /// value.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Stream presentation timestamp for this block. Legacy alias for
    /// <see cref="Timestamp"/>; both return the same value.
    /// </summary>
    public TimeSpan PresentationTime { get; }

    // ── IAudioBuffer substrate ────────────────────────────────────

    /// <inheritdoc />
    public TimeSpan Timestamp => PresentationTime;

    /// <inheritdoc />
    /// <remarks>
    /// Derived from <see cref="FrameCount"/> / <see cref="SampleRate"/>.
    /// Producers with variable-rate post-resample boundaries can
    /// override by storing the canonical value at construction
    /// time — but the current decoder path produces fixed-rate
    /// buffers, so the derivation is exact.
    /// </remarks>
    public TimeSpan Duration =>
        SampleRate > 0 ? TimeSpan.FromSeconds((double)FrameCount / SampleRate) : TimeSpan.Zero;

    /// <inheritdoc />
    public int ChannelCount => Channels;

    /// <inheritdoc />
    public int FrameCount => Channels > 0 ? SampleCount / Channels : 0;

    /// <inheritdoc />
    public AudioSampleFormat SampleFormat => AudioSampleFormat.Int16;

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    /// <param name="sampleData">
    /// Pooled memory owner. Ownership transfers to this buffer on
    /// construction; the buffer is responsible for disposing it once
    /// the refcount reaches zero.
    /// </param>
    /// <param name="sampleCount">
    /// Total scalar samples (interleaved). For mono S16 this equals
    /// <see cref="FrameCount"/>; for stereo S16 it equals
    /// <see cref="FrameCount"/> &times; 2.
    /// </param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="presentationTime">Presentation timestamp.</param>
    public PcmAudioBuffer(
        IMemoryOwner<short> sampleData,
        int sampleCount,
        int sampleRate,
        int channels,
        TimeSpan presentationTime
    )
    {
        SampleData = sampleData;
        SampleCount = sampleCount;
        SampleRate = sampleRate;
        Channels = channels;
        PresentationTime = presentationTime;
    }

    /// <summary>
    /// Gets a read-only view of the valid PCM sample data sliced to
    /// <see cref="SampleCount"/> elements. The returned memory is
    /// valid until the buffer's refcount reaches zero.
    /// </summary>
    public ReadOnlyMemory<short> Samples => SampleData.Memory[..SampleCount];

    // ── Refcount surface ──────────────────────────────────────────

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">
    /// The buffer has already been fully released.
    /// </exception>
    public IAudioBuffer AddRef()
    {
        // CAS loop — increment if and only if the buffer is still live.
        // Spin is unbounded but in practice converges immediately because
        // AddRef contention is rare (typically a tap operator's Observe
        // callback racing with the downstream operator's pull).
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    nameof(PcmAudioBuffer),
                    "Cannot AddRef on a disposed buffer."
                );
            }
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return this;
            }
        }
    }

    /// <summary>
    /// Releases one reference. Returns the pooled sample buffer to its
    /// pool when the last reference releases. Calling
    /// <see cref="Dispose"/> more times than <see cref="AddRef"/> is
    /// a no-op (the count clamps at zero) so the pre-refcount
    /// "single Dispose" callers keep working.
    /// </summary>
    public void Dispose()
    {
        int newCount = Interlocked.Decrement(ref _refCount);
        if (newCount > 0)
            return;

        if (newCount < 0)
        {
            // Over-dispose — clamp back to zero. Defends against legacy
            // call sites that pre-date refcounting and might dispose
            // an already-released buffer.
            Interlocked.Increment(ref _refCount);
            return;
        }

        SampleData.Dispose();
    }
}
