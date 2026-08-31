// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Graph;

/// <summary>
/// The shape of an <see cref="ITensor"/> — an ordered list of positive
/// dimensions. Immutable and value-typed; equality is structural (two
/// shapes with the same dimensions in the same order are equal).
/// </summary>
/// <remarks>
/// <para>
/// Storage is <see cref="ImmutableArray{Int32}"/>. Hot-path access via
/// <see cref="AsSpan"/> returns a <see cref="ReadOnlySpan{Int32}"/>
/// that's stack-only and JIT-friendly for tight loops; the indexer is
/// fine for incidental access. The choice of immutable storage rather
/// than a raw array prevents external mutation; the choice of a
/// non-ref-struct wrapper keeps tensors usable across <c>await</c>
/// boundaries, lambda captures, and generic type arguments — all of
/// which a <c>ref struct</c> shape would forbid.
/// </para>
/// <para>
/// V1 represents <b>contiguous, row-major</b> tensors only. Strides are
/// derived from the shape. Non-contiguous tensors (zero-copy slices,
/// broadcasted views) require an explicit strides field; that lands in
/// a later iteration when the use case forces it.
/// </para>
/// </remarks>
public readonly struct TensorShape : IEquatable<TensorShape>
{
    private readonly ImmutableArray<int> _dims;

    /// <summary>
    /// Constructs a shape from a sequence of positive dimensions.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="dims"/> is empty or contains a non-positive value.
    /// </exception>
    public TensorShape(params int[] dims)
    {
        ArgumentNullException.ThrowIfNull(dims);
        if (dims.Length == 0)
        {
            throw new ArgumentException(
                "A tensor shape must have at least one dimension.",
                nameof(dims)
            );
        }
        for (int i = 0; i < dims.Length; i++)
        {
            if (dims[i] <= 0)
            {
                throw new ArgumentException(
                    $"Dimension {i} is {dims[i]}; all dimensions must be positive.",
                    nameof(dims)
                );
            }
        }
        _dims = ImmutableArray.Create(dims);
    }

    /// <summary>
    /// Constructs a shape from an existing <see cref="ImmutableArray{Int32}"/>.
    /// Useful when the caller has already validated and built the array
    /// (e.g., when slicing or transposing an existing shape).
    /// </summary>
    public TensorShape(ImmutableArray<int> dims)
    {
        if (dims.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A tensor shape must have at least one dimension.",
                nameof(dims)
            );
        }
        for (int i = 0; i < dims.Length; i++)
        {
            if (dims[i] <= 0)
            {
                throw new ArgumentException(
                    $"Dimension {i} is {dims[i]}; all dimensions must be positive.",
                    nameof(dims)
                );
            }
        }
        _dims = dims;
    }

    /// <summary>The number of dimensions (the rank).</summary>
    public int Rank => _dims.IsDefault ? 0 : _dims.Length;

    /// <summary>
    /// The size of dimension <paramref name="index"/>. Standard 0-based
    /// indexing; throws on out-of-range.
    /// </summary>
    public int this[int index] => _dims[index];

    /// <summary>
    /// Returns a stack-only span over the dimensions for tight inner loops
    /// where every cycle counts. Allocates nothing.
    /// </summary>
    public ReadOnlySpan<int> AsSpan() => _dims.AsSpan();

    /// <summary>
    /// The product of all dimensions — the number of elements the tensor
    /// holds. Computed lazily; iterates the dims via <see cref="AsSpan"/>.
    /// </summary>
    public long ElementCount
    {
        get
        {
            long count = 1;
            foreach (var d in _dims.AsSpan())
                count *= d;
            return count;
        }
    }

    /// <summary>
    /// The number of bytes a tensor of this shape and the given
    /// <paramref name="dtype"/> occupies (assuming contiguous layout).
    /// </summary>
    public long ByteCount(DType dtype) => ElementCount * dtype.ByteSize();

    public bool Equals(TensorShape other)
    {
        if (_dims.IsDefault && other._dims.IsDefault)
            return true;
        if (_dims.IsDefault || other._dims.IsDefault)
            return false;
        return _dims.AsSpan().SequenceEqual(other._dims.AsSpan());
    }

    public override bool Equals(object? obj) => obj is TensorShape other && Equals(other);

    public override int GetHashCode()
    {
        if (_dims.IsDefault)
            return 0;
        var hash = new HashCode();
        foreach (var d in _dims.AsSpan())
            hash.Add(d);
        return hash.ToHashCode();
    }

    public static bool operator ==(TensorShape left, TensorShape right) => left.Equals(right);

    public static bool operator !=(TensorShape left, TensorShape right) => !left.Equals(right);

    public override string ToString()
    {
        if (_dims.IsDefault)
            return "[]";
        return "[" + string.Join(",", _dims) + "]";
    }
}
