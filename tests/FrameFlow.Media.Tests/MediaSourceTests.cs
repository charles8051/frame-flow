namespace FrameFlow.Media.Tests;

public sealed class MediaSourceTests
{
    // -----------------------------------------------------------------------
    // FromFile — happy path
    // -----------------------------------------------------------------------

    [Fact]
    public void FromFile_SetsFilePath_ToFullPath()
    {
        // Use a relative path to exercise the full-path expansion.
        // Path.GetFullPath normalises it regardless of whether the file exists.
        var relativePath = "video.mp4";
        var expected = Path.GetFullPath(relativePath);

        var source = MediaSource.FromFile(relativePath);

        Assert.Equal(expected, source.FilePath);
    }

    [Fact]
    public void FromFile_SetsDisplayName_ToFileName()
    {
        var source = MediaSource.FromFile("some/path/clip.mkv");
        Assert.Equal("clip.mkv", source.DisplayName);
    }

    [Fact]
    public void FromFile_SetsIsSeekable_True()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.True(source.IsSeekable);
    }

    [Fact]
    public void FromFile_SetsUri_ToFileUri()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.NotNull(source.Uri);
        Assert.True(source.Uri!.IsFile);
    }

    [Fact]
    public void FromFile_Uri_PointsToSamePathAsFilePath()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.Equal(source.FilePath, source.Uri!.LocalPath);
    }

    // -----------------------------------------------------------------------
    // FromUri — remote URI is not seekable
    // -----------------------------------------------------------------------

    [Fact]
    public void FromUri_RemoteUri_IsNotSeekable()
    {
        var uri = new Uri("https://example.com/stream.m3u8");
        var source = MediaSource.FromUri(uri);
        Assert.False(source.IsSeekable);
    }

    [Fact]
    public void FromUri_RemoteUri_SetsDisplayName_ToUriString()
    {
        var uri = new Uri("https://example.com/stream.m3u8");
        var source = MediaSource.FromUri(uri);
        Assert.Equal(uri.ToString(), source.DisplayName);
    }

    [Fact]
    public void FromUri_RemoteUri_StoresUri()
    {
        var uri = new Uri("rtsp://192.168.1.1/live");
        var source = MediaSource.FromUri(uri);
        Assert.Equal(uri, source.Uri);
    }

    [Fact]
    public void FromUri_RemoteUri_FilePathIsNull()
    {
        var uri = new Uri("https://cdn.example.com/video.mp4");
        var source = MediaSource.FromUri(uri);
        Assert.Null(source.FilePath);
    }

    // -----------------------------------------------------------------------
    // FromUri — file URI is seekable
    // -----------------------------------------------------------------------

    [Fact]
    public void FromUri_FileUri_IsSeekable()
    {
        var uri = new Uri(Path.GetFullPath("video.mp4"));
        var source = MediaSource.FromUri(uri);
        Assert.True(source.IsSeekable);
    }

    [Fact]
    public void FromUri_FileUri_SetsFilePath()
    {
        var fullPath = Path.GetFullPath("video.mp4");
        var uri = new Uri(fullPath);
        var source = MediaSource.FromUri(uri);
        Assert.Equal(uri.LocalPath, source.FilePath);
    }

    // -----------------------------------------------------------------------
    // Interface contract
    // -----------------------------------------------------------------------

    [Fact]
    public void ImplementsIMediaSource()
    {
        var source = MediaSource.FromFile("video.mp4");
        Assert.IsAssignableFrom<IMediaSource>(source);
    }

    [Fact]
    public void IMediaSource_DisplayName_MatchesProperty()
    {
        IMediaSource source = MediaSource.FromFile("clip.mp4");
        Assert.Equal("clip.mp4", source.DisplayName);
    }

    [Fact]
    public void IMediaSource_IsSeekable_MatchesProperty()
    {
        IMediaSource source = MediaSource.FromFile("clip.mp4");
        Assert.True(source.IsSeekable);
    }

    // -----------------------------------------------------------------------
    // Record constructor — direct instantiation
    // -----------------------------------------------------------------------

    [Fact]
    public void RecordConstructor_StoresAllProperties()
    {
        var uri = new Uri("https://example.com/test.mp4");
        var source = new MediaSource
        {
            DisplayName = "My Video",
            Uri = uri,
            FilePath = "/files/test.mp4",
            IsSeekable = false,
        };

        Assert.Equal("My Video", source.DisplayName);
        Assert.Equal(uri, source.Uri);
        Assert.Equal("/files/test.mp4", source.FilePath);
        Assert.False(source.IsSeekable);
    }

    [Fact]
    public void RecordConstructor_DefaultIsSeekable_IsTrue()
    {
        var source = new MediaSource { DisplayName = "Test" };
        Assert.True(source.IsSeekable);
    }

    [Fact]
    public void RecordConstructor_DefaultUri_IsNull()
    {
        var source = new MediaSource { DisplayName = "Test" };
        Assert.Null(source.Uri);
    }

    [Fact]
    public void RecordConstructor_DefaultFilePath_IsNull()
    {
        var source = new MediaSource { DisplayName = "Test" };
        Assert.Null(source.FilePath);
    }

    // -----------------------------------------------------------------------
    // FromStill — the image2 recipe, in one place (#304)
    // -----------------------------------------------------------------------

    [Fact]
    public void FromStill_NamesImage2_RatherThanLettingTheFileBeProbed()
    {
        // Probed, a single image opens on a *_pipe demuxer that reports no duration, so the item
        // ends as soon as its one frame is presented. Naming the demuxer is the whole point.
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromSeconds(5));
        Assert.Equal("image2", source.InputFormat);
    }

    [Fact]
    public void FromStill_KeepsWhatFromFileSets()
    {
        var expected = MediaSource.FromFile("some/path/slide.png");
        var source = MediaSource.FromStill("some/path/slide.png", TimeSpan.FromSeconds(5));

        Assert.Equal(expected.DisplayName, source.DisplayName);
        Assert.Equal(expected.FilePath, source.FilePath);
        Assert.Equal(expected.Uri, source.Uri);
    }

    [Theory]
    // A whole number of seconds reduces to 1/n, which is what a hand-written recipe produces.
    [InlineData(5, "1/5")]
    [InlineData(1, "1/1")]
    [InlineData(13, "1/13")]
    [InlineData(120, "1/120")]
    public void FromStill_WritesAWholeSecondDwell_AsOneOverTheSeconds(int seconds, string expected)
    {
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected, source.DemuxerOptions?["framerate"]);
    }

    [Fact]
    public void FromStill_WritesAFractionalDwell_AsExactIntegerTerms()
    {
        // The reason this factory takes a TimeSpan rather than a formatted string. Writing the
        // seconds into the denominator gives "1/7.5" — a decimal inside a rational, which then
        // has to be formatted invariantly or a comma separator breaks it. Ticks give 2/15.
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromSeconds(7.5));
        Assert.Equal("2/15", source.DemuxerOptions?["framerate"]);
    }

    [Fact]
    public void FromStill_WritesASubSecondDwell_AsAFrameRateAboveOne()
    {
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromMilliseconds(250));
        Assert.Equal("4/1", source.DemuxerOptions?["framerate"]);
    }

    [Fact]
    public void FromStill_QuantisesToTheNearestMillisecond()
    {
        // Ticks would be exact, and exactness is not the thing that matters here: FFmpeg
        // re-derives the rational it is handed, and terms in the billions are one of the two ways
        // the realized duration stops matching the dwell. A millisecond is finer than a dwell is
        // ever specified to.
        // 1.2345678s rounds to 1235ms, and 1000/1235 reduces to 200/247. The integration ladder
        // confirms that opens a clip of exactly 1.235s.
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromTicks(12_345_678));
        Assert.Equal("200/247", source.DemuxerOptions?["framerate"]);
    }

    [Fact]
    public void FromStill_KeepsTheTermsSmall()
    {
        // The guard behind the quantisation. Every accepted dwell reduces to terms well inside
        // what FFmpeg re-derives faithfully; measured, the realized duration starts drifting as
        // the terms grow. See MediaSource.MaximumStillDwell.
        foreach (var dwell in new[]
        {
            TimeSpan.FromTicks(12_345_678),
            TimeSpan.FromSeconds(7.5),
            TimeSpan.FromMilliseconds(1),
            MediaSource.MaximumStillDwell,
            MediaSource.MaximumStillDwell - TimeSpan.FromTicks(1),
        })
        {
            var rate = MediaSource.FromStill("slide.png", dwell).DemuxerOptions?["framerate"];
            Assert.NotNull(rate);

            var terms = rate.Split('/');
            Assert.True(long.Parse(terms[0]) <= 1000, $"numerator too large in {rate}");
            Assert.True(long.Parse(terms[1]) <= 600_000, $"denominator too large in {rate}");
        }
    }

    [Fact]
    public void FromStill_RefusesADwellPastTheMeasuredCeiling()
    {
        // Past it the demuxer succeeds and the clip is simply the wrong length: 1/3600 reports a
        // duration of zero. A silent wrong answer is what this factory exists to prevent, so the
        // range it cannot deliver is refused rather than handed over.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaSource.FromStill("slide.png", MediaSource.MaximumStillDwell + TimeSpan.FromSeconds(1))
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaSource.FromStill("slide.png", TimeSpan.FromHours(1))
        );
    }

    [Fact]
    public void FromStill_AcceptsTheCeilingItself()
    {
        var source = MediaSource.FromStill("slide.png", MediaSource.MaximumStillDwell);
        Assert.Equal("1/1800", source.DemuxerOptions?["framerate"]);
    }

    [Fact]
    public void FromStill_QuantisationIsPartOfTheContract()
    {
        // Stated on the parameter, not left to be discovered. Everything inside a millisecond
        // lands on the same clip, and the caller is told so rather than finding out from a
        // duration that does not match what they asked for.
        var asked = TimeSpan.FromTicks(10_001); // 1.0001 ms
        var rounded = MediaSource.FromStill("slide.png", asked);
        var whole = MediaSource.FromStill("slide.png", TimeSpan.FromMilliseconds(1));

        Assert.Equal(
            whole.DemuxerOptions?["framerate"],
            rounded.DemuxerOptions?["framerate"]
        );
    }

    [Theory]
    // Rounds to nearest, away from zero at the midpoint, like the documented contract says.
    [InlineData(14_000, "1000/1")]  // 1.4 ms -> 1 ms
    [InlineData(15_000, "500/1")]   // 1.5 ms -> 2 ms
    [InlineData(16_000, "500/1")]   // 1.6 ms -> 2 ms
    public void FromStill_RoundsToNearestMillisecond(int ticks, string expected)
    {
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromTicks(ticks));
        Assert.Equal(expected, source.DemuxerOptions?["framerate"]);
    }

    [Fact]
    public void FromStill_RefusesADwellUnderAMillisecond()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaSource.FromStill("slide.png", TimeSpan.FromTicks(1))
        );
    }

    [Fact]
    public void FromStill_NeverWritesADecimalPoint()
    {
        // Guards the whole class of culture bugs at once: an integer rational has no separator to
        // get wrong, so no call site has to remember to format invariantly.
        foreach (var dwell in new[]
        {
            TimeSpan.FromSeconds(7.5),
            TimeSpan.FromSeconds(0.3),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(99.999),
        })
        {
            var rate = MediaSource.FromStill("slide.png", dwell).DemuxerOptions?["framerate"];
            Assert.NotNull(rate);
            Assert.DoesNotContain('.', rate);
            Assert.DoesNotContain(',', rate);
        }
    }

    [Fact]
    public void FromStill_SetsPatternTypeNone()
    {
        // image2 otherwise reads the path as a printf sequence pattern, so a file actually named
        // photo%03d.png fails to open while sitting on disk.
        var source = MediaSource.FromStill("photo%03d.png", TimeSpan.FromSeconds(5));
        Assert.Equal("none", source.DemuxerOptions?["pattern_type"]);
    }

    [Fact]
    public void FromStill_ComparesOptionKeysOrdinally()
    {
        // Comparison is FFmpeg's, not the dictionary's, so a set with two keys differing only in
        // case is ambiguous. IMediaSource.DemuxerOptions documents Ordinal; honour it here.
        var source = MediaSource.FromStill("slide.png", TimeSpan.FromSeconds(5));
        Assert.NotNull(source.DemuxerOptions);
        Assert.False(source.DemuxerOptions.ContainsKey("FrameRate"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromStill_RefusesANonPositiveDwell(int seconds)
    {
        // A still with no duration is exactly what this factory exists to avoid producing.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaSource.FromStill("slide.png", TimeSpan.FromSeconds(seconds))
        );
    }

    [Fact]
    public void FromStill_TakesNoPositionOnTheExtension()
    {
        // Deliberate: whether a file is one image is a fact about the content, and a
        // content-addressed store has no extension to read. The caller decides, not the factory.
        var animated = MediaSource.FromStill("maybe-animated.webp", TimeSpan.FromSeconds(5));
        var extensionless = MediaSource.FromStill("0bfe12ab.bin", TimeSpan.FromSeconds(5));

        Assert.Equal("image2", animated.InputFormat);
        Assert.Equal("image2", extensionless.InputFormat);
    }
}
