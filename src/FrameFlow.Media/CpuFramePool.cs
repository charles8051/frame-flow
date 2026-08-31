// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Media;

/// <summary>
/// CPU-backed <see cref="IFramePool"/> bounded by a <see cref="SemaphoreSlim"/>
/// to enforce backpressure when all frames are in-flight.
/// </summary>
/// <remarks>
/// <para>
/// Frames are backed by <see cref="ArrayPool{T}.Shared"/>. The semaphore count
/// equals the pool capacity — each <see cref="RentAsync"/> decrements the count
/// and each <see cref="Return"/> increments it.
/// </para>
/// <para>
/// <b>Location.</b> Lives in <c>FrameFlow.Media</c> rather than
/// <c>FrameFlow.Playback</c>, where it started (moved during Crossbar
/// ADR-0014 Phase 4 prep). Sinks (Avalonia / SDL) and examples
/// (FrameDumper / SdlPlayer) need a pool, and should not have to pull in the
/// playback assembly to get one.
/// </para>
/// </remarks>
public sealed class CpuFramePool : IFramePool
{
    private readonly ILogger<CpuFramePool> _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _capacity;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="CpuFramePool"/>.
    /// </summary>
    /// <param name="logger">Logger for structured diagnostics.</param>
    /// <param name="capacity">Maximum number of frames that can be in-flight simultaneously. Defaults to 3.</param>
    public CpuFramePool(ILogger<CpuFramePool> logger, int capacity = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);

        _logger = logger;
        _capacity = capacity;
        _semaphore = new SemaphoreSlim(capacity, capacity);

        _logger.LogDebug("CpuFramePool created with capacity {Capacity}", _capacity);
    }

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    /// <inheritdoc />
    public async ValueTask<IVideoFrame> RentAsync(
        int width,
        int height,
        PixelFormat format,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);

        int bufferSize = ComputeBufferSize(width, height, format);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        ComputeStrides(width, height, format, out int strideY, out int strideU, out int strideV);

        var frame = new PooledCpuVideoFrame(
            returnToPool: ReturnFrame,
            buffer: buffer,
            width: width,
            height: height,
            strideY: strideY,
            strideU: strideU,
            strideV: strideV,
            format: format,
            pts: TimeSpan.Zero,
            duration: TimeSpan.Zero
        );

        _logger.LogDebug(
            "Frame rented: {Width}x{Height} {Format}, semaphore remaining {Remaining}/{Capacity}",
            width,
            height,
            format,
            _semaphore.CurrentCount,
            _capacity
        );

        return frame;
    }

    /// <inheritdoc />
    public void Return(IVideoFrame frame)
    {
        if (_disposed)
        {
            _logger.LogWarning("Frame returned to disposed pool — frame will not be reused");
            return;
        }

        try
        {
            _semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogWarning("Frame returned to disposed pool — semaphore already disposed");
            return;
        }

        _logger.LogDebug(
            "Frame returned, semaphore remaining {Remaining}/{Capacity}",
            _semaphore.CurrentCount,
            _capacity
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _semaphore.Dispose();

        _logger.LogDebug("CpuFramePool disposed");
    }

    /// <summary>
    /// Callback wired into each <see cref="PooledCpuVideoFrame"/> so that
    /// ref-count-reaching-zero automatically releases the semaphore.
    /// </summary>
    private void ReturnFrame(PooledCpuVideoFrame frame) => Return(frame);

    private static int ComputeBufferSize(int width, int height, PixelFormat format)
    {
        return format switch
        {
            PixelFormat.Bgra32 => width * height * 4,
            PixelFormat.Rgba32 => width * height * 4,
            PixelFormat.Yuv420P => width * height * 3 / 2,
            PixelFormat.Nv12 => width * height * 3 / 2,
            _ => width * height * 4, // safe fallback
        };
    }

    private static void ComputeStrides(
        int width,
        int height,
        PixelFormat format,
        out int strideY,
        out int strideU,
        out int strideV
    )
    {
        switch (format)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
                strideY = width * 4;
                strideU = 0;
                strideV = 0;
                break;

            case PixelFormat.Yuv420P:
                strideY = width;
                strideU = width / 2;
                strideV = width / 2;
                break;

            case PixelFormat.Nv12:
                strideY = width;
                strideU = width; // interleaved UV plane
                strideV = 0;
                break;

            default:
                strideY = width * 4;
                strideU = 0;
                strideV = 0;
                break;
        }
    }
}
