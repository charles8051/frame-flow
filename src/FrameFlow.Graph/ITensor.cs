// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// The minimal tensor primitive Crossbar's runtime requires. A
/// peer of <see cref="IFrame"/> for non-pixel sensor data:
/// point clouds, IMU samples, audio buffers, embedding vectors,
/// inference inputs and outputs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refcounted ownership.</b> Unlike <see cref="IFrame"/>, which
/// leaves the refcount model to the binding library (per
/// ADR-0001 §2), <see cref="ITensor"/> exposes <see cref="AddRef"/>
/// directly. Tensors are Crossbar's own primitive — there is no
/// equivalent of the Periphery / FrameFlow split — so the substrate
/// can rely on the refcount being part of the contract. This makes
/// fan-out over <see cref="ITensor"/> ergonomic: an edge can share a
/// tensor by bumping its refcount, without the caller-supplied
/// <see cref="EdgeConfig{T}.Cloner"/> that <see cref="IFrame"/> edges
/// need (ADR-0054).
/// </para>
/// <para>
/// <b>Memory domain.</b> Tensors carry a
/// <see cref="FrameMemoryDomain"/> indicating where their data lives
/// (CPU, GPU, etc.). Sinks advertise the domains they accept; the
/// runtime is responsible for inserting converter operators between
/// incompatible domains. The memory-domain shape is intentionally
/// extensible per ADR-0001 §4.
/// </para>
/// <para>
/// <b>Disposal semantics.</b> <see cref="IDisposable.Dispose"/>
/// decrements the reference count. The underlying buffer is released
/// (returned to a pool, freed, etc.) when the count reaches zero.
/// Calling <see cref="AddRef"/> after the final
/// <see cref="IDisposable.Dispose"/> is a use-after-release bug and
/// throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public interface ITensor : IDisposable
{
    /// <summary>The element type.</summary>
    DType Dtype { get; }

    /// <summary>The shape (dimensions, in order).</summary>
    TensorShape Shape { get; }

    /// <summary>The memory domain where the tensor data resides.</summary>
    FrameMemoryDomain MemoryDomain { get; }

    /// <summary>
    /// The number of bytes the tensor data occupies. For a contiguous
    /// tensor this is <c><see cref="Shape"/>.ElementCount * <see cref="Dtype"/>.ByteSize()</c>.
    /// </summary>
    long ByteCount { get; }

    /// <summary>
    /// Atomically adds one reference and returns the same instance for
    /// fluent usage. Each <see cref="AddRef"/> requires a balancing
    /// <see cref="IDisposable.Dispose"/>; the underlying buffer
    /// releases only when all references have disposed.
    /// </summary>
    /// <returns>This tensor instance.</returns>
    /// <exception cref="ObjectDisposedException">
    /// The tensor's reference count is already zero.
    /// </exception>
    ITensor AddRef();
}
