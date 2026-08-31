using Periphery.Camera;

namespace FrameFlow.Camera.Tests.Fakes;

/// <summary>
/// Minimal ref-counted <see cref="ICameraFrame"/> for bridge tests.
/// Exposes <see cref="RefCount"/> so tests can assert AddRef/Dispose
/// balance, and throws <see cref="ObjectDisposedException"/> on
/// AddRef-after-zero (matching Periphery's lease semantics in
/// <c>Periphery.Camera.Tests.Fakes.FakeFrame</c>). The payload fields
/// (Width / Height / Plane / buffer) return trivial stubs — the bridge
/// itself never inspects them, only manages refs.
/// </summary>
internal sealed class FakeCameraFrame : ICameraFrame
{
    private int _refCount = 1;

    /// <summary>Current refcount. <c>1</c> at construction. Tests assert balance off this.</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    public int Width => 1;
    public int Height => 1;
    public TimeSpan Timestamp => TimeSpan.Zero;
    public CameraPixelFormat PixelFormat => CameraPixelFormat.Bgra32;
    public int PlaneCount => 1;
    public bool IsContiguous => true;
    public ReadOnlyMemory<byte> ContiguousBuffer => ReadOnlyMemory<byte>.Empty;

    public CameraPlane GetPlane(int index) =>
        new(Buffer: ReadOnlyMemory<byte>.Empty, Stride: 0, Width: 1, Height: 1);

    public ICameraFrame AddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(FakeCameraFrame));
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return this;
        }
    }

    public void Dispose()
    {
        var n = Interlocked.Decrement(ref _refCount);
        if (n < 0)
        {
            // Idempotent under stress; tests asserting double-dispose
            // detection inspect RefCount directly.
            Interlocked.Increment(ref _refCount);
        }
    }
}
