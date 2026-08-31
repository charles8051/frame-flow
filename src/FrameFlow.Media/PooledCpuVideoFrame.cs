// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// CPU-resident <see cref="IVideoFrame"/> backed by a pooled byte array
/// with atomic ref counting. The pooled sibling of the public
/// one-shot <see cref="CpuVideoFrame"/> — both implement
/// <see cref="IVideoFrame"/> but only this one supports
/// <see cref="AddRef"/> (decoder-produced frames don't).
/// </summary>
/// <remarks>
/// <para>
/// When the ref count reaches zero the backing buffer is returned to
/// <see cref="ArrayPool{T}.Shared"/> and the return-to-pool delegate is
/// invoked so the <see cref="CpuFramePool"/> semaphore can be released.
/// </para>
/// <para>
/// <b>Name + location.</b> Renamed from <c>FrameFlow.Playback.CpuVideoFrame</c>
/// during Phase 4 prep (Crossbar ADR-0014) — moving alongside <see cref="CpuFramePool"/>
/// would have collided with the existing public
/// <see cref="CpuVideoFrame"/> in this namespace, so the pooled variant
/// got the qualifier instead. Stays <c>internal sealed</c>: nothing
/// outside the pool needs to know the concrete frame type, since the
/// pool returns <see cref="IVideoFrame"/>.
/// </para>
/// </remarks>
internal sealed class PooledCpuVideoFrame : IVideoFrame
{
    private readonly Action<PooledCpuVideoFrame>? _returnToPool;
    private byte[]? _buffer;
    private readonly int _strideY;
    private readonly int _strideU;
    private readonly int _strideV;
    private int _refCount = 1;

    /// <summary>
    /// Initializes a new <see cref="PooledCpuVideoFrame"/>.
    /// </summary>
    /// <param name="returnToPool">
    /// Delegate invoked when the ref count reaches zero.
    /// May be <see langword="null"/> for stub/null frames that don't participate in pooling.
    /// </param>
    /// <param name="buffer">Rented byte array backing the frame data.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="strideY">Byte stride for the Y (or packed) plane.</param>
    /// <param name="strideU">Byte stride for the U plane (0 for packed formats).</param>
    /// <param name="strideV">Byte stride for the V plane (0 for packed formats).</param>
    /// <param name="format">Pixel format.</param>
    /// <param name="pts">Presentation timestamp.</param>
    /// <param name="duration">Display duration.</param>
    public PooledCpuVideoFrame(
        Action<PooledCpuVideoFrame>? returnToPool,
        byte[] buffer,
        int width,
        int height,
        int strideY,
        int strideU,
        int strideV,
        PixelFormat format,
        TimeSpan pts,
        TimeSpan duration
    )
    {
        _returnToPool = returnToPool;
        _buffer = buffer;
        Width = width;
        Height = height;
        _strideY = strideY;
        _strideU = strideU;
        _strideV = strideV;
        Format = format;
        _pts = pts;
        _duration = duration;
    }

    private TimeSpan _pts;
    private TimeSpan _duration;

    /// <inheritdoc />
    public TimeSpan Pts => _pts;

    /// <inheritdoc />
    public TimeSpan Duration => _duration;

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    /// <inheritdoc />
    public IVideoFrame AddRef()
    {
        // Spin until we either increment or discover the frame is already disposed.
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    nameof(PooledCpuVideoFrame),
                    "Cannot AddRef on a disposed frame."
                );
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return this;
            }
        }
    }

    /// <inheritdoc />
    public CpuFrameData? AsCpu()
    {
        var buf = _buffer;
        if (buf is null || Volatile.Read(ref _refCount) <= 0)
            return null;

        return BuildCpuFrameData(buf);
    }

    /// <inheritdoc />
    public CpuFrameData ToCpu()
    {
        var buf = _buffer;
        if (buf is null || Volatile.Read(ref _refCount) <= 0)
        {
            throw new ObjectDisposedException(
                nameof(PooledCpuVideoFrame),
                "Cannot access frame data after disposal."
            );
        }

        return BuildCpuFrameData(buf);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        int newCount = Interlocked.Decrement(ref _refCount);

        if (newCount > 0)
            return;

        if (newCount < 0)
        {
            // Already fully disposed — restore to 0 and bail.
            Interlocked.Increment(ref _refCount);
            return;
        }

        // newCount == 0 — we are the final release.
        var buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        _returnToPool?.Invoke(this);
    }

    /// <summary>
    /// Copies pixel data into the backing buffer and sets presentation metadata.
    /// Must be called exactly once before the frame is presented to a sink.
    /// </summary>
    /// <param name="source">Source pixel data to copy.</param>
    /// <param name="pts">Presentation timestamp.</param>
    /// <param name="duration">Display duration.</param>
    /// <exception cref="ObjectDisposedException">The frame has been disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> is larger than the backing buffer.
    /// </exception>
    internal void WriteData(ReadOnlySpan<byte> source, TimeSpan pts, TimeSpan duration)
    {
        var buf = _buffer ?? throw new ObjectDisposedException(nameof(PooledCpuVideoFrame));
        if (source.Length > buf.Length)
            throw new ArgumentException(
                $"Source ({source.Length} bytes) exceeds buffer capacity ({buf.Length} bytes).",
                nameof(source)
            );

        source.CopyTo(buf.AsSpan());
        _pts = pts;
        _duration = duration;
    }

    private CpuFrameData BuildCpuFrameData(byte[] buf)
    {
        // For packed formats (Bgra32, Rgba32), all data is in PlaneY.
        // For planar formats (Yuv420P, Nv12), split across planes.
        int ySize = _strideY * Height;

        if (_strideU == 0 && _strideV == 0)
        {
            // Packed format — single plane.
            return new CpuFrameData(
                PlaneY: new ReadOnlyMemory<byte>(buf, 0, ySize),
                PlaneU: ReadOnlyMemory<byte>.Empty,
                PlaneV: ReadOnlyMemory<byte>.Empty,
                StrideY: _strideY,
                StrideU: 0,
                StrideV: 0,
                Width: Width,
                Height: Height
            );
        }

        // Planar format — compute plane offsets.
        int chromaHeight = Format switch
        {
            PixelFormat.Yuv420P => Height / 2,
            PixelFormat.Nv12 => Height / 2,
            _ => Height,
        };

        int uSize = _strideU * chromaHeight;
        int vSize = _strideV * chromaHeight;

        return new CpuFrameData(
            PlaneY: new ReadOnlyMemory<byte>(buf, 0, ySize),
            PlaneU: new ReadOnlyMemory<byte>(buf, ySize, uSize),
            PlaneV: new ReadOnlyMemory<byte>(buf, ySize + uSize, vSize),
            StrideY: _strideY,
            StrideU: _strideU,
            StrideV: _strideV,
            Width: Width,
            Height: Height
        );
    }
}
