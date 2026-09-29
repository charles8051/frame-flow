// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;

namespace FrameFlow.Inference;

/// <summary>
/// An output ONNX Runtime allocated in CPU memory, read in place: <see cref="Bytes"/> is the
/// value's own buffer, and the final <see cref="Dispose"/> releases the value.
/// </summary>
/// <remarks>
/// <para>
/// Reference counted as <see cref="ITensor"/> says. A pin taken on <see cref="Bytes"/> holds a
/// reference of its own, so the buffer stays valid until the pin is disposed, even when every
/// other reference is disposed first. After the final dispose, <see cref="Bytes"/>, and the span
/// of a <see cref="ReadOnlyMemory{T}"/> taken from it earlier, throw
/// <see cref="ObjectDisposedException"/> rather than read released memory.
/// </para>
/// <para>
/// The owner is the <c>OrtValue</c> in the session; a test gives any <see cref="IDisposable"/>
/// over its own memory.
/// </para>
/// </remarks>
internal sealed unsafe class OrtOutputTensor : ICpuTensor
{
    private readonly IDisposable _owner;
    private readonly NativeBytes _bytes;
    private int _refCount = 1;

    /// <summary>
    /// Wraps <paramref name="byteCount"/> bytes at <paramref name="address"/>, which
    /// <paramref name="owner"/> keeps valid until it is disposed. Takes ownership of
    /// <paramref name="owner"/>.
    /// </summary>
    internal OrtOutputTensor(IDisposable owner, nint address, int byteCount, DType dtype, TensorShape shape)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        _owner = owner;
        _bytes = new NativeBytes(this, (byte*)address, byteCount);
        Dtype = dtype;
        Shape = shape;
        ByteCount = byteCount;
    }

    /// <inheritdoc />
    public DType Dtype { get; }

    /// <inheritdoc />
    public TensorShape Shape { get; }

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    /// <inheritdoc />
    public long ByteCount { get; }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The final reference has been disposed.</exception>
    public ReadOnlyMemory<byte> Bytes
    {
        get
        {
            ThrowIfReleased();
            return _bytes.Memory;
        }
    }

    /// <summary>True once the final reference is disposed and the owner released.</summary>
    internal bool IsReleased => Volatile.Read(ref _refCount) <= 0;

    /// <inheritdoc />
    public ITensor AddRef()
    {
        // Only from a positive count: at zero the owner has been released.
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    nameof(OrtOutputTensor), "Cannot AddRef a tensor whose final reference was disposed.");
            }
        } while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);

        return this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        int count = Interlocked.Decrement(ref _refCount);
        if (count < 0)
        {
            Interlocked.Increment(ref _refCount);
            throw new ObjectDisposedException(
                nameof(OrtOutputTensor),
                "Tensor disposed more times than its reference count permits. Each AddRef requires exactly one balancing Dispose.");
        }

        if (count == 0)
        {
            _owner.Dispose();
            ((IDisposable)_bytes).Dispose();
        }
    }

    public override string ToString() => $"OrtOutputTensor({Dtype}, {Shape}, refs={Volatile.Read(ref _refCount)})";

    private void ThrowIfReleased() => ObjectDisposedException.ThrowIf(IsReleased, this);

    /// <summary>The owner's buffer as <see cref="Memory{T}"/>. A pin holds a reference to the tensor.</summary>
    private sealed class NativeBytes : MemoryManager<byte>
    {
        private readonly OrtOutputTensor _tensor;
        private readonly byte* _pointer;
        private readonly int _length;

        public NativeBytes(OrtOutputTensor tensor, byte* pointer, int length)
        {
            _tensor = tensor;
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan()
        {
            _tensor.ThrowIfReleased();
            return new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
            _tensor.AddRef();
            return new MemoryHandle(_pointer + elementIndex, pinnable: this);
        }

        public override void Unpin() => _tensor.Dispose();

        protected override void Dispose(bool disposing)
        {
            // The tensor owns the buffer; nothing is held here.
        }
    }
}
