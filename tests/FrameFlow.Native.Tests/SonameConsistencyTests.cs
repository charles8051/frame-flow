using System.Text.RegularExpressions;
using FrameFlow.Native.Core;

namespace FrameFlow.Native.Tests;

/// <summary>One versioned FFmpeg library name found in a source file.</summary>
/// <param name="Library">The library stem, e.g. <c>avutil</c>.</param>
/// <param name="Major">The major version in the file name, e.g. <c>59</c>.</param>
/// <param name="Line">1-based line number, so a failure names somewhere to go.</param>
internal readonly record struct SonameReference(string Library, int Major, int Line);

/// <summary>
/// Finds versioned FFmpeg library file names in source text.
/// </summary>
/// <remarks>
/// Pure: text in, references out. No filesystem, so the matching itself is testable
/// against literals rather than against whatever happens to be in the tree.
/// </remarks>
internal static partial class SonameScan
{
    // The three shapes a versioned FFmpeg library takes:
    //   avutil-59.dll        Windows
    //   libavutil.so.59      Linux
    //   libavutil.59.dylib   macOS
    //
    // The optional "lib" is matched but not captured, so the stem is the same on every
    // platform. It has to be in the pattern rather than left to a word boundary: there is
    // no boundary inside "libavutil", so anchoring on the stem alone matches the Windows
    // spelling and silently misses both Unix ones.
    [GeneratedRegex(
        @"\b(?:lib)?(avcodec|avdevice|avfilter|avformat|avutil|swresample|swscale)(?:-(\d+)|\.so\.(\d+)|\.(\d+)\.dylib)\b"
    )]
    private static partial Regex SonamePattern();

    internal static IReadOnlyList<SonameReference> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var found = new List<SonameReference>();

        foreach (Match m in SonamePattern().Matches(text))
        {
            var major = m.Groups[2].Success ? m.Groups[2]
                : m.Groups[3].Success ? m.Groups[3]
                : m.Groups[4];

            found.Add(
                new SonameReference(
                    m.Groups[1].Value,
                    int.Parse(major.Value),
                    LineOf(text, m.Index)
                )
            );
        }

        return found;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }
}

/// <summary>
/// Holds every versioned FFmpeg library name written down in the tree to the major the
/// generated bindings were built against.
/// </summary>
/// <remarks>
/// <para>
/// <c>FFmpegLibraryResolver</c> owns the authoritative name map, and at least eight other
/// places keep their own copy: <c>FrameFlow.MotionClip/NativeBootstrap.cs</c>, four
/// <c>TestEnvironment</c> classes, three <c>FfmpegBootstrapFixture</c> classes, plus
/// <c>TestCorpusFixture</c>, <c>ColdStartEnvironment</c> and <c>scripts/fetch-ffmpeg.cs</c>.
/// They cannot reference each other: they live in different assemblies, and several are
/// private.
/// </para>
/// <para>
/// <b>A partial bump fails quietly, which is why this is worth a test.</b> A helper that
/// still looks for <c>avutil-59.dll</c> after a move to FFmpeg 9 finds nothing, returns
/// null, and <c>RequiresFfmpegFact</c> turns that into <c>Skip</c>. That project's FFmpeg
/// tests stop running and the suite stays green. The ABI gate in <c>FrameFlow.Native</c>
/// does not cover it either: that catches a mismatched library which <i>loaded</i>, not a
/// helper that found none.
/// </para>
/// <para>
/// The majors are read from the binding package rather than written here, so this test
/// moves with the <c>FFmpeg.AutoGen.Abstractions</c> reference and cannot become a second
/// copy of the thing it is checking.
/// </para>
/// </remarks>
public sealed class SonameConsistencyTests
{
    /// <summary>
    /// Directories that actually resolve libraries. Docs are excluded on purpose: they
    /// record history, and a sentence about what a name used to be is not a defect.
    /// </summary>
    private static readonly string[] ScanRoots = ["src", "tests", "scripts", "nuget"];

    /// <summary>
    /// File types that name a library by its on-disk file name.
    /// </summary>
    /// <remarks>
    /// <c>.cs</c> alone was not enough. The FFmpeg 9 move found the pack guard in
    /// <c>FrameFlow.Native.csproj</c>, which lists the five file names twice and fails the
    /// build when they are missing, still on the old major and invisible to this sweep.
    /// <c>.csproj</c>, <c>.json</c> (runtime-manifest) and <c>.sh</c> (the macOS build's
    /// LIBS array) all carry real names and are covered now.
    /// </remarks>
    private static readonly string[] ScanExtensions = ["*.cs", "*.csproj", "*.json", "*.sh"];

    /// <summary>This file, excluded from its own sweep. See <see cref="SourceFiles"/>.</summary>
    private const string ThisFileName = "SonameConsistencyTests.cs";

    /// <summary>
    /// A floor on what a healthy sweep sees, so a pattern that matches nothing fails here
    /// instead of passing over nothing. 26 files carry a soname today, excluding this one.
    /// </summary>
    /// <remarks>
    /// Deliberately slack against that 26, because the number legitimately falls when a
    /// duplicate helper is consolidated or a library is dropped from the fetch, and a floor
    /// that tracked the count closely would turn every such cleanup into an edit here.
    ///
    /// It does not catch a <i>partial</i> pattern break. The first version of this matcher
    /// anchored on the library stem and silently missed both Unix spellings, which would
    /// have left roughly fourteen files still matching and cleared any floor worth setting.
    /// The matcher tests below are what catch that, and they did.
    /// </remarks>
    private const int MinimumFilesExpected = 10;

