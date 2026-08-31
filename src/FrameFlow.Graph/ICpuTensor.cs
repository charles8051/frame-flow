// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// An <see cref="ITensor"/> whose data resides in CPU-accessible memory.
/// Exposes the raw byte view of the tensor's contents; typed accessors
/// belong on concrete implementations (e.g., <see cref="CpuTensor{T}"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Bytes"/> is the canonical access path for memory-domain-
/// agnostic operators (logging, hashing, framework adapters that take
/// raw byte buffers — ONNX Runtime's <c>OrtValue.CreateTensorFromBytes</c>,
/// for example). The byte ordering matches the platform's native
/// endianness; the layout is contiguous, row-major, with no padding
/// between elements (V1 invariant — see <see cref="TensorShape"/>
/// remarks).
/// </para>
/// <para>
/// The lifetime of the returned <see cref="ReadOnlyMemory{Byte}"/> is
/// the lifetime of the tensor reference held by the caller. After
/// <see cref="IDisposable.Dispose"/> drops the count to zero, the bytes
/// may have been recycled to a pool; consumers must not retain the
/// memory across the dispose boundary.
/// </para>
/// </remarks>
public interface ICpuTensor : ITensor
{
    /// <summary>
    /// The raw bytes of the tensor's contents. Length equals
    /// <see cref="ITensor.ByteCount"/>; layout is contiguous row-major.
    /// </summary>
    ReadOnlyMemory<byte> Bytes { get; }
}
