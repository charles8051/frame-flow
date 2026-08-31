// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Graph;

/// <summary>
/// CPU-resident tensor with strongly-typed element access. Backed by a
/// pool-rented <see cref="byte"/>[] buffer; refcount-based ownership
/// returns the buffer to the pool when the final reference disposes.
/// </summary>
/// <typeparam name="T">
/// Element type. Must be unmanaged and match a known <see cref="DType"/>;
/// see the constructor for the supported set.
/// </typeparam>
/// <remarks>
/// <para>
/// Construct via <see cref="CpuTensorPool.Rent{T}"/> rather than
/// directly — the pool tracks outstanding tensors for diagnostics and
/// returns the buffer on the final dispose.
/// </para>
/// <para>
/// <b>Mutability split.</b> The interface surface
/// (<see cref="ICpuTensor.Bytes"/>) is read-only. The concrete
/// <see cref="CpuTensor{T}.Span"/> property exposes a writable
/// <see cref="System.Span{T}"/>, which is the canonical path for the
/// producer that just rented the tensor to fill its data. Once the
/// tensor is handed downstream (where consumers see it through
/// <see cref="ICpuTensor"/>), it should be treated as immutable; the
/// type system can't enforce this for you, but the convention matches
/// how <c>FrameFlow.Media.IVideoFrame</c> and
/// <c>Periphery.Camera.ICameraFrame</c> behave today.
/// </para>
/// </remarks>
public sealed class CpuTensor<T> : ICpuTensor
    where T : unmanaged
{
    private static readonly DType ElementDtype = ResolveDType();

    private readonly byte[] _buffer;
    private readonly int _byteCount;
    private readonly CpuTensorPool? _pool;
    private int _refCount;

    /// <summary>
    /// Constructs a tensor wrapping the supplied buffer. Internal because
    /// the pool is the canonical construction site;
    /// <see cref="CpuTensorPool.Rent{T}"/> calls this and returns the
    /// fresh tensor with refcount 1.
    /// </summary>
    internal CpuTensor(byte[] buffer, TensorShape shape, CpuTensorPool? pool)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Shape = shape;
        _pool = pool;
        _byteCount = checked((int)shape.ByteCount(ElementDtype));
        if (_byteCount > buffer.Length)
        {
            throw new ArgumentException(
                $"Buffer is {buffer.Length} bytes; shape {shape} of dtype "
                    + $"{ElementDtype} requires {_byteCount} bytes.",
                nameof(buffer)
            );
        }
        _buffer = buffer;
        _refCount = 1;
    }

    /// <inheritdoc/>
    public DType Dtype => ElementDtype;

    /// <inheritdoc/>
    public TensorShape Shape { get; }

    /// <inheritdoc/>
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    /// <inheritdoc/>
    public long ByteCount => _byteCount;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Bytes => _buffer.AsMemory(0, _byteCount);

    /// <summary>
    /// A writable, strongly-typed span over the tensor's elements. The
    /// producer that owns the tensor uses this to fill the data; consumers
    /// receiving the tensor through <see cref="ICpuTensor"/> should treat
    /// the contents as read-only (see remarks on <see cref="CpuTensor{T}"/>).
    /// </summary>
    public Span<T> Span => MemoryMarshal.Cast<byte, T>(_buffer.AsSpan(0, _byteCount));

    /// <summary>
    /// A read-only typed view over the tensor's elements. Equivalent to
    /// <see cref="Span"/> but signals read-only intent at the call site.
    /// </summary>
    public ReadOnlySpan<T> ReadOnlySpan =>
        MemoryMarshal.Cast<byte, T>(_buffer.AsSpan(0, _byteCount));

    /// <inheritdoc/>
    public ITensor AddRef()
    {
        // CAS loop: only increment if the count is currently positive. A
        // count of zero means the buffer has already been returned to the
        // pool; AddRef'ing in that state is a use-after-release bug.
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    nameof(CpuTensor<T>),
                    "Cannot AddRef a disposed tensor (its buffer has been returned to the pool)."
                );
            }
        } while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);
        return this;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        var newCount = Interlocked.Decrement(ref _refCount);
        if (newCount < 0)
        {
            // Restore the count to keep diagnostics monotonic and signal
            // the bug to the caller; the buffer was already released on
            // the prior dispose.
            Interlocked.Increment(ref _refCount);
            throw new ObjectDisposedException(
                nameof(CpuTensor<T>),
                "Tensor disposed more times than its reference count permits. Each AddRef requires exactly one balancing Dispose."
            );
        }
        if (newCount == 0)
        {
            _pool?.Return(_buffer);
        }
    }

    public override string ToString() =>
        $"CpuTensor<{typeof(T).Name}>({Shape}, refs={Volatile.Read(ref _refCount)})";

    /// <summary>
    /// Maps the type parameter <typeparamref name="T"/> to its
    /// <see cref="DType"/>. Resolved once per generic instantiation.
    /// </summary>
    private static DType ResolveDType()
    {
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
            $"CpuTensor<{typeof(T).Name}>: element type has no corresponding DType. "
                + "Use one of: float, Half, double, sbyte, byte, short, ushort, int, uint, long, ulong, bool."
        );
    }
}
