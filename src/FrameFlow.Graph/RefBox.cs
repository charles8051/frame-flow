// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Refcount-aware wrapper for items that don't natively implement
/// <see cref="IRefCounted"/>. Lets value types and POCOs ride the
/// substrate uniformly. When the refcount reaches zero, an optional
/// dispose action is invoked on the wrapped value (for cases where
/// the value itself owns a resource).
/// </summary>
/// <typeparam name="T">The wrapped value type.</typeparam>
/// <remarks>
/// <para>
/// The substrate uses this internally to wrap arbitrary item types.
/// Operators authored in the substrate can take either a raw
/// <see cref="IRefCounted"/>-implementing type or use <see cref="RefBox{T}"/>
/// for ordinary values.
/// </para>
/// </remarks>
public sealed class RefBox<T> : IRefCounted
{
    private int _refCount;
    private readonly Action<T>? _onLastRelease;

    public RefBox(T value, Action<T>? onLastRelease = null)
    {
        Value = value;
        _onLastRelease = onLastRelease;
        _refCount = 1;
    }

    /// <summary>The wrapped value.</summary>
    public T Value { get; }

    /// <summary>Current reference count. Exposed for diagnostics / tests.</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    public IRefCounted AddRef()
    {
        var newCount = Interlocked.Increment(ref _refCount);
        if (newCount <= 1)
        {
            throw new ObjectDisposedException(
                nameof(RefBox<T>),
                "AddRef called after the last reference was disposed."
            );
        }
        return this;
    }

    public void Dispose()
    {
        var newCount = Interlocked.Decrement(ref _refCount);
        if (newCount < 0)
        {
            throw new ObjectDisposedException(
                nameof(RefBox<T>),
                "Dispose called more times than AddRef + construction."
            );
        }
        if (newCount == 0)
        {
            _onLastRelease?.Invoke(Value);
        }
    }
}

/// <summary>
/// Convenience factory for boxing values into refcounted wrappers.
/// </summary>
public static class RefBox
{
    public static RefBox<T> Of<T>(T value, Action<T>? onLastRelease = null) =>
        new(value, onLastRelease);
}
