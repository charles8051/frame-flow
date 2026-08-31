using System.Buffers;
using System.Runtime.CompilerServices;
using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests.Doubles;

/// <summary>
/// A controllable <see cref="IAudioDecoder"/> double for use in unit tests.
/// Yields a pre-loaded sequence of <see cref="PcmAudioBuffer"/> instances without
/// requiring real FFmpeg binaries or native interop.
/// </summary>
/// <remarks>
/// Each block in the pre-loaded sequence is backed by a small pooled buffer.
/// The caller owns each yielded block and must dispose it after use (ADR-0012).
/// </remarks>
internal sealed class FakeAudioDecoder : IAudioDecoder
{
    private readonly IReadOnlyList<PcmAudioBuffer> _blocks;
    private bool _disposed;

    public FakeAudioDecoder(IReadOnlyList<PcmAudioBuffer>? blocks = null)
    {
        _blocks = blocks ?? Array.Empty<PcmAudioBuffer>();
    }

    /// <summary>Number of times <see cref="DecodeAsync"/> has been called.</summary>
    public int DecodeCallCount { get; private set; }

    /// <summary>Number of times <see cref="DisposeAsync"/> has been called.</summary>
    public int DisposeCallCount { get; private set; }

    /// <summary>Whether this decoder has been disposed.</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc/>
    public async IAsyncEnumerable<PcmAudioBuffer> DecodeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DecodeCallCount++;

        foreach (var block in _blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return block;
            await Task.Yield();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Factory helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal <see cref="PcmAudioBuffer"/> backed by a small pooled buffer.
    /// The block contains <paramref name="sampleCount"/> interleaved stereo S16 samples.
    /// </summary>
    public static PcmAudioBuffer MakeBlock(
        int sampleCount = 1024,
        int sampleRate = 48_000,
        int channels = 2,
        TimeSpan pts = default
    )
    {
        int totalSamples = sampleCount * channels;
        var owner = MemoryPool<short>.Shared.Rent(totalSamples);
        return new PcmAudioBuffer(owner, totalSamples, sampleRate, channels, pts);
    }

    public void ResetPacketQueue() { }

    public void Flush() { }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public void CompletePacketQueue() { }
}
