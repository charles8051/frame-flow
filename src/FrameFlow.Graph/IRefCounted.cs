// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Items flowing through the substrate are refcounted. The substrate
/// holds a ref while invoking an operator and disposes that ref when
/// the operator returns (or throws). Operators receive an item, do
/// work, and either return a (potentially new) item or null to drop.
/// They never call <see cref="IDisposable.Dispose"/> on the input —
/// the substrate owns that lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// For fan-out (storage node with multiple consumers), the substrate
/// calls <see cref="AddRef"/> N-1 times so each downstream gets its
/// own reference. Refcount discipline is uniform: any code that holds
/// a reference must eventually dispose it.
/// </para>
/// <para>
/// Value types and non-refcountable items can ride the substrate by
/// wrapping in a refcount-aware container (see <see cref="RefBox{T}"/>).
/// The wrapper's AddRef/Dispose handle the refcount; the inner value
/// is freed when the count reaches zero.
/// </para>
/// </remarks>
public interface IRefCounted : IDisposable
{
    /// <summary>
    /// Increments the reference count and returns this item. The
    /// returned reference must be disposed independently.
    /// </summary>
    IRefCounted AddRef();
}