    private static int ExpectedMajor(string library) =>
        library switch
        {
            "avutil" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvUtil),
            "avcodec" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvCodec),
            "avformat" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvFormat),
            "swscale" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.SwScale),
            "swresample" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.SwResample),

            // Outside RequiredLibraries, because nothing loads them, but inside
            // FfmpegLibrary, because their file names are still written down and a bump has
            // to move those. Read from the binding package like the other five rather than
            // carried here, so they cannot go stale while the sweep keeps passing them.
            "avdevice" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvDevice),
            "avfilter" => FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvFilter),

            _ => throw new ArgumentOutOfRangeException(nameof(library), library, null),
        };

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        foreach (var root in ScanRoots)
        {
            var dir = Path.Combine(TestEnvironment.RepoRoot, root);
            if (!Directory.Exists(dir))
                continue;

            var files = ScanExtensions.SelectMany(ext =>
                Directory.EnumerateFiles(dir, ext, SearchOption.AllDirectories)
            );

            foreach (var file in files)
            {
                // Build output carries copies of the sources and generated files; scanning
                // it would double-report and would depend on what was last built.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                // This file, because its InlineData cases are examples for the matcher and
                // not references to a library. Holding them to the current major would make
                // a version bump edit parser fixtures for no reason, and they are covered
                // by the matcher tests they belong to. Nothing here resolves a library, so
                // the exclusion costs the sweep nothing.
                if (Path.GetFileName(file) == ThisFileName)
                    continue;

                yield return (file, File.ReadAllText(file));
            }
        }
    }

    // --- The sweep ---

    [Fact]
    public void EverySonameInTheTree_MatchesTheBindings()
    {
        var wrong = new List<string>();

        foreach (var (path, text) in SourceFiles())
        {
            foreach (var reference in SonameScan.Find(text))
            {
                var expected = ExpectedMajor(reference.Library);
                if (reference.Major == expected)
                    continue;

                var relative = Path.GetRelativePath(TestEnvironment.RepoRoot, path);
                wrong.Add(
                    $"{relative}:{reference.Line} names {reference.Library}-{reference.Major}, "
                        + $"expected {expected}"
                );
            }
        }

        Assert.True(
            wrong.Count == 0,
            "Versioned FFmpeg library names disagree with the majors "
                + "FFmpeg.AutoGen.Abstractions was generated for. Every copy has to move "
                + "together: a helper left on the old major finds no library, returns null, "
                + "and RequiresFfmpegFact turns that into a skip rather than a failure.\n  "
                + string.Join("\n  ", wrong)
        );
    }

    /// <summary>
    /// The sweep above passes if it finds nothing, so this fails when it finds too little.
    /// </summary>
    [Fact]
    public void TheSweep_ActuallyReadsTheTree()
    {
        var filesWithSonames = SourceFiles().Count(f => SonameScan.Find(f.Text).Count > 0);

        Assert.True(
            filesWithSonames >= MinimumFilesExpected,
            $"Only {filesWithSonames} files carry a versioned FFmpeg library name, and at "
                + $"least {MinimumFilesExpected} are expected. Either the pattern stopped "
                + "matching, or the scan roots moved, in which case the test above is "
                + "passing over nothing."
        );
    }

    // --- The matcher itself ---

    [Theory]
    [InlineData("avutil-59.dll", "avutil", 59)]
    [InlineData("libavutil.so.59", "avutil", 59)]
    [InlineData("libavutil.59.dylib", "avutil", 59)]
    [InlineData("avcodec-61.dll", "avcodec", 61)]
    [InlineData("libswresample.so.5", "swresample", 5)]
    [InlineData("libswscale.8.dylib", "swscale", 8)]
    [InlineData("avfilter-10.dll", "avfilter", 10)]
    public void Find_MatchesEveryPlatformShape(string text, string library, int major)
    {
        var found = Assert.Single(SonameScan.Find(text));

        Assert.Equal(library, found.Library);
        Assert.Equal(major, found.Major);
    }

    [Theory]
    [InlineData("avutil")]
    [InlineData("libavutil.dylib")]
    [InlineData("FFmpeg 7.1")]
    [InlineData("libfoo-59.dll")]
    public void Find_IgnoresTextWithNoVersionedName(string text)
    {
        Assert.Empty(SonameScan.Find(text));
    }

    [Fact]
    public void Find_ReportsTheLine()
    {
        var text = "one\ntwo\navutil-59.dll\n";

        var found = Assert.Single(SonameScan.Find(text));

        Assert.Equal(3, found.Line);
    }

    [Fact]
    public void Find_ReportsEveryOccurrence()
    {
        var text = "avutil-59.dll and libavcodec.so.61 and libswscale.8.dylib";

        var found = SonameScan.Find(text);

        Assert.Equal(3, found.Count);
        Assert.Equal(["avutil", "avcodec", "swscale"], found.Select(f => f.Library));
    }
}
