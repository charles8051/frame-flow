using FrameFlow.Media;

namespace FrameFlow.Examples.Multicast.Shared;

/// <summary>The looks the third pane can give the video.</summary>
public enum ColorFilter
{
    None,
    Grayscale,
    Sepia,
}

/// <summary>The example's own color filter; FrameFlow has none.</summary>
public static class ColorFilters
{
    /// <summary>
    /// <paramref name="frame"/> with <paramref name="filter"/> applied: a new BGRA frame at the same
    /// timestamp, or, for <see cref="ColorFilter.None"/>, another reference to
    /// <paramref name="frame"/>. The caller owns what it returns.
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="frame"/> is not BGRA.</exception>
    public static IVideoFrame Apply(IVideoFrame frame, ColorFilter filter)
    {
        if (filter == ColorFilter.None)
            return frame.AddRef();
        if (frame.Format != PixelFormat.Bgra32)
            throw new NotSupportedException($"The filter reads Bgra32 frames, not {frame.Format}.");

        return CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            frame.Width,
            frame.Height,
            frame.Pts,
            frame.Duration,
            frame.Timeline,
            (Source: frame.ToCpu(), Filter: filter),
            static (target, state) => Fill(state.Source, target, state.Filter),
            sampleAspectRatio: frame.SampleAspectRatio,
            rotation: frame.Rotation
        );
    }

    private static void Fill(CpuFrameData source, CpuVideoFramePlanes target, ColorFilter filter)
    {
        int rowBytes = target.Width * 4;
        for (int y = 0; y < target.Height; y++)
        {
            var from = source.PlaneY.Span.Slice(y * source.StrideY, rowBytes);
            var to = target.Y.Slice(y * target.StrideY, rowBytes);
            for (int x = 0; x < rowBytes; x += 4)
            {
                int b = from[x], g = from[x + 1], r = from[x + 2];
                if (filter == ColorFilter.Grayscale)
                {
                    to[x] = to[x + 1] = to[x + 2] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                }
                else
                {
                    to[x] = (byte)Math.Min(255, (r * 272 + g * 534 + b * 131) / 1000);
                    to[x + 1] = (byte)Math.Min(255, (r * 349 + g * 686 + b * 168) / 1000);
                    to[x + 2] = (byte)Math.Min(255, (r * 393 + g * 769 + b * 189) / 1000);
                }
                to[x + 3] = from[x + 3];
            }
        }
    }
}
