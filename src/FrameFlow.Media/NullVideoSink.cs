// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// A no-op <see cref="IVideoSink"/> that immediately disposes every frame
/// presented to it. Useful for headless benchmarks, testing, and pipelines
/// where video output is not needed.
/// </summary>
/// <remarks>
/// Moved from <c>FrameFlow.Playback</c> to <c>FrameFlow.Media</c> during
/// Phase 4 prep (Crossbar ADR-0014). Sinks (Avalonia / SDL) and examples
/// consume it — anchoring it in the substrate-neutral Media assembly keeps
/// them from transitively pulling <c>FrameFlow.Playback</c>.
/// </remarks>
public sealed class NullVideoSink : IVideoSink
{
    /// <inheritdoc />
    public IFramePool FramePool { get; } = new NullFramePool();

    /// <inheritdoc />
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        frame.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// An unbounded frame pool that produces lightweight stub frames.
    /// No semaphore — callers are never blocked.
    /// </summary>
    private sealed class NullFramePool : IFramePool
    {
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public ValueTask<IVideoFrame> RentAsync(
            int width,
            int height,
            PixelFormat format,
            CancellationToken ct
        )
        {
            // Allocate a minimal buffer — just enough for the format.
            int bufferSize = format switch
            {
                PixelFormat.Bgra32 => width * height * 4,
                PixelFormat.Rgba32 => width * height * 4,
                PixelFormat.Yuv420P => width * height * 3 / 2,
                PixelFormat.Nv12 => width * height * 3 / 2,
                _ => width * height * 4,
            };

            int strideY,
                strideU = 0,
                strideV = 0;
            switch (format)
            {
                case PixelFormat.Yuv420P:
                    strideY = width;
                    strideU = width / 2;
                    strideV = width / 2;
                    break;
                case PixelFormat.Nv12:
                    strideY = width;
                    strideU = width;
                    break;
                default:
                    strideY = width * 4;
                    break;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

#pragma warning disable CA2000 // Ownership is intentionally transferred to the returned IVideoFrame instance.
            // Uses the internal pooled-frame implementation — same-assembly
            // access works because PooledCpuVideoFrame lives in this
            // namespace post-move (previously was internal to FrameFlow.Playback).
            IVideoFrame frame = new PooledCpuVideoFrame(
                returnToPool: null,
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
#pragma warning restore CA2000

            return new ValueTask<IVideoFrame>(frame);
        }

        public void Return(IVideoFrame frame)
        {
            // No-op — null pool does not track frames.
        }

        public void Dispose()
        {
            // No-op — nothing to clean up.
        }
    }
}
