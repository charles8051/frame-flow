// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// A decoded PCM audio block whose sample data is backed by pooled memory.
/// Implements the graph substrate's <see cref="FrameFlow.Graph.IAudioBuffer"/>
/// interface with reference counting so a single buffer can be safely
/// observed (audio sink playback) and consumed (resampler, ASR) without
/// duplicating the underlying memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refcounted ownership (amending ADR-0012).</b> A new buffer starts
/// at refcount 1. Each <see cref="AddRef"/> increments; each
/// <see cref="Dispose"/> decrements. When the count reaches zero the
/// sample storage returns to its pool.
/// This is the audio counterpart to <see cref="IVideoFrame.AddRef"/>
/// and formally amends ADR-0012's single-owner stance for audio buffers
/// per the graph substrate's <see cref="IAudioBuffer"/> contract.
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
/// <b>Immutable once created (ADR-0080, decision 5).</b> A buffer is made
/// by <see cref="Create{TState}"/>, which rents storage, runs a fill
/// callback over it, and returns the buffer. <see cref="Samples"/> is
/// read-only, and no holder can reach the storage to free it. A debug
/// build fills released storage with a fixed value, so a read through a
/// <see cref="Samples"/> view kept past the final release fails every time.
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
    /// <summary>The value a debug build writes over storage when the buffer is released.</summary>
    internal const short ReleasedFill = unchecked((short)0xDDDD);

    // ── Backing state ─────────────────────────────────────────────
    private int _refCount = 1;
    private readonly short[] _storage;
    private readonly ArrayPool<short> _pool;

    /// <summary>
    /// Total scalar PCM samples in <see cref="Samples"/> (interleaved
    /// layout — <see cref="FrameCount"/> × <see cref="ChannelCount"/>).
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

    private PcmAudioBuffer(
        short[] storage,
        ArrayPool<short> pool,
        int sampleCount,
        int sampleRate,
        int channels,
        TimeSpan presentationTime
    )
    {
        _storage = storage;
        _pool = pool;
        SampleCount = sampleCount;
        SampleRate = sampleRate;
        Channels = channels;
        PresentationTime = presentationTime;
    }

    /// <summary>
    /// Creates a buffer: rents room for <paramref name="capacity"/> scalar
    /// samples, runs <paramref name="fill"/> over it, and returns a buffer
    /// holding the samples the callback reports writing.
    /// </summary>
    /// <typeparam name="TState">State passed through to <paramref name="fill"/>.</typeparam>
    /// <param name="capacity">
    /// The most scalar samples (interleaved) the callback may write. A
    /// producer that learns its count only while filling, such as a
    /// resampler, passes an upper bound.
    /// </param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="presentationTime">Presentation timestamp.</param>
    /// <param name="state">Passed to <paramref name="fill"/>, so it can be a static lambda that allocates nothing.</param>
    /// <param name="fill">
    /// Writes interleaved samples from the start of the span and returns how
    /// many it wrote, between 0 and <paramref name="capacity"/>. It runs once,
    /// before the buffer exists.
    /// </param>
    /// <param name="pool">Where the storage comes from. Defaults to <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <returns>The buffer, holding one reference.</returns>
    /// <remarks>
    /// If <paramref name="fill"/> throws, the storage goes back to
    /// <paramref name="pool"/> and the exception propagates unchanged, even
    /// when the pool's <c>Return</c> throws too. No buffer is created.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="fill"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="fill"/> reported a count below 0 or above <paramref name="capacity"/>.
    /// </exception>
    public static PcmAudioBuffer Create<TState>(
        int capacity,
        int sampleRate,
        int channels,
        TimeSpan presentationTime,
        TState state,
        PcmAudioFill<TState> fill,
        ArrayPool<short>? pool = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentNullException.ThrowIfNull(fill);

        pool ??= ArrayPool<short>.Shared;
        short[] storage = pool.Rent(capacity);

        int written;
        try
        {
            written = fill(storage.AsSpan(0, capacity), state);
        }
        catch
        {
            ReturnStorageAfterFailure(pool, storage);
            throw;
        }

        if ((uint)written > (uint)capacity)
        {
            ReturnStorageAfterFailure(pool, storage);
            throw new InvalidOperationException(
                $"The fill callback reported {written} samples for a capacity of {capacity}."
            );
        }

        return new PcmAudioBuffer(storage, pool, written, sampleRate, channels, presentationTime);
    }

    /// <summary>
    /// Gets a read-only view of the <see cref="SampleCount"/> valid
    /// samples. The view is valid until the buffer's refcount reaches zero.
    /// </summary>
    public ReadOnlyMemory<short> Samples => _storage.AsMemory(0, SampleCount);

    // ── Refcount surface ──────────────────────────────────────────

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">
    /// The buffer has already been fully released.
    /// </exception>
    public IAudioBuffer AddRef()
    {
        RefCounting.AddRef(ref _refCount, this);
        return this;
    }

    /// <summary>
    /// Releases one reference. Returns the pooled sample buffer to its
    /// pool when the last reference releases. A release past zero frees
    /// nothing and is counted as an over-release (<see cref="RefCounting"/>).
    /// </summary>
    public void Dispose()
    {
        if (RefCounting.Release(ref _refCount, this))
            ReturnStorage(_pool, _storage);
    }

    private static void ReturnStorage(ArrayPool<short> pool, short[] storage)
    {
#if DEBUG
        storage.AsSpan().Fill(ReleasedFill);
#endif
        pool.Return(storage);
    }

    /// <summary>
    /// Returns the storage on a failure path. The failure's own exception is the one the caller
    /// sees; a pool that also throws from <c>Return</c> leaves the array to the garbage collector.
    /// </summary>
    private static void ReturnStorageAfterFailure(ArrayPool<short> pool, short[] storage)
    {
        try
        {
            ReturnStorage(pool, storage);
        }
        catch (Exception)
        {
            // Deliberately dropped: rethrowing here would replace the exception being reported.
        }
    }
}
