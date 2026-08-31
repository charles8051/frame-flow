// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;

namespace FrameFlow.Graph;

/// <summary>
/// Pool that allocates and recycles CPU-resident tensor buffers.
/// Concrete <see cref="CpuTensor{T}"/> instances rent from the pool;
/// the buffer returns when the final reference disposes.
/// </summary>
/// <remarks>
/// <para>
/// Backed by an <see cref="ArrayPool{Byte}"/> — by default the shared
/// pool, which is sufficient for general-purpose use. A custom pool may
/// be supplied to constrain memory pressure for known-size workloads.
/// </para>
/// <para>
/// The pool tracks <see cref="Outstanding"/> as a diagnostic — the
/// number of tensors that have been rented and not yet had their final
/// reference disposed. A persistently growing value indicates a
/// downstream consumer leaking references.
/// </para>
/// <para>
/// The pool itself is thread-safe; concurrent <see cref="Rent{T}"/> and
/// internal-Return calls are lock-free.
/// </para>
/// </remarks>
public sealed class CpuTensorPool : IDisposable
{
    private readonly ArrayPool<byte> _arrayPool;
    private long _outstanding;
    private long _totalRented;
    private long _totalReturned;
    private bool _disposed;

    /// <summary>
    /// Number of tensors that have been rented and not yet had their
    /// final reference disposed.
    /// </summary>
    public long Outstanding => Interlocked.Read(ref _outstanding);

    /// <summary>Total tensors ever rented from this pool.</summary>
    public long TotalRented => Interlocked.Read(ref _totalRented);

    /// <summary>Total tensors whose final reference has disposed.</summary>
    public long TotalReturned => Interlocked.Read(ref _totalReturned);

    /// <summary>
    /// Creates a pool backed by the supplied <paramref name="arrayPool"/>,
    /// or <see cref="ArrayPool{Byte}.Shared"/> by default.
    /// </summary>
    public CpuTensorPool(ArrayPool<byte>? arrayPool = null)
    {
        _arrayPool = arrayPool ?? ArrayPool<byte>.Shared;
    }

    /// <summary>
    /// Rents a tensor of the given <paramref name="shape"/> with element
    /// type <typeparamref name="T"/>. The returned tensor has refcount 1
    /// and is owned by the caller; <c>Dispose</c> drops the count to zero
    /// and returns the underlying buffer to the pool.
    /// </summary>
    /// <typeparam name="T">
    /// The element type. Must be unmanaged and have a corresponding
    /// <see cref="DType"/>; see <see cref="CpuTensor{T}"/> for the list.
    /// </typeparam>
    public CpuTensor<T> Rent<T>(TensorShape shape)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var byteCount = shape.ByteCount(GetDType<T>());
        if (byteCount > int.MaxValue)
        {
            throw new ArgumentException(
                $"Tensor would require {byteCount} bytes, exceeding int.MaxValue ({int.MaxValue}).",
                nameof(shape)
            );
        }

        var buffer = _arrayPool.Rent((int)byteCount);
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _totalRented);
        return new CpuTensor<T>(buffer, shape, this);
    }

    internal void Return(byte[] buffer)
    {
        _arrayPool.Return(buffer);
        Interlocked.Decrement(ref _outstanding);
        Interlocked.Increment(ref _totalReturned);
    }

    /// <summary>
    /// Marks the pool as disposed. Subsequent <see cref="Rent{T}"/> calls
    /// throw; in-flight tensors that haven't had their final reference
    /// disposed remain valid and will return their buffers normally.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        // ArrayPool.Shared lives forever; nothing to release. Custom
        // arraypools managed by the caller are the caller's concern.
    }

    private static DType GetDType<T>()
        where T : unmanaged
    {
        // Mirrors CpuTensor<T>.ResolveDType — duplicated here so the
        // pool can compute byte size without instantiating the tensor
        // (which would trigger ResolveDType anyway, but we'd then have
        // to reach into the private static field). Simpler to duplicate
        // the small switch.
        if (typeof(T) == typeof(float))
            return DType.Float32;
        if (typeof(T) == typeof(Half))
            return DType.Float16;
        if (typeof(T) == typeof(double))
            return DType.Float64;
        if (typeof(T) == typeof(sbyte))
            return DType.Int8;
        if (typeof(T) == typeof(byte))
            return DType.UInt8;
        if (typeof(T) == typeof(short))
            return DType.Int16;
        if (typeof(T) == typeof(ushort))
            return DType.UInt16;
        if (typeof(T) == typeof(int))
            return DType.Int32;
        if (typeof(T) == typeof(uint))
            return DType.UInt32;
        if (typeof(T) == typeof(long))
            return DType.Int64;
        if (typeof(T) == typeof(ulong))
            return DType.UInt64;
        if (typeof(T) == typeof(bool))
            return DType.Bool;
        throw new NotSupportedException(
            $"CpuTensorPool.Rent<{typeof(T).Name}>: element type has no corresponding DType."
        );
    }
}
