// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Avalonia.Windows.Core;

/// <summary>A ring's size in pixels.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
internal readonly record struct RingSize(int Width, int Height);

/// <summary>
/// The part of a frame a converter reads, in coded pixels. The whole frame unless a filled view
/// crops it (#766, #767).
/// </summary>
/// <param name="X">Left of the part.</param>
/// <param name="Y">Top of the part.</param>
/// <param name="Width">Width of the part.</param>
/// <param name="Height">Height of the part.</param>
internal readonly record struct SourceRect(double X, double Y, double Width, double Height)
{
    /// <summary>The whole <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    public static SourceRect Whole(int width, int height) => new(0, 0, width, height);

    /// <summary>Whether the part has no area.</summary>
    public bool IsEmpty => !(Width > 0) || !(Height > 0);
}

/// <summary>
/// The size of the shader-mode converter's ring for a view (#767). Pure: sizes in, a size out.
/// </summary>
/// <remarks>
/// <para>
/// The ring holds the part of the frame the view shows. It is the size that part is drawn at in
/// physical pixels, so the compositor draws it 1:1, but never larger than the part's own coded
/// size on either axis: at or above that, the ring holds the part at its coded size and the
/// compositor scales it up, as it did before the ring followed the view.
/// </para>
/// <para>
/// <see cref="RingSettle"/> decides when a change of that size is applied.
/// </para>
/// </remarks>
internal static class ViewSizedRingPolicy
{
    /// <summary>The step a growing ring's size is rounded up to, so a drag rebuilds it every 64 px, not every frame.</summary>
    public const int Step = 64;

    /// <summary>
    /// The largest ring for <paramref name="source"/>: its coded size, rounded to whole pixels,
    /// at least one pixel and at most the <paramref name="frameWidth"/> x
    /// <paramref name="frameHeight"/> frame on each axis.
    /// </summary>
    public static RingSize Cap(SourceRect source, int frameWidth, int frameHeight)
    {
        if (source.IsEmpty)
            return new RingSize(frameWidth, frameHeight);
        return new RingSize(
            Math.Clamp((int)Math.Round(source.Width), 1, Math.Max(frameWidth, 1)),
            Math.Clamp((int)Math.Round(source.Height), 1, Math.Max(frameHeight, 1)));
    }

    /// <summary>
    /// The ring the view wants: the physical size <paramref name="drawWidth"/> x
    /// <paramref name="drawHeight"/> the part is drawn at, held to <paramref name="cap"/> on each
    /// axis. <see langword="null"/> when nothing is drawn, so the ring keeps its size.
    /// </summary>
    public static RingSize? Wanted(int drawWidth, int drawHeight, RingSize cap)
    {
        if (drawWidth <= 0 || drawHeight <= 0)
            return null;
        return new RingSize(Math.Min(drawWidth, cap.Width), Math.Min(drawHeight, cap.Height));
    }

    /// <summary>
    /// The size a <paramref name="current"/> ring grows to for <paramref name="wanted"/>: each axis
    /// that grows is rounded up to <see cref="Step"/>, and each other axis keeps its size, so a
    /// shrink on it still waits for the hold. Both are held to <paramref name="cap"/>.
    /// </summary>
    public static RingSize Grown(RingSize wanted, RingSize current, RingSize cap) =>
        new(Axis(wanted.Width, current.Width, cap.Width), Axis(wanted.Height, current.Height, cap.Height));

    private static int Axis(int wanted, int current, int cap) =>
        Math.Min(wanted > current ? RoundUp(wanted) : current, cap);

    private static int RoundUp(int value) => (value + Step - 1) / Step * Step;
}

