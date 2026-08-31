// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// Adapter exposing <see cref="PcmAudioBuffer"/> as the substrate's
/// <see cref="IRefCounted"/>. Same shape as <c>FrameFlow.Video.VideoFrameRef</c>:
/// each wrapper owns exactly one ref on the underlying buffer; <c>AddRef</c>
/// bumps the underlying refcount and returns a new wrapper.
/// </summary>
public sealed class PcmAudioBufferRef : IRefCounted
{
    private PcmAudioBuffer? _buffer;

    /// <summary>
    /// The wrapped buffer. Operators reach through to this for sample
    /// data, PTS, etc. The wrapper guarantees the buffer is alive as
    /// long as the wrapper itself hasn't been disposed (or its inner
    /// buffer detached via <see cref="Detach"/>).
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The wrapper has been disposed or its inner buffer detached.
    /// </exception>
    public PcmAudioBuffer Buffer =>
        _buffer ?? throw new ObjectDisposedException(nameof(PcmAudioBufferRef));

    /// <summary>Adopts an existing ref on <paramref name="buffer"/>.</summary>
    public PcmAudioBufferRef(PcmAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    public IRefCounted AddRef()
    {
        var b = _buffer ?? throw new ObjectDisposedException(nameof(PcmAudioBufferRef));
        // PcmAudioBuffer.AddRef returns the IAudioBuffer substrate
        // interface; cast back to the concrete type for our wrapper.
        var bumped = (PcmAudioBuffer)b.AddRef();
        return new PcmAudioBufferRef(bumped);
    }

    /// <summary>
    /// Transfers ownership of the inner buffer out of this wrapper.
    /// After Detach, <see cref="Dispose"/> is a no-op. Symmetric with
    /// <c>VideoFrameRef.Detach</c>; used by sink adapters that pass
    /// ownership to <see cref="IAudioSink.PresentAsync"/> per ADR-0044.
    /// </summary>
    public PcmAudioBuffer? Detach() => Interlocked.Exchange(ref _buffer, null);

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buffer, null);
        b?.Dispose();
    }
}
