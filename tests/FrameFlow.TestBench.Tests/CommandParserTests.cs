using FrameFlow.Media;
using FrameFlow.TestBench;
using Xunit;

namespace FrameFlow.TestBench.Tests;

/// <summary>
/// Tests for the bench's command surface.
/// </summary>
/// <remarks>
/// The parser is pure — no pipeline, no I/O, no clock — which is what lets the whole
/// command surface be covered without a media file or an audio device. Everything the
/// bench does that needs a running pipeline is deliberately outside it.
/// </remarks>
public sealed class CommandParserTests
{
    private static BenchCommand Parse(string line)
    {
        var result = CommandParser.Parse(line);
        Assert.Null(result.Error);
        return Assert.IsAssignableFrom<BenchCommand>(result.Command);
    }

    private static string Error(string line)
    {
        var result = CommandParser.Parse(line);
        Assert.True(result.IsError, $"'{line}' was expected to fail and did not.");
        return result.Error!;
    }

    // ── The verbs ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("play")]
    [InlineData("PLAY")]
    [InlineData("  play  ")]
    public void VerbsAreCaseInsensitiveAndTrimmed(string line) =>
        Assert.IsType<BenchCommand.Play>(Parse(line));

    [Fact]
    public void LoadTakesAPath() =>
        Assert.Equal("clip.mp4", Assert.IsType<BenchCommand.Load>(Parse("load clip.mp4")).Path);

    [Fact]
    public void LoadKeepsSpacesInAPath()
    {
        // Splitting the line into words would make the bench unable to open half the
        // files on a desktop.
        var load = Assert.IsType<BenchCommand.Load>(Parse(@"load C:\clips\my take 3.mp4"));
        Assert.Equal(@"C:\clips\my take 3.mp4", load.Path);
    }

    [Fact]
    public void LoadStripsSurroundingQuotes() =>
        Assert.Equal(
            @"C:\clips\take.mp4",
            Assert.IsType<BenchCommand.Load>(Parse(@"load ""C:\clips\take.mp4""")).Path
        );

    [Fact]
    public void LoadWithoutAPathFails() => Assert.Contains("needs a path", Error("load"));

    // ── load's source options (#278) ────────────────────────────────────
    //
    // MediaSource.FromFile leaves InputFormat and DemuxerOptions unset, so before these the
    // bench could only open a source the way the probe read it. A still image probes to a
    // *_pipe demuxer and reports no duration, which is not the same source an operator asking
    // for image2 at a framerate means.

    [Fact]
    public void LoadTakesADemuxerName()
    {
        var load = Assert.IsType<BenchCommand.Load>(Parse("load --format image2 slide.png"));

        Assert.Equal("image2", load.Format);
        Assert.Equal("slide.png", load.Path);
    }

    [Fact]
    public void LoadTakesDemuxerOptions()
    {
        var load = Assert.IsType<BenchCommand.Load>(
            Parse("load --format image2 --option framerate=1/10 --option loop=1 slide.png")
        );

        Assert.Equal("image2", load.Format);
        Assert.Equal("slide.png", load.Path);
        Assert.NotNull(load.Options);
        Assert.Equal("1/10", load.Options!["framerate"]);
        Assert.Equal("1", load.Options["loop"]);
    }

    [Fact]
    public void AnOptionValueKeepsItsOwnEqualsSigns()
    {
        // A value is everything after the first '=': FFmpeg option values contain them
        // (headers, key lists), and splitting on every one would truncate those.
        var load = Assert.IsType<BenchCommand.Load>(
            Parse("load --option headers=X-Key=abc clip.mp4")
        );

        Assert.Equal("X-Key=abc", load.Options!["headers"]);
    }

    [Fact]
    public void FlagsComeBeforeThePathSoAPathWithSpacesStillWorks()
    {
        var load = Assert.IsType<BenchCommand.Load>(
            Parse(@"load --format image2 C:\pictures\slide with spaces.png")
        );

        Assert.Equal("image2", load.Format);
        Assert.Equal(@"C:\pictures\slide with spaces.png", load.Path);
    }

    [Fact]
    public void APathAfterNoFlagsIsUnchanged()
    {
        // The ordinary case keeps its old shape exactly, including a path that is the whole
        // remainder of the line.
        var load = Assert.IsType<BenchCommand.Load>(Parse(@"load C:\clips\my take 3.mp4"));

        Assert.Null(load.Format);
        Assert.Null(load.Options);
        Assert.Equal(@"C:\clips\my take 3.mp4", load.Path);
    }

