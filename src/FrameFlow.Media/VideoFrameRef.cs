// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// Adapter that exposes an <see cref="IVideoFrame"/> as the new
/// substrate's <see cref="IRefCounted"/>. Each <c>VideoFrameRef</c>
/// wrapper owns exactly one reference on the underlying frame; the
/// wrapper's <c>Dispose</c> releases that ref (which calls through
/// to <see cref="IDisposable.Dispose"/> on the frame, decrementing
/// the underlying refcount per FrameFlow's frame-ownership contract).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The substrate (Crossbar)
/// uniformly requires <c>T : class, IRefCounted</c> on flowing items
/// because the substrate handles AddRef/Dispose automatically at
/// every operator boundary. FrameFlow's <see cref="IVideoFrame"/>
/// has the right *shape* (it has <c>AddRef</c> and <c>Dispose</c>)
/// but doesn't implement <c>IRefCounted</c> — its <c>AddRef</c>
/// returns <c>IVideoFrame</c>, not <c>IRefCounted</c>.
/// </para>
/// <para>
/// Two options to bridge: change <c>IVideoFrame</c> to extend
/// <c>IRefCounted</c> (impacts <c>FrameFlow.Media</c> and every
/// implementer), or wrap at the substrate boundary. We use the
/// wrapper for the Phase 2 port — zero changes to existing
/// FrameFlow code, no compile breakage in modules that haven't
/// migrated yet.
/// </para>
/// <para>
/// <b>Wrapper lifecycle.</b> Each wrapper instance represents one
/// ref on the underlying frame:
/// </para>
/// <list type="bullet">
/// <item><b>Construction</b> takes an <see cref="IVideoFrame"/> that
/// the caller already owns one ref on. The wrapper doesn't AddRef —
/// it adopts that ref.</item>
/// <item><b><see cref="AddRef"/></b> calls <c>frame.AddRef()</c> (so
/// the underlying refcount goes up) and returns a NEW wrapper that
/// owns the new ref. The substrate can then dispose both wrappers
/// independently.</item>
/// <item><b><see cref="Dispose"/></b> calls <c>frame.Dispose()</c>,
/// releasing the wrapper's ref. After all wrappers are disposed,
/// the underlying refcount reaches zero and the frame returns to
/// its pool.</item>
/// </list>
/// </remarks>
public sealed class VideoFrameRef : IRefCounted
{
    private IVideoFrame? _frame;

    /// <summary>
    /// The wrapped <see cref="IVideoFrame"/>. Operators reach through
    /// to this property to access pixel data, dimensions, PTS, etc.
    /// The wrapper guarantees the frame is alive as long as the
    /// wrapper itself hasn't been disposed (or its inner frame
    /// detached via <see cref="Detach"/>).
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The wrapper has been disposed or its inner frame detached.
    /// </exception>
    public IVideoFrame Frame =>
        _frame ?? throw new ObjectDisposedException(nameof(VideoFrameRef));

    /// <summary>
    /// Adopts an existing ref on <paramref name="frame"/>. The caller
    /// must already hold one ref (typically just-allocated from a
    /// pool or just-returned from another converter); this wrapper
    /// takes ownership.
    /// </summary>
    public VideoFrameRef(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
    }

    public IRefCounted AddRef()
    {
        var f = _frame ?? throw new ObjectDisposedException(nameof(VideoFrameRef));
        // May throw for one-shot frames (decoder-produced
        // Media.CpuVideoFrame, converter outputs); that's the same
        // contract IVideoFrame.AddRef already exposes — callers that
        // need fan-out over one-shot frames must use
        // VideoFrameExtensions.CloneCpu instead.
        f.AddRef();
        return new VideoFrameRef(f);
    }

    /// <summary>
    /// Transfers ownership of the inner frame out of this wrapper.
    /// After Detach, <see cref="Dispose"/> is a no-op (the frame's
    /// ref is now owned by the caller, who is responsible for
    /// disposing it). Returns <see langword="null"/> if the wrapper
    /// was already disposed or detached.
    /// </summary>
    /// <remarks>
    /// Used by sink adapters that pass the frame to
    /// <see cref="IVideoSink.PresentAsync"/> per the ADR-0044 sink
    /// contract ("the sink takes ownership of the frame and is
    /// responsible for disposing it"). Detach avoids both
    /// the <see cref="AddRef"/> call (which one-shot decoder frames
    /// reject) and the double-dispose that would otherwise happen
    /// when the substrate releases the wrapper after the sink body
    /// returns.
    /// </remarks>
    public IVideoFrame? Detach() => Interlocked.Exchange(ref _frame, null);

    public void Dispose()
    {
        var f = Interlocked.Exchange(ref _frame, null);
        f?.Dispose();
    }
}
