// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.MotionClip;

/// <summary>
/// A synthetic, hardware-free frame source for the spike: a mostly-static
/// scene (gradient background + a parked white block) punctuated by periodic
/// "motion windows" during which the block sweeps across the frame, generating
/// large frame-to-frame deltas that trip the <see cref="MotionDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the <em>fallback</em> source: the example opens a live camera by
/// default (see <see cref="CameraVideoSource"/>) and only uses this synthetic
/// scene when <c>--synthetic</c> is passed or no camera is available, so the
/// recorder still runs and produces real clips on any machine. The rest of the
/// pipeline (resize/convert → recorder sink) is identical for either source.
/// </para>
/// <para>
/// Frames are produced at <paramref name="fps"/> with a real-time delay so the
/// recorder's pre-roll (frames) and post-roll (frames) windows map to the
/// wall-clock seconds in <see cref="ClipRecorderOptions"/>.
/// </para>
/// </remarks>
internal static class SyntheticSceneSource
{
    /// <summary>
    /// Builds the source node. Cancellation (Ctrl+C / <c>--exit-after</c>)
    /// ends the stream cleanly via end-of-stream rather than a fault.
    /// </summary>
    public static SourceNode<VideoFrameRef> Create(int width, int height, int fps)
    {
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        int motionEveryFrames = fps * 8; // a motion window every ~8 s
        int motionLengthFrames = (int)(fps * 1.5); // each window lasts ~1.5 s
        int frameIndex = 0;

        return new SourceNode<VideoFrameRef>(
            "synthetic-scene",
            async ct =>
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null; // clean EOS on shutdown
                }

                int i = frameIndex++;
                bool moving = (i % motionEveryFrames) < motionLengthFrames;
                CpuVideoFrame frame = RenderFrame(width, height, i, fps, moving);
                return new VideoFrameRef(frame);
            }
        );
    }

    private static CpuVideoFrame RenderFrame(int w, int h, int i, int fps, bool moving)
    {
        int stride = w * 4;
        int size = stride * h;
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(size);
        Span<byte> px = owner.Memory.Span;

        // Static gradient background.
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            byte gy = (byte)(y * 255 / h);
            for (int x = 0; x < w; x++)
            {
                int p = row + (x * 4);
                px[p] = (byte)(x * 255 / w); // B
                px[p + 1] = gy; // G
                px[p + 2] = 64; // R
                px[p + 3] = 0xFF; // A
            }
        }

        // White block: sweeps left→right each second during a motion window,
        // parked at centre otherwise.
        int blockW = w / 6;
        int blockH = h / 6;
        int bx0;
        if (moving)
        {
            int phase = i % fps;
            bx0 = phase * Math.Max(1, w - blockW) / fps;
        }
        else
        {
            bx0 = (w - blockW) / 2;
        }
        int by0 = (h - blockH) / 2;

        for (int y = by0; y < by0 + blockH && y < h; y++)
        {
            int row = y * stride;
            for (int x = bx0; x < bx0 + blockW && x < w; x++)
            {
                int p = row + (x * 4);
                px[p] = 0xFF;
                px[p + 1] = 0xFF;
                px[p + 2] = 0xFF;
                px[p + 3] = 0xFF;
            }
        }

        return new CpuVideoFrame(
            owner,
            w,
            h,
            stride,
            PixelFormat.Bgra32,
            TimeSpan.FromSeconds(i / (double)fps),
            TimeSpan.FromSeconds(1.0 / fps)
        );
    }
}
