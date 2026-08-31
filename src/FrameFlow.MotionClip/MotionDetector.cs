// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.MotionClip;

/// <summary>
/// Frame-delta motion detector (ADR-0052 §4). A stateful tap that classifies
/// each frame as "moved" or "still" by downsampling to a fixed small grayscale
/// buffer and counting pixels that changed beyond a threshold against the
/// previous frame.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately dependency-free — no YOLO, no SIMD in this spike. At 320×180
/// grayscale the per-frame cost is sub-millisecond, so it runs inline
/// (synchronous) in the recorder's sink node and keeps pace with the source.
/// </para>
/// <para>
/// Expects BGRA32 packed input (the pipeline converts to BGRA32 before the
/// detector). Luma is an integer approximation of Rec.601:
/// <c>(R·77 + G·150 + B·29) &gt;&gt; 8</c>.
/// </para>
/// </remarks>
internal sealed class MotionDetector
{
    private const int DownWidth = 320;
    private const int DownHeight = 180;
    private const int DownPixels = DownWidth * DownHeight;

    private readonly byte[] _current = new byte[DownPixels];
    private readonly byte[] _previous = new byte[DownPixels];
    private readonly int _pixelThreshold;
    private readonly double _motionThreshold;
    private readonly int _warmupFrames;
    private readonly MotionSectorMask _mask;
    private bool _hasPrevious;
    private int _warmupRemaining;

    /// <param name="pixelThreshold">Per-pixel luma delta to count as "changed" (ADR default 25).</param>
    /// <param name="motionThreshold">Changed-pixel ratio above which a frame is "moved" (ADR default 0.02).</param>
    /// <param name="warmupFrames">
    /// Frames after a reset during which the detector keeps re-seeding its
    /// reference without firing motion. Most webcams need 0.5–1 s for
    /// auto-exposure / AWB / sensor gain to settle after open, and frame N+1
    /// versus frame N during that window is "different from itself" — not
    /// real motion. Default 30 (≈1 s at 30 fps).
    /// </param>
    /// <param name="sectorMask">
    /// Optional numpad-grid mask that limits which downsampled pixels count
    /// toward the changed-pixel ratio. <see langword="null"/> = all 9 sectors
    /// armed (no masking). The ratio denominator becomes
    /// <see cref="MotionSectorMask.ActivePixelCount"/> when a mask is in
    /// effect, so the user-facing <c>--sensitivity</c> threshold has
    /// consistent semantics regardless of how many sectors are armed.
    /// </param>
    public MotionDetector(
        int pixelThreshold = 25,
        double motionThreshold = 0.02,
        int warmupFrames = 30,
        MotionSectorMask? sectorMask = null
    )
    {
        _pixelThreshold = pixelThreshold;
        _motionThreshold = motionThreshold;
        _warmupFrames = Math.Max(0, warmupFrames);
        // Default to an all-armed mask sized to the downsampled buffer so
        // the inner loop always reads from the same shape — no nullable
        // check per pixel.
        _mask = sectorMask ?? new MotionSectorMask(DownWidth, DownHeight, armedSectors: null);
    }

    /// <summary>The active sector mask. Exposed so the UI can render an overlay matching what the detector is actually using.</summary>
    public MotionSectorMask SectorMask => _mask;

    /// <summary>The changed-pixel ratio computed for the most recent frame.</summary>
    public double LastMotionRatio { get; private set; }

    /// <summary>
    /// <see langword="true"/> while the warmup window is still draining — the
    /// detector is updating its reference frame but not firing motion. Exposed
    /// for status reporting; transitions to <see langword="false"/> after
    /// <c>warmupFrames</c> frames following a <see cref="ResetReference"/>.
    /// </summary>
    public bool IsWarmingUp => _warmupRemaining > 0;

    /// <summary>
    /// Drops the stored reference frame and arms a warmup window so the next
    /// <c>warmupFrames</c> frames seed the reference WITHOUT triggering motion.
    /// Call when capture (re)starts — e.g. a camera reconnect — so neither the
    /// stale prior-session reference nor the camera's auto-exposure / AWB
    /// settling fires a spurious "motion" event in the first second.
    /// </summary>
    public void ResetReference()
    {
        _hasPrevious = false;
        _warmupRemaining = _warmupFrames;
        LastMotionRatio = 0;
    }

    /// <summary>
    /// Classifies a frame. Returns <see langword="true"/> when the changed-pixel
    /// ratio versus the previous frame exceeds the motion threshold. The first
    /// frame seeds the reference and returns <see langword="false"/>; during
    /// the warmup window the reference continues to track the latest input
    /// (so the camera's startup ramp doesn't bake into the baseline).
    /// </summary>
    public bool Process(IVideoFrame frame)
    {
        var cpu = frame.AsCpu();
        if (cpu is null)
            return false;

        Downsample(cpu.Value);

        // Seed the reference on the very first frame, AND keep re-seeding
        // during warmup. While warmup is active, the reference tracks the
        // most recent frame so when the window closes the detector is
        // already calibrated against a stabilized scene.
        if (!_hasPrevious || _warmupRemaining > 0)
        {
            Array.Copy(_current, _previous, DownPixels);
            _hasPrevious = true;
            if (_warmupRemaining > 0)
                _warmupRemaining--;
            LastMotionRatio = 0;
            return false;
        }

        int changed = 0;
        ReadOnlySpan<bool> mask = _mask.PixelMask;
        for (int i = 0; i < DownPixels; i++)
        {
            if (mask[i] && Math.Abs(_current[i] - _previous[i]) > _pixelThreshold)
                changed++;
        }

        // Ratio is over the ARMED area, not the full frame. When all 9 sectors
        // are armed, this is identical to the historic full-frame ratio.
        LastMotionRatio = (double)changed / _mask.ActivePixelCount;
        Array.Copy(_current, _previous, DownPixels);
        return LastMotionRatio > _motionThreshold;
    }

    private void Downsample(CpuFrameData cpu)
    {
        ReadOnlySpan<byte> src = cpu.PlaneY.Span;
        int sw = cpu.Width;
        int sh = cpu.Height;
        int stride = cpu.StrideY;

        for (int y = 0; y < DownHeight; y++)
        {
            int sy = y * sh / DownHeight;
            int srcRow = sy * stride;
            int dstRow = y * DownWidth;
            for (int x = 0; x < DownWidth; x++)
            {
                int sx = x * sw / DownWidth;
                int p = srcRow + (sx * 4);
                byte b = src[p];
                byte g = src[p + 1];
                byte r = src[p + 2];
                _current[dstRow + x] = (byte)(((r * 77) + (g * 150) + (b * 29)) >> 8);
            }
        }
    }
}
