using System.Buffers;

namespace FrameFlow.Media.Tests.Doubles;

/// <summary>
/// A controllable <see cref="IMemoryOwner{T}"/> that wraps a fixed byte array and tracks
/// how many times <see cref="Dispose"/> has been called. Safe to call multiple times.
/// Used to verify that <see cref="CpuVideoFrame"/> disposes its pixel data exactly as
/// required by the ownership contract (ADR-0005, ADR-0012).
/// </summary>
/// <typeparam name="T">Element type — typically <c>byte</c> for pixel data.</typeparam>
internal sealed class FakeMemoryOwner<T> : IMemoryOwner<T>
{
    private readonly T[] _buffer;

    /// <summary>Number of times <see cref="Dispose"/> has been called.</summary>
    public int DisposeCallCount { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has been called at least once.</summary>
    public bool IsDisposed => DisposeCallCount > 0;

    public FakeMemoryOwner(T[] buffer)
    {
        _buffer = buffer;
    }

    /// <summary>Creates a fake owner backed by a zero-filled buffer of the requested length.</summary>
    public static FakeMemoryOwner<T> OfLength(int length) => new FakeMemoryOwner<T>(new T[length]);

    /// <summary>Creates a fake owner backed by the supplied array (no copy).</summary>
    public static FakeMemoryOwner<T> FromArray(T[] buffer) => new FakeMemoryOwner<T>(buffer);

    public Memory<T> Memory => _buffer.AsMemory();

    public void Dispose() => DisposeCallCount++;
}
