// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Whisper;

/// <summary>
/// Wraps a <see cref="Caption"/> (a value record) as the substrate's
/// <see cref="IRefCounted"/>. <see cref="Caption"/> has no real resources
/// to dispose — it implements <see cref="IDisposable"/> only to satisfy
/// the old substrate's <c>TFrame : IDisposable</c> constraint — so the
/// wrapper's lifecycle is trivial: refcount the wrapper itself, ignore
/// the inner caption's no-op <c>Dispose</c>.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper counts its own references and <c>AddRef</c> returns the same
/// instance (ADR-0080, decision 1). The caption value is small and immutable,
/// so every holder shares it.
/// </para>
/// <para>
/// Pattern parallels <c>RefBox&lt;Caption&gt;</c> but exists as its
/// own named type for cleaner generic signatures in operator factories
/// and tests.
/// </para>
/// </remarks>
public sealed class CaptionRef : IRefCounted
{
    private int _refCount;

    /// <summary>The wrapped caption.</summary>
    public Caption Value { get; }

    public CaptionRef(Caption value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        _refCount = 1;
    }

    public IRefCounted AddRef()
    {
        RefCounting.AddRef(ref _refCount, this);
        return this;
    }

    public void Dispose()
    {
        // Caption is a value record with no resources, so the final release frees
        // nothing. The count still matters: it makes AddRef after the final release
        // fail, and an over-release visible.
        RefCounting.Release(ref _refCount, this);
    }
}