    [Fact]
    public void LoadWithFlagsAndNoPathFails() =>
        Assert.Contains("needs a path", Error("load --format image2"));

    [Fact]
    public void AnOptionWithoutAValueFails() =>
        Assert.Contains("key=value", Error("load --option framerate slide.png"));

    [Fact]
    public void AnOptionWithAnEmptyKeyFails() =>
        Assert.Contains("key=value", Error("load --option =1/10 slide.png"));

    [Fact]
    public void AnUnknownFlagFails() =>
        Assert.Contains("--frobnicate", Error("load --frobnicate 1 slide.png"));

    [Fact]
    public void FormatWithoutANameFails() =>
        Assert.Contains("demuxer name", Error("load --format"));

    // ── load round-trips through the formatter ──────────────────────────
    //
    // The transcript is the artifact worth pasting into an issue, so every line the bench
    // prints has to be a line it would accept back. These are the values where "print the
    // field verbatim" silently produces a different command.

    [Theory]
    [InlineData("load clip.mp4")]
    [InlineData("load --format image2 slide.png")]
    [InlineData("load --format image2 --option framerate=1/10 slide.png")]
    [InlineData("load --option headers=X-Key=abc clip.mp4")]
    // A path with spaces: bare, because the path is the trailing run and needs no quoting.
    [InlineData(@"load C:\clips\my take 3.mp4")]
    // A path that would be read as a flag, one that would be cut at a comment, and an option
    // value with a space in it. Each has to come back quoted or it reparses as something else.
    [InlineData(@"load ""--input.png""")]
    [InlineData(@"load ""C:\clips	ake #3.mp4""")]
    [InlineData(@"load --option ""headers=X-Key: a b"" clip.mp4")]
    public void LoadSurvivesTheRoundTrip(string line)
    {
        var first = Assert.IsType<BenchCommand.Load>(Parse(line));
        var rendered = CommandFormatter.Describe(first);
        var second = Assert.IsType<BenchCommand.Load>(Parse(rendered));

        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Format, second.Format);
        Assert.Equal(first.Options?.Count ?? 0, second.Options?.Count ?? 0);

