using System.Buffers;

namespace FrameFlow.Media.Tests.Doubles;

/// <summary>
/// An <see cref="ArrayPool{T}"/> that hands out a fresh array per rent and counts what comes
/// back. It never reuses an array, so storage a test held on to keeps whatever was last written
/// to it, including the fill a debug build writes on release.
/// </summary>
internal sealed class CountingArrayPool<T> : ArrayPool<T>
{
    private int _rents;
    private int _returns;

    /// <summary>When set, <see cref="Return"/> throws instead of counting.</summary>
    public bool ThrowOnReturn { get; init; }

    public int Rents => Volatile.Read(ref _rents);

    public int Returns => Volatile.Read(ref _returns);

    /// <summary>The array the last <see cref="Rent"/> handed out.</summary>
    public T[]? LastRented { get; private set; }

    public override T[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rents);
        var array = new T[minimumLength];
        LastRented = array;
        return array;
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        if (ThrowOnReturn)
            throw new InvalidOperationException("pool failed");
        Interlocked.Increment(ref _returns);
    }
}
