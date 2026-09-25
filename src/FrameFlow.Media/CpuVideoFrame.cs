// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// A video frame whose pixels reside in CPU memory, in up to three tightly packed planes.
/// </summary>
/// <remarks>
/// <para>
/// A frame is created by <see cref="Create{TState}"/>, which rents storage, runs a fill
/// callback over the writable planes, and returns the frame. Nothing writes to it after that
/// (ADR-0080, decision 5): <see cref="AsCpu"/> is read-only, and no holder can reach the
/// storage to free it.
/// </para>
/// <para>
/// The frame counts its own references (<see cref="RefCounting"/>, ADR-0080):
/// <see cref="AddRef"/> returns this same instance, every holder shares the one buffer, and the
/// final <see cref="Dispose"/> returns the storage to its pool. After that <see cref="AsCpu"/>
/// returns <see langword="null"/> and <see cref="ToCpu"/> throws. The
/// <see cref="ReadOnlyMemory{T}"/> views an earlier <see cref="AsCpu"/> returned are not
/// counted; a debug build fills released storage with a fixed byte so a read through one
/// fails every time rather than only when the array has been reused.
/// </para>
/// </remarks>
public sealed class CpuVideoFrame : IVideoFrame
{
    /// <summary>The byte a debug build writes over storage when the frame is released.</summary>
    internal const byte ReleasedFill = 0xDD;

    private int _refCount = 1;
    private readonly byte[] _storage;
    private readonly ArrayPool<byte> _pool;
    private readonly CpuFrameLayout _layout;

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <summary>Bytes per row of the first plane.</summary>
    public int Stride => _layout.StrideY;

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <summary>Presentation timestamp relative to the start of the media stream.</summary>
    public TimeSpan PresentationTime { get; }

    // ── IVideoFrame metadata ──────────────────────────────────────────

    /// <inheritdoc />
    public TimeSpan Pts => PresentationTime;

    /// <inheritdoc />
    public TimeSpan Duration { get; }

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    private CpuVideoFrame(
        byte[] storage,
        ArrayPool<byte> pool,
        CpuFrameLayout layout,
        PixelFormat format,
        int width,
        int height,
        TimeSpan presentationTime,
        TimeSpan duration
    )
    {
        _storage = storage;
        _pool = pool;
        _layout = layout;
        Format = format;
        Width = width;
        Height = height;
        PresentationTime = presentationTime;
        Duration = duration;
        Diagnostics.CpuFrameMetrics.OnFrameCreated(storage.Length);
    }

    /// <summary>
    /// Creates a frame: rents storage for <paramref name="format"/> at the given size, runs
    /// <paramref name="fill"/> over its planes, and returns the frame.
    /// </summary>
    /// <typeparam name="TState">State passed through to <paramref name="fill"/>.</typeparam>
    /// <param name="format">The pixel format, which sets the planes and their strides.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="presentationTime">Presentation timestamp.</param>
    /// <param name="duration">How long the frame is displayed.</param>
    /// <param name="state">Passed to <paramref name="fill"/>, so it can be a static lambda that allocates nothing.</param>
    /// <param name="fill">Writes the pixels. It runs once, before the frame exists.</param>
    /// <param name="pool">Where the storage comes from. Defaults to <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <returns>The frame, holding one reference.</returns>
    /// <remarks>
    /// If <paramref name="fill"/> throws, the storage goes back to <paramref name="pool"/> and
    /// the exception propagates unchanged, even when the pool's <c>Return</c> throws too. No
    /// frame is created.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="fill"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative, or the frame does not fit in one array.</exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not a known pixel format.</exception>
    public static CpuVideoFrame Create<TState>(
        PixelFormat format,
        int width,
        int height,
        TimeSpan presentationTime,
        TimeSpan duration,
        TState state,
        CpuVideoFrameFill<TState> fill,
        ArrayPool<byte>? pool = null
    )
    {
        ArgumentNullException.ThrowIfNull(fill);

        var layout = CpuFrameLayout.For(format, width, height);
        pool ??= ArrayPool<byte>.Shared;
        byte[] storage = pool.Rent(layout.TotalBytes);

        try
        {
            fill(new CpuVideoFramePlanes(storage, layout, format, width, height), state);
        }
        catch
        {
            ReturnStorageAfterFailure(pool, storage);
            throw;
        }

        return new CpuVideoFrame(
            storage,
            pool,
            layout,
            format,
            width,
            height,
            presentationTime,
            duration
        );
    }

    // ── IVideoFrame ref counting ──────────────────────────────────────

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The frame has already been released.</exception>
    public IVideoFrame AddRef()
    {
        RefCounting.AddRef(ref _refCount, this);
        return this;
    }

    // ── IVideoFrame domain access ─────────────────────────────────────

    /// <inheritdoc />
    public CpuFrameData? AsCpu()
    {
        if (Volatile.Read(ref _refCount) <= 0)
            return null;

        return new CpuFrameData(
            PlaneY: _storage.AsMemory(0, _layout.LengthY),
            PlaneU: _storage.AsMemory(_layout.OffsetU, _layout.LengthU),
            PlaneV: _storage.AsMemory(_layout.OffsetV, _layout.LengthV),
            StrideY: _layout.StrideY,
            StrideU: _layout.StrideU,
            StrideV: _layout.StrideV,
            Width: Width,
            Height: Height
        );
    }

    /// <inheritdoc />
    public CpuFrameData ToCpu() =>
        AsCpu() ?? throw new ObjectDisposedException(nameof(CpuVideoFrame));

    /// <inheritdoc />
    public void Dispose()
    {
        if (!RefCounting.Release(ref _refCount, this))
            return;

        try
        {
            ReturnStorage(_pool, _storage);
        }
        finally
        {
            // The frame is released whether or not the pool's Return throws.
            Diagnostics.CpuFrameMetrics.OnFrameReleased(_storage.Length);
        }
    }

    private static void ReturnStorage(ArrayPool<byte> pool, byte[] storage)
    {
#if DEBUG
        storage.AsSpan().Fill(ReleasedFill);
#endif
        pool.Return(storage);
    }

    /// <summary>
    /// Returns the storage on a failure path. The failure's own exception is the one the caller
    /// sees; a pool that also throws from <c>Return</c> leaves the array to the garbage collector.
    /// </summary>
    private static void ReturnStorageAfterFailure(ArrayPool<byte> pool, byte[] storage)
    {
        try
        {
            ReturnStorage(pool, storage);
        }
        catch (Exception)
        {
            // Deliberately dropped: rethrowing here would replace the exception being reported.
        }
    }
}
