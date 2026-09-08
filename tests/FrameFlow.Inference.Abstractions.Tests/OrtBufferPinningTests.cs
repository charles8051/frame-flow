using System.Buffers;
using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Guards the buffer-lifetime invariant that
/// <c>OrtInferenceSessionBase.BindCpuTensor</c> depends on: a pin taken
/// over <see cref="ICpuTensor.Bytes"/> and <em>held</em> keeps the buffer
/// at a fixed address across a compacting GC.
/// </summary>
/// <remarks>
/// <para>
/// <c>OrtValue.CreateTensorValueWithData</c> stores the raw address and
/// dereferences it later, during <c>RunWithBinding</c>. The bind loop
/// allocates between those two points, so a GC can land there. Binding
/// under a <c>fixed</c> block released the pin before the value was even
/// returned, leaving ORT holding an address the GC was free to vacate.
/// </para>
/// <para>
/// <b>Scope.</b> This exercises the mechanism, not the call site —
/// <c>BindCpuTensor</c> needs a real <c>InferenceSession</c> (GPU/DML
/// natives plus a model), which this GPU-free suite does not construct.
/// It fails if <see cref="ICpuTensor.Bytes"/> is ever backed by something
/// a <see cref="MemoryHandle"/> cannot hold in place.
/// </para>
/// </remarks>
public sealed class OrtBufferPinningTests
{
    [Fact]
    public void AHeldPinKeepsAPooledTensorBufferAtOneAddress()
    {
        using var pool = new CpuTensorPool();
        // Small enough to land on the compacting heap rather than the LOH,
        // which does not move and so would prove nothing.
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));

        var pin = tensor.Bytes.Pin();
        try
        {
            var before = AddressOf(pin);

            ChurnTheHeapAndCompact();

            Assert.Equal(before, AddressOf(pin));
        }
        finally
        {
            pin.Dispose();
        }
    }

    [Fact]
    public void EveryHeldPinSurvivesTheAllocationsOfTheBindsThatFollowIt()
    {
        // The shape of Run's bind loop: each later bind allocates (a shape
        // array, an OrtValue, list growth) while earlier buffers are already
        // bound and must not move.
        using var pool = new CpuTensorPool();
        var tensors = new List<CpuTensor<float>>();
        var pins = new List<MemoryHandle>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));
                tensors.Add(tensor);
                pins.Add(tensor.Bytes.Pin());
            }

            var addresses = pins.Select(AddressOf).ToArray();

            ChurnTheHeapAndCompact();

            Assert.Equal(addresses, pins.Select(AddressOf).ToArray());
        }
        finally
        {
            foreach (var pin in pins)
                pin.Dispose();
            foreach (var tensor in tensors)
                tensor.Dispose();
        }
    }

    // ── PinForBinding: the seam BindCpuTensor actually calls ─────────────

    [Fact]
    public void PinForBindingReturnsAHandleThatIsHoldingTheBuffer()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));

        using var pin = OrtInferenceSessionBase.PinForBinding(tensor);

        // A released pin — what `fixed` leaves behind, and what
        // default(MemoryHandle) would be — carries a null pointer.
        Assert.NotEqual(IntPtr.Zero, AddressOf(pin));

        // A second pin of the same buffer must land on the same address:
        // proof the handle refers to this tensor's memory and not a copy.
        using var alias = tensor.Bytes.Pin();
        Assert.Equal(AddressOf(alias), AddressOf(pin));
    }

    [Fact]
    public void TheAddressFromPinForBindingSurvivesACompactingGc()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));

        // The bug's shape: take the address, return, let the caller keep
        // allocating, then dereference. Here the pin comes back with the
        // address, so the buffer cannot move under it.
        using var pin = OrtInferenceSessionBase.PinForBinding(tensor);
        var address = AddressOf(pin);

        ChurnTheHeapAndCompact();

        Assert.Equal(address, AddressOf(pin));
        using var alias = tensor.Bytes.Pin();
        Assert.Equal(address, AddressOf(alias));
    }

    // ── PinAndRegister: the ownership handoff BindCpuTensor performs ─────

    [Fact]
    public void PinAndRegisterHandsTheCallerThePinBehindTheAddressItReturns()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));
        var pins = new List<MemoryHandle>(1);

        var address = OrtInferenceSessionBase.PinAndRegister(tensor, pins);

        // The address ORT would be given must be the address of the pin the
        // caller now owns — not a second, unregistered pin, and not an
        // address whose pin was dropped on the way out.
        var registered = Assert.Single(pins);
        Assert.Equal(AddressOf(registered), address);
        Assert.NotEqual(IntPtr.Zero, address);

        try
        {
            ChurnTheHeapAndCompact();

            // Still the buffer's address, because the pin in `pins` held it.
            Assert.Equal(address, AddressOf(registered));
            using var alias = tensor.Bytes.Pin();
            Assert.Equal(address, AddressOf(alias));
        }
        finally
        {
            foreach (var pin in pins)
                pin.Dispose();
        }
    }

    // ── BindPinned: the whole bind body, minus the ORT call ──────────────

    [Fact]
    public void BindPinnedBuildsTheValueOverTheAddressTheRegisteredPinProtects()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));
        var boundValues = new List<IntPtr>(1);
        var pins = new List<MemoryHandle>(1);

        // The value stands in for the OrtValue: it records exactly the
        // address that would have gone to CreateTensorValueWithData.
        var value = OrtInferenceSessionBase.BindPinned(
            tensor,
            boundValues,
            pins,
            createValue: address => address,
            disposeValue: static _ => { }
        );

        try
        {
            var registered = Assert.Single(pins);
            Assert.Equal(AddressOf(registered), value);
            Assert.Equal(value, Assert.Single(boundValues));
            Assert.NotEqual(IntPtr.Zero, value);

            ChurnTheHeapAndCompact();

            // The address ORT was given still names this tensor's buffer.
            Assert.Equal(value, AddressOf(registered));
            using var alias = tensor.Bytes.Pin();
            Assert.Equal(value, AddressOf(alias));
        }
        finally
        {
            foreach (var pin in pins)
                pin.Dispose();
        }
    }

    [Fact]
    public void BindPinnedLeavesThePinRegisteredWhenValueCreationThrows()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));
        var boundValues = new List<IntPtr>(1);
        var pins = new List<MemoryHandle>(1);

        Assert.Throws<InvalidOperationException>(
            () =>
                OrtInferenceSessionBase.BindPinned<IntPtr>(
                    tensor,
                    boundValues,
                    pins,
                    createValue: _ => throw new InvalidOperationException("ORT said no."),
                    disposeValue: static _ => { }
                )
        );

        // The pin must already be the caller's, or Run's finally cannot
        // free it and the buffer stays pinned for the process's life.
        Assert.Single(pins);
        Assert.Empty(boundValues);
        foreach (var pin in pins)
            pin.Dispose();
    }

    [Fact]
    public void BindPinnedDisposesTheValueWhenRegisteringItThrows()
    {
        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(new TensorShape(1, 896, 1));
        var pins = new List<MemoryHandle>(1);
        var disposed = new List<IntPtr>();

        Assert.Throws<NotSupportedException>(
            () =>
                OrtInferenceSessionBase.BindPinned(
                    tensor,
                    new RefusingList<IntPtr>(),
                    pins,
                    createValue: address => address,
                    disposeValue: disposed.Add
                )
        );

        // Nothing else knows about the value, so this is its only chance.
        Assert.Single(disposed);
        Assert.Single(pins);
        foreach (var pin in pins)
            pin.Dispose();
    }

    /// <summary>A registry that refuses, standing in for an Add that throws.</summary>
    private sealed class RefusingList<T> : ICollection<T>
    {
        public void Add(T item) => throw new NotSupportedException();

        public int Count => 0;
        public bool IsReadOnly => false;

        public void Clear() { }

        public bool Contains(T item) => false;

        public void CopyTo(T[] array, int arrayIndex) { }

        public bool Remove(T item) => false;

        public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    [Fact]
    public void PinForBindingRejectsATensorReportingMoreBytesThanItExposes()
    {
        // ORT is told it may read ByteCount bytes from the address. A tensor
        // that over-reports would have it read off the end of the pin.
        var lying = new OverReportingTensor(exposed: 64, reported: 4096);

        var ex = Assert.Throws<ArgumentException>(
            () => OrtInferenceSessionBase.PinForBinding(lying)
        );
        Assert.Contains("past the end", ex.Message, StringComparison.Ordinal);
    }

    private sealed class OverReportingTensor(int exposed, long reported) : ICpuTensor
    {
        private readonly byte[] _buffer = new byte[exposed];

        public DType Dtype => DType.UInt8;
        public TensorShape Shape => new(exposed);
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;
        public long ByteCount => reported;
        public ReadOnlyMemory<byte> Bytes => _buffer;

        public ITensor AddRef() => this;

        public void Dispose() { }
    }

    private static IntPtr AddressOf(MemoryHandle handle)
    {
        unsafe
        {
            return (IntPtr)handle.Pointer;
        }
    }

    private static void ChurnTheHeapAndCompact()
    {
        for (int i = 0; i < 256; i++)
            _ = new byte[8 * 1024];

        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
