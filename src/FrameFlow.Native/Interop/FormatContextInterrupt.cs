// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Interop;

/// <summary>
/// What an <c>AVFormatContext</c>'s interrupt callback answers: whether the call FFmpeg is making
/// on the context should give up (ADR-0086, decision 4; #709).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it observes.</b> The cancellation of the one operation using the context now, and
/// whether the context is closing. Each operation installs its own token for as long as it runs
/// (<see cref="Enter"/>, <see cref="Exit"/>): the open and its stream probe observe the open's
/// token, a demux pump the run's, a seek and its keyframe search the seek's, a packet read the
/// read's. Between operations no token is installed, and nothing is interrupted. So a seek made
/// after a run was cancelled observes the seek's own token, not the run's. <see cref="Close"/>
/// makes every later answer "give up", for the close and anything still blocked in FFmpeg.
/// </para>
/// <para>
/// <b>Where it is asked.</b> FFmpeg calls the callback on the thread making the blocking call,
/// inside it: before each read or write of the underlying protocol, and every 100 ms while a
/// network protocol waits for data, a connection or a reply. A call it interrupts returns
/// <c>AVERROR_EXIT</c> (<see cref="FFAvUtil.AvErrorExit"/>). A local file is asked before each
/// read of its buffer, which seldom blocks; a stalled network source is where it matters.
/// </para>
/// <para>
/// <b>Lifetime.</b> FFmpeg copies the callback and its argument into every protocol context the
/// open creates, so they cannot change over the context's life, and the state they point at has
/// to outlive the context. The argument is a <see cref="GCHandle"/> to this object, freed by
/// <see cref="Dispose"/>, which the owner calls only once the context has been closed, or once a
/// failed open has freed it. <see cref="FormatContextHandle"/> does that after
/// <c>avformat_close_input</c>.
/// </para>
/// <para>
/// <b>The callback.</b> It reads a token and a flag through the handle: no allocation, no lock,
/// and no managed state that can be freed under it. The token is written by the thread that runs
/// the operation, before the native call, and read inside the call, on that thread unless a
/// protocol reads ahead on a thread of its own; it is one reference, so a read is never torn. The
/// flag is written by the closing thread and read with a barrier.
/// </para>
/// </remarks>
internal sealed unsafe class FormatContextInterrupt : IDisposable
{
    private GCHandle _self;
    private CancellationToken _operation;
    private int _closed;

    /// <summary>Allocates the handle FFmpeg is given as the callback's argument.</summary>
    internal FormatContextInterrupt()
    {
        _self = GCHandle.Alloc(this);
    }

    /// <summary>
    /// The callback's argument: the handle to this object, as FFmpeg holds it.
    /// <see cref="nint.Zero"/> once <see cref="Dispose"/> has freed it.
    /// </summary>
    internal nint Opaque => _self.IsAllocated ? GCHandle.ToIntPtr(_self) : nint.Zero;

    /// <summary>The callback FFmpeg calls, as the function pointer it is given.</summary>
    internal static delegate* unmanaged[Cdecl]<void*, int> Callback => &ShouldInterrupt;

    /// <summary>
    /// Whether the call FFmpeg is making on the context should give up: the context is closing,
    /// or the operation using it has been cancelled.
    /// </summary>
    internal bool IsInterruptRequested =>
        Volatile.Read(ref _closed) != 0 || _operation.IsCancellationRequested;

    /// <summary>
    /// Sets the callback on a context that has been allocated and not yet opened, so the open and
    /// every protocol context it creates carry it.
    /// </summary>
    /// <param name="formatContext">The <c>AVFormatContext*</c>, from <c>avformat_alloc_context</c>.</param>
    internal void Install(nint formatContext)
    {
        ObjectDisposedException.ThrowIf(!_self.IsAllocated, this);
        ref AVFormatContext ctx = ref Unsafe.AsRef<AVFormatContext>((void*)formatContext);
        ctx.interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = (nint)Callback };
        ctx.interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_self);
    }

    /// <summary>
    /// Installs the token of an operation about to use the context, on the thread that will make
    /// its native calls, and returns the token it replaces for <see cref="Exit"/> to restore.
    /// </summary>
    internal CancellationToken Enter(CancellationToken operation)
    {
        var replaced = _operation;
        _operation = operation;
        return replaced;
    }

    /// <summary>Ends an operation's use: restores the token <see cref="Enter"/> replaced.</summary>
    internal void Exit(CancellationToken replaced) => _operation = replaced;

    /// <summary>
    /// The context is closing: every call made on it from now on, and any still blocked in it,
    /// gives up. Called before <c>avformat_close_input</c>, so the close itself does not wait on a
    /// stalled source.
    /// </summary>
    internal void Close() => Volatile.Write(ref _closed, 1);

    /// <summary>
    /// Frees the handle FFmpeg holds. Call only once the context has been closed or a failed open
    /// has freed it: a callback made after this would read a freed handle.
    /// </summary>
    public void Dispose()
    {
        if (_self.IsAllocated)
            _self.Free();
    }

    // FFmpeg's int (*callback)(void *opaque): nonzero to give up. Nothing may throw across the
    // native boundary, so a handle that does not resolve answers "give up", the safe answer.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShouldInterrupt(void* opaque)
    {
        if (opaque == null)
            return 0;
        try
        {
            return GCHandle.FromIntPtr((nint)opaque).Target is FormatContextInterrupt state
                && state.IsInterruptRequested
                ? 1
                : 0;
        }
        catch
        {
            return 1;
        }
    }
}