/// <summary>
/// When a view-driven change of the ring's size is applied (#767). Each change builds a new ring
/// and imports it into the compositor again, so a drag or a layout animation must not rebuild it
/// on every frame. Pure: the caller supplies the timestamps from the shell's clock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// A ring smaller than the wanted size on either axis is too small to draw 1:1, and the compositor
/// would scale it up. It grows at once: each axis that grows is rounded up to
/// <see cref="ViewSizedRingPolicy.Step"/>, so a drag that keeps growing rebuilds it once per step,
/// and the other axis keeps its size.
/// </description></item>
/// <item><description>
/// A ring at least the wanted size takes the exact size once that size has been wanted, unchanged,
/// for the hold time. A shrink waits for the drag to stop, and a ring rounded up while growing
/// settles on the size it is drawn at.
/// </description></item>
/// </list>
/// </remarks>
/// <param name="Pending">The size wanted but not yet applied, or null.</param>
/// <param name="PendingSince">The timestamp <see cref="Pending"/> was first wanted at.</param>
internal readonly record struct RingSettle(RingSize? Pending, long PendingSince)
{
    /// <summary>
    /// Folds in the size wanted at <paramref name="now"/> for a ring of <paramref name="current"/>
    /// size. Returns the size to resize the ring to now, or <see langword="null"/> to keep it.
    /// </summary>
    /// <param name="current">The ring's size.</param>
    /// <param name="wanted">The size the view wants, from <see cref="ViewSizedRingPolicy.Wanted"/>; null keeps the ring.</param>
    /// <param name="cap">The largest ring, from <see cref="ViewSizedRingPolicy.Cap"/>.</param>
    /// <param name="now">The shell's clock, in ticks of its own.</param>
    /// <param name="holdTicks">How long a smaller size has to be wanted before the ring takes it.</param>
    public (RingSettle Next, RingSize? Resize) Advance(RingSize current, RingSize? wanted, RingSize cap, long now, long holdTicks)
    {
        if (wanted is not { } size || size == current)
            return (default, null);

        if (size.Width > current.Width || size.Height > current.Height)
            return (default, ViewSizedRingPolicy.Grown(size, current, cap));

        if (Pending != size)
            return (new RingSettle(size, now), null);
        if (now - PendingSince >= holdTicks)
            return (default, size);
        return (this, null);
    }
}

/// <summary>
/// What the shader pass reads for one ring: the source part as texture coordinates, and how much
/// of the frame each ring pixel covers (#767). Laid out as the D3D11 shader's constant buffer reads
/// it after <see cref="YuvLevels"/>. Pure.
/// </summary>
/// <remarks>
/// Where a ring pixel covers more than one texel of a plane, the shader averages every texel under
/// its footprint, weighted by how much of each the footprint covers: an area (box) filter. One
/// bilinear sample reads a 2x2 block, so past 2:1 it skips texels and fine detail aliases. A mip
/// chain cannot stand in: an NV12 or P010 texture has one mip level, so it would need a
/// full-size RGB copy first, which is the memory the view-sized ring saves. The filter reads about
/// one texel per source texel in total, whatever the ratio, up to the shader's 64 texels per axis.
/// </remarks>
/// <param name="U">Left of the source part, 0 to 1 across the frame.</param>
/// <param name="V">Top of the source part, 0 to 1 down the frame.</param>
/// <param name="UWidth">Width of the source part, as a fraction of the frame's.</param>
/// <param name="VHeight">Height of the source part, as a fraction of the frame's.</param>
/// <param name="FootprintU">Width of the frame one ring pixel covers, as a fraction of the frame's.</param>
/// <param name="FootprintV">Height of the frame one ring pixel covers.</param>
/// <param name="Unused0">Padding to the constant buffer's 16-byte rows.</param>
/// <param name="Unused1">Padding.</param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ShaderSampling(
    float U,
    float V,
    float UWidth,
    float VHeight,
    float FootprintU,
    float FootprintV,
    float Unused0 = 0,
    float Unused1 = 0)
{
    /// <summary>
    /// The sampling that draws <paramref name="source"/> of a <paramref name="frameWidth"/> x
    /// <paramref name="frameHeight"/> frame into a <paramref name="ring"/>-sized target.
    /// </summary>
    public static ShaderSampling For(SourceRect source, int frameWidth, int frameHeight, RingSize ring)
    {
        if (source.IsEmpty)
            source = SourceRect.Whole(frameWidth, frameHeight);

        float uWidth = (float)(source.Width / frameWidth);
        float vHeight = (float)(source.Height / frameHeight);
        return new ShaderSampling(
            (float)(source.X / frameWidth),
            (float)(source.Y / frameHeight),
            uWidth,
            vHeight,
            uWidth / ring.Width,
            vHeight / ring.Height);
    }
}
