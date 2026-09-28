// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Media.Diagnostics;

/// <summary>Where the library copies or converts a frame's pixels (#435).</summary>
public enum FrameCopySite
{
    /// <summary>The decoder downloads a hardware frame to system memory.</summary>
    DecoderDownload,

    /// <summary>The decoder converts a frame to BGRA with <c>sws_scale</c>, after a download or from software decode.</summary>
    DecoderConvert,

    /// <summary>A GPU frame is read back to system memory, as <c>VideoOperators.ToCpu</c> does.</summary>
    ReadbackDownload,

    /// <summary>A read-back frame is converted to BGRA.</summary>
    ReadbackConvert,

    /// <summary>A converter operator (<c>ConvertPixelFormat</c>, <c>Resize</c>, <c>ResizeAndConvert</c>) writes a new frame.</summary>
    OperatorConvert,

    /// <summary>An encoder converts a frame to the codec's pixel format.</summary>
    EncoderConvert,

    /// <summary>A presenter copies a CPU frame into what it displays: a bitmap, a texture, or a staging texture.</summary>
    PresenterUpload,

    /// <summary>A presenter copies a GPU frame between textures, or across devices.</summary>
    PresenterGpuCopy,

    /// <summary>A presenter converts a GPU frame to BGRA with a shader.</summary>
    PresenterGpuConvert,
}

/// <summary>
/// Counts, per <see cref="FrameCopySite"/>, the frames the library has copied or converted since
/// the process started (#435). A path that keeps frames on the GPU shows no downloads.
/// </summary>
/// <remarks>
/// <para>
/// Process-wide, like <see cref="CpuFrameMetrics"/>: every player, pass and presenter in the
/// process adds to the same counts, and they never reset. Take a <see cref="Snapshot"/> before
/// and after the work in question, and read the difference with
/// <see cref="FrameCopyCounts.Since"/>.
/// </para>
/// <para>
/// Only the library records; a count is one frame through one site, whatever its size. Scrape
/// the same counts with <c>dotnet-counters</c> as <c>frameflow.media.frame_copies</c> on the
/// <c>FrameFlow.Media</c> meter, tagged by <c>site</c>.
/// </para>
/// </remarks>
public static class FrameCopyMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Media.Copies", "1.0.0");
    private static readonly FrameCopySite[] Sites = Enum.GetValues<FrameCopySite>();
    private static readonly long[] Counts = new long[Sites.Length];

    static FrameCopyMetrics()
    {
        Meter.CreateObservableCounter(
            "frameflow.media.frame_copies",
            () => Sites.Select(site => new Measurement<long>(
                Count(site),
                new KeyValuePair<string, object?>("site", site.ToString()))),
            unit: "{frames}",
            description: "Frames the library copied or converted, by where it happened."
        );
    }

    /// <summary>Frames copied or converted at <paramref name="site"/> since the process started.</summary>
    public static long Count(FrameCopySite site) => Interlocked.Read(ref Counts[(int)site]);

    /// <summary>Every site's count, read together.</summary>
    public static FrameCopyCounts Snapshot()
    {
        var counts = new long[Sites.Length];
        for (int i = 0; i < counts.Length; i++)
            counts[i] = Interlocked.Read(ref Counts[i]);
        return new FrameCopyCounts(counts);
    }

    /// <summary>Counts one frame through <paramref name="site"/>. Called by the site, once the copy has happened.</summary>
    internal static void Record(FrameCopySite site) => Interlocked.Increment(ref Counts[(int)site]);
}

/// <summary>The per-site counts of <see cref="FrameCopyMetrics"/> at one moment.</summary>
public sealed class FrameCopyCounts
{
    private readonly long[] _counts;

    internal FrameCopyCounts(long[] counts) => _counts = counts;

    /// <summary>The count at <paramref name="site"/>.</summary>
    public long this[FrameCopySite site] => _counts[(int)site];

    /// <summary>The copies made between <paramref name="earlier"/> and this snapshot, per site.</summary>
    public FrameCopyCounts Since(FrameCopyCounts earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        var difference = new long[_counts.Length];
        for (int i = 0; i < difference.Length; i++)
            difference[i] = _counts[i] - earlier._counts[i];
        return new FrameCopyCounts(difference);
    }

    /// <summary>The sum over every site.</summary>
    public long Total => _counts.Sum();

    /// <inheritdoc />
    public override string ToString() =>
        string.Join(", ", Enum.GetValues<FrameCopySite>().Select(site => $"{site}={this[site]}"));
}
