// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// Process-wide telemetry for the memory held by live <see cref="CpuVideoFrame"/>s: how many
/// there are and how many bytes their buffers occupy.
/// </summary>
/// <remarks>
/// <para>
/// Frames are shared by reference count (ADR-0080), so one buffer can outlive the node that
/// produced it for as long as any holder keeps it. This gauge is the instrument for deciding
/// whether that memory needs a FrameFlow-owned pool (#381). Bytes are the length of each frame's
/// <see cref="CpuVideoFrame.PixelData"/>, which for a buffer rented from
/// <see cref="System.Buffers.MemoryPool{T}.Shared"/> is the whole rented array, so the shared
/// pool's rounding to a power of two is counted.
/// </para>
/// <para>
/// A frame is counted once, from construction to its final release. <c>AddRef</c> shares the
/// buffer and is not counted.
/// </para>
/// <para>
/// Scrape with <c>dotnet-counters</c> via the <c>FrameFlow.Media</c> meter.
/// </para>
/// </remarks>
public static class CpuFrameMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Media", "1.0.0");

    private static long _outstandingBytes;
    private static long _highWaterBytes;
    private static int _outstandingFrames;

    static CpuFrameMetrics()
    {
        Meter.CreateObservableGauge(
            "frameflow.media.cpu_frame_bytes_outstanding",
            () => Interlocked.Read(ref _outstandingBytes),
            unit: "By",
            description: "Bytes held by live CPU video frames, counting each buffer's rented length."
        );
        Meter.CreateObservableGauge(
            "frameflow.media.cpu_frame_bytes_outstanding_max",
            () => Interlocked.Read(ref _highWaterBytes),
            unit: "By",
            description: "Peak bytes held by live CPU video frames since process start."
        );
        Meter.CreateObservableGauge(
            "frameflow.media.cpu_frames_outstanding",
            () => Volatile.Read(ref _outstandingFrames),
            unit: "{frames}",
            description: "Live CPU video frames."
        );
    }

    /// <summary>Bytes held by live CPU video frames right now.</summary>
    public static long OutstandingBytes => Interlocked.Read(ref _outstandingBytes);

    /// <summary>The most bytes live CPU video frames have held at once since process start.</summary>
    public static long HighWaterBytes => Interlocked.Read(ref _highWaterBytes);

    /// <summary>Live CPU video frames right now.</summary>
    public static int OutstandingFrames => Volatile.Read(ref _outstandingFrames);

    internal static void OnFrameCreated(long bytes)
    {
        Interlocked.Increment(ref _outstandingFrames);
        long current = Interlocked.Add(ref _outstandingBytes, bytes);
        long high;
        while (current > (high = Interlocked.Read(ref _highWaterBytes)))
        {
            if (Interlocked.CompareExchange(ref _highWaterBytes, current, high) == high)
                break;
        }
    }

    internal static void OnFrameReleased(long bytes)
    {
        Interlocked.Decrement(ref _outstandingFrames);
        Interlocked.Add(ref _outstandingBytes, -bytes);
    }
}
