// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// Adapts a <see cref="Periphery.Camera.ICameraFrame"/> to flow through
/// the <see cref="FrameFlow.Graph"/> substrate. Periphery's camera
/// frames are substrate-independent (ADR-0045) — they declare their
/// own refcount discipline without inheriting from
/// <see cref="Graph.IRefCounted"/>. This adapter is the
/// boundary that maps the camera-frame protocol onto the substrate-
/// item protocol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> The adapter holds exactly one ref on the inner
/// <see cref="Periphery.Camera.ICameraFrame"/>. <see cref="AddRef"/>
/// calls <see cref="ICameraFrame.AddRef"/> on the inner frame and
/// returns the same adapter wrapper (substrate refcount discipline
/// is satisfied by the inner frame's CAS-loop). <see cref="Dispose"/>
/// calls <see cref="System.IDisposable.Dispose"/> on the inner frame,
/// returning the buffer to the pool when the final ref releases.
/// </para>
/// <para>
/// <b>Why a separate type and not just casting</b>:
/// <see cref="Periphery.Camera.ICameraFrame"/>'s <c>AddRef</c> returns
/// <see cref="Periphery.Camera.ICameraFrame"/>, not
/// <see cref="Graph.IRefCounted"/>. The two contracts have
/// compatible *behavior* but incompatible *types* — a casting bridge
/// requires explicit-interface plumbing in Periphery, which would
/// reintroduce the substrate dependency ADR-0045 deliberately removed.
/// The adapter pattern keeps Periphery substrate-clean at the small
/// cost of one allocation per frame entering the graph (poolable if
/// it ever matters).
/// </para>
/// <para>
/// <b>Inner access.</b> Operators that need camera-specific surface
/// (<see cref="ICameraFrame.PixelFormat"/>, plane access, etc.) read
/// <see cref="Inner"/> directly. The substrate's
/// <see cref="Graph.IFrame"/> surface
/// (<see cref="IFrame.Width"/> / <c>Height</c> /
/// <c>Timestamp</c>) is forwarded for substrate-typed operator bodies
/// that don't reach for the camera vocabulary.
/// </para>
/// </remarks>
public sealed class CameraFrameAdapter : IFrame, IRefCounted
{
    private readonly ICameraFrame _inner;

    /// <summary>Constructs an adapter that takes ownership of one ref on <paramref name="inner"/>.</summary>
    public CameraFrameAdapter(ICameraFrame inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The wrapped Periphery camera frame. Use this for camera-specific access.</summary>
    public ICameraFrame Inner => _inner;

    /// <inheritdoc />
    public int Width => _inner.Width;

    /// <inheritdoc />
    public int Height => _inner.Height;

    /// <inheritdoc />
    public TimeSpan Timestamp => _inner.Timestamp;

    /// <inheritdoc />
    public IRefCounted AddRef()
    {
        _inner.AddRef();
        return this;
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