        if (first.Options is not null)
        {
            foreach (var (key, value) in first.Options)
                Assert.Equal(value, second.Options![key]);
        }
    }

    [Fact]
    public void AQuotedOptionValueKeepsItsSpaces()
    {
        var load = Assert.IsType<BenchCommand.Load>(
            Parse(@"load --option ""headers=X-Key: a b"" clip.mp4")
        );

        Assert.Equal("X-Key: a b", load.Options!["headers"]);
        Assert.Equal("clip.mp4", load.Path);
    }

    [Theory]
    [InlineData("off", RepeatMode.Off)]
    [InlineData("one", RepeatMode.One)]
    [InlineData("all", RepeatMode.All)]
    public void RepeatTakesAMode(string argument, RepeatMode expected) =>
        Assert.Equal(
            expected,
            Assert.IsType<BenchCommand.Repeat>(Parse($"repeat {argument}")).Mode
        );

    [Fact]
    public void RepeatRejectsAnUnknownMode() => Assert.Contains("'off'", Error("repeat sometimes"));

    [Theory]
    [InlineData("diag", false)]
    [InlineData("diag --all", true)]
    public void DiagTakesAnOptionalAllFlag(string line, bool all) =>
        Assert.Equal(all, Assert.IsType<BenchCommand.Diag>(Parse(line)).All);

    [Fact]
    public void MuteTakesOnOrOff()
    {
        Assert.True(Assert.IsType<BenchCommand.Mute>(Parse("mute on")).On);
        Assert.False(Assert.IsType<BenchCommand.Mute>(Parse("mute off")).On);
    }

    [Fact]
    public void UnknownVerbsFail() => Assert.Contains("unknown command", Error("frobnicate"));

    [Fact]
    public void ArgumentsToArgumentlessVerbsFail() =>
        Assert.Contains("takes no arguments", Error("play fast"));

    // ── Durations ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("250ms", 250)]
    [InlineData("1.5s", 1_500)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("0s", 0)]
    public void DurationsTakeANumberAndAUnit(string text, double expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), CommandParser.ParseDuration(text));

    [Fact]
    public void MillisecondsAreNotReadAsMinutes()
    {
        // "ms" ends with "s" and starts with "m". Matching the shorter suffix first
        // would make every millisecond value wrong by a factor of 60,000.
        Assert.Equal(TimeSpan.FromMilliseconds(500), CommandParser.ParseDuration("500ms"));
        Assert.Equal(TimeSpan.FromMinutes(500), CommandParser.ParseDuration("500m"));
    }

    [Theory]
    [InlineData("5")] // no unit
    [InlineData("s")] // no number
    [InlineData("-1s")] // negative
    [InlineData("abcms")]
    [InlineData("")]
    public void DurationsWithoutANumberAndUnitFail(string text) =>
        Assert.Null(CommandParser.ParseDuration(text));

    [Fact]
    public void ABareNumberIsNotADuration()
    {
        // Accepting it would mean picking seconds or milliseconds by convention, and a
        // script that meant the other one is off by a thousand without saying anything.
        Assert.Contains("needs a duration", Error("seek 5"));
    }

    [Fact]
    public void SeekTakesADuration() =>
        Assert.Equal(
            TimeSpan.FromSeconds(90),
            Assert.IsType<BenchCommand.Seek>(Parse("seek 90s")).Position
        );

    // ── Volume ──────────────────────────────────────────────────────────

    [Fact]
    public void VolumeAboveUnityIsAccepted()
    {
        // IVolumeControl accepts gain above 1.0 and documents that it may distort.
        // Rejecting it here would make the bench unable to reproduce a report about
        // exactly that.
        Assert.Equal(1.5f, Assert.IsType<BenchCommand.Volume>(Parse("volume 1.5")).Level);
    }

    [Fact]
    public void NegativeVolumeFails() => Assert.Contains("at or above 0", Error("volume -1"));

    // ── Comments and blank lines ────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("   # indented")]
    public void BlankAndCommentLinesAreSkippedRatherThanRejected(string line)
    {
        var result = CommandParser.Parse(line);
        Assert.False(result.IsError);
        Assert.Null(result.Command);
    }

    [Fact]
    public void ATrailingCommentIsStripped() =>
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            Assert.IsType<BenchCommand.Seek>(Parse("seek 1s   # settle first")).Position
        );

    [Fact]
    public void AHashInsideQuotesIsPartOfThePath()
    {
        // A naive cut at the first '#' truncates this to a path that does not exist.
        var load = Assert.IsType<BenchCommand.Load>(Parse(@"load ""C:\clips\take #3.mp4"""));
        Assert.Equal(@"C:\clips\take #3.mp4", load.Path);
    }

    // ── Whole scripts ───────────────────────────────────────────────────

    [Fact]
    public void AScriptParsesEveryLineBeforeRunningAny()
    {
        string[] lines = ["load clip.mp4", "play", "wait 2s", "diag", "quit"];

        Assert.True(CommandParser.TryParseScript(lines, out var commands, out var errors));
        Assert.Empty(errors);
        Assert.Collection(
            commands,
            c => Assert.IsType<BenchCommand.Load>(c),
            c => Assert.IsType<BenchCommand.Play>(c),
            c => Assert.IsType<BenchCommand.Wait>(c),
            c => Assert.IsType<BenchCommand.Diag>(c),
            c => Assert.IsType<BenchCommand.Quit>(c)
        );
    }

    [Fact]
    public void EveryBadLineIsReported()
    {
        // One round trip rather than one per typo. Reporting only the first would make
        // a five-typo script a five-run fix.
        string[] lines = ["load clip.mp4", "frobnicate", "play", "seek soon"];

        Assert.False(CommandParser.TryParseScript(lines, out _, out var errors));
        Assert.Equal(2, errors.Count);
        Assert.Contains("line 2", errors[0]);
        Assert.Contains("line 4", errors[1]);
    }

    [Fact]
    public void ErrorsCarryTheLineNumberFromTheFileNotTheCommandIndex()
    {
        // Blank and comment lines still count. An error naming the wrong line is worse
        // than one naming no line at all.
        string[] lines = ["# setup", "", "load clip.mp4", "", "frobnicate"];

        Assert.False(CommandParser.TryParseScript(lines, out _, out var errors));
        Assert.Contains("line 5", Assert.Single(errors));
    }
}
