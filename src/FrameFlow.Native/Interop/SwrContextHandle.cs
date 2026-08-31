// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns a native <c>SwrContext*</c> allocated by
/// <see cref="FFSwResample.swr_alloc"/> and freed by <see cref="FFSwResample.swr_free"/>.
/// </summary>
/// <remarks>
/// Lifecycle contract (ADR-0005):
/// <list type="bullet">
///   <item>Allocate with <see cref="FFSwResample.swr_alloc"/>.</item>
///   <item>Configure options via <c>FFAvUtil.av_opt_set_*</c>.</item>
///   <item>Initialise with <see cref="FFSwResample.swr_init"/>.</item>
///   <item>Wrap the pointer in this handle for deterministic release.</item>
///   <item>This handle is the sole owner; do not pass the raw pointer out of
///   <c>FrameFlow.Native</c> or <c>FrameFlow.Decoding</c>.</item>
/// </list>
/// </remarks>
internal sealed class SwrContextHandle : SafeHandle
{
    /// <summary>Initialises an invalid (null) handle.</summary>
    public SwrContextHandle()
        : base(nint.Zero, ownsHandle: true) { }

    /// <summary>Wraps an existing <c>SwrContext*</c> pointer.</summary>
    /// <param name="ptr">The native pointer returned by <c>swr_alloc</c>.</param>
    public SwrContextHandle(nint ptr)
        : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(ptr);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Releases the underlying <c>SwrContext</c> by calling <c>swr_free</c>.
    /// This method is invoked by the runtime during disposal or finalization.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        FFSwResample.swr_free(ref handle);
        return true;
    }
}
