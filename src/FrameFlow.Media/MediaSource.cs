// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO;

namespace FrameFlow.Media;

public sealed record MediaSource : IMediaSource
{
    public required string DisplayName { get; init; }

    /// <inheritdoc />
    public Uri? Uri { get; init; }

    /// <inheritdoc />
    public string? FilePath { get; init; }

    /// <inheritdoc />
    public bool IsSeekable { get; init; } = true;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? DemuxerOptions { get; init; }

    /// <inheritdoc />
    public string? InputFormat { get; init; }

    public static MediaSource FromFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new MediaSource
        {
            DisplayName = Path.GetFileName(fullPath),
            Uri = new Uri(fullPath),
            FilePath = fullPath,
            IsSeekable = true,
        };
    }

    public static MediaSource FromUri(Uri uri)
    {
        return new MediaSource
        {
            DisplayName = uri.ToString(),
            Uri = uri,
            FilePath = uri.IsFile ? uri.LocalPath : null,
            IsSeekable = uri.IsFile,
        };
    }

    /// <summary>
    /// Opens a single image as a clip of <paramref name="dwell"/> holding one frame, so it has a
    /// duration and a playlist moves on after it.
    /// </summary>
    /// <param name="path">The image file. It is not read here; the demuxer opens it on load.</param>
    /// <param name="dwell">How long the image is the current item. Must be positive.</param>
    /// <remarks>
    /// <para>
    /// Probed, a single image opens on a <c>*_pipe</c> demuxer, which reports no duration whatever
    /// options it is given, so the item ends the moment its one frame is presented. Naming
    /// <c>image2</c> and giving it a <c>framerate</c> is what makes the same file a clip of a known
    /// length; the pacer's end-of-content hold is what then keeps the frame on screen for it.
    /// </para>
    /// <para>
    /// <b>This does not look at the extension, and does not refuse any format.</b> Whether a file is
    /// one image is a fact about the content, and the caller generally knows it from something
    /// better than the path: a declared content type, a manifest, a database column. A
    /// content-addressed store has no extension to read at all. Applied to a format that can be
    /// animated, such as <c>.gif</c> or <c>.webp</c>, <c>image2</c> yields the first frame and
    /// reports it as the whole file, so decide before you call this rather than after.
    /// </para>
    /// <para>
    /// <c>pattern_type=none</c> because <c>image2</c> otherwise reads the path as a printf sequence
    /// pattern, so a file actually named <c>photo%03d.png</c> fails to open with "could find no file
    /// with path ... and index in the range 0-4" while sitting on disk. The path here names one
    /// existing file, so the pattern handling has nothing to offer and one filename in it to break.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dwell"/> is under a millisecond, or over <see cref="MaximumStillDwell"/>.
    /// Both ends are refused rather than silently opened, because the failure they produce is a
    /// still with no duration, which is the case this factory exists to avoid.
    /// </exception>
    public static MediaSource FromStill(string path, TimeSpan dwell)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dwell, MinimumStillDwell, nameof(dwell));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dwell, MaximumStillDwell, nameof(dwell));

        return FromFile(path) with
        {
            InputFormat = "image2",
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framerate"] = FrameRateFor(dwell),
                ["pattern_type"] = "none",
            },
        };
    }

    /// <summary>
    /// The shortest dwell <see cref="FromStill"/> accepts. Below a millisecond there is nothing
    /// left to hold.
    /// </summary>
    public static readonly TimeSpan MinimumStillDwell = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The longest dwell <see cref="FromStill"/> accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a measured limit, not a chosen one. FFmpeg does not store the <c>framerate</c>
    /// option as written: it evaluates the rational and re-derives one, and past a point the
    /// duration that comes back bears no relation to the dwell asked for. Measured against the
    /// bundled FFmpeg, opening a still at <c>1/3600</c> reports a duration of <b>zero</b>, at
    /// <c>1/3601</c> one second, and at <c>1/4000</c> four hundred seconds. None of them
    /// announce themselves: the demuxer succeeds and the clip is simply the wrong length, which
    /// is the failure <see cref="FromStill"/> exists to prevent rather than relocate.
    /// </para>
    /// <para>
    /// The cliff sits near an hour, and below it there is a second sensitivity to how large the
    /// reduced terms are, so the accepted range stops well short of both. Ten minutes is
    /// comfortably inside what measured correct at every rational shape tried, and is already
    /// far longer than a still is held for. Widening it later is not a breaking change; the
    /// evidence for a wider bound is a longer ladder in <c>StillImageDwellTests</c>.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MaximumStillDwell = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The <c>framerate</c> that makes one frame last <paramref name="dwell"/>, as a reduced
    /// rational of two integers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One frame over <c>dwell</c> seconds is <c>1/dwell</c> frames per second, and FFmpeg reads
    /// the option as a rational rather than a number. Writing the seconds into the denominator
    /// gives <c>1/7.5</c> for a fractional dwell, which is a decimal inside a rational and
    /// carries the invariant-culture question with it. Milliseconds avoid both: <c>1000/ms</c> is
    /// the same value in integer terms, and reducing it keeps them small. A 7.5 second dwell is
    /// <c>2/15</c>.
    /// </para>
    /// <para>
    /// Milliseconds rather than ticks, which would be exact. The terms matter as well as the
    /// value: FFmpeg re-derives the rational it was given, and a dwell written in ticks reduces
    /// to terms in the billions for any value that is not a round number of them, which is one
    /// of the two ways the realized duration goes wrong (see <see cref="MaximumStillDwell"/>).
    /// A millisecond is finer than a dwell is ever specified to.
    /// </para>
    /// </remarks>
    private static string FrameRateFor(TimeSpan dwell)
    {
        const long MillisecondsPerSecond = 1000;

        var numerator = MillisecondsPerSecond;
        var denominator = (long)Math.Round(dwell.TotalMilliseconds, MidpointRounding.AwayFromZero);

        var divisor = GreatestCommonDivisor(numerator, denominator);
        return $"{numerator / divisor}/{denominator / divisor}";
    }

    private static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
