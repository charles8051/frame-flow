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
