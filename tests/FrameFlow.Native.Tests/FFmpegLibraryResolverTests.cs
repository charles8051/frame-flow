using FrameFlow.Native;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Unit tests for <see cref="FFmpegLibraryResolver"/> — platform file name mapping and
/// candidate path generation. These tests do not require FFmpeg binaries.
/// </summary>
public sealed class FFmpegLibraryResolverTests
{
    // -----------------------------------------------------------------------
    // RequiredLibraries
    // -----------------------------------------------------------------------

    [Fact]
    public void RequiredLibraries_ContainsAllFiveLibraries()
    {
        var expected = new[] { "avutil", "swresample", "swscale", "avcodec", "avformat" };
        Assert.Equal(expected.Length, FFmpegLibraryResolver.RequiredLibraries.Length);
        foreach (var lib in expected)
            Assert.Contains(lib, FFmpegLibraryResolver.RequiredLibraries);
    }

    [Fact]
    public void RequiredLibraries_AvutilIsFirst()
    {
        // avutil must be loaded first because avcodec and avformat depend on it.
        Assert.Equal("avutil", FFmpegLibraryResolver.RequiredLibraries[0]);
    }

    // -----------------------------------------------------------------------
    // PlatformFileName — platform-conditional tests
    // -----------------------------------------------------------------------

    [Fact]
    public void PlatformFileName_OnWindows_AvutilHasDllSuffix()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var name = FFmpegLibraryResolver.PlatformFileName("avutil");
        Assert.EndsWith(".dll", name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avutil-59", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlatformFileName_OnWindows_AvformatHasDllSuffix()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var name = FFmpegLibraryResolver.PlatformFileName("avformat");
        Assert.EndsWith(".dll", name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avformat-61", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlatformFileName_OnLinux_AvutilHasSoSuffix()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var name = FFmpegLibraryResolver.PlatformFileName("avutil");
        Assert.StartsWith("lib", name, StringComparison.Ordinal);
        Assert.Contains(".so.", name, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformFileName_OnMacOs_AvutilHasDylibSuffix()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var name = FFmpegLibraryResolver.PlatformFileName("avutil");
        Assert.StartsWith("lib", name, StringComparison.Ordinal);
        Assert.EndsWith(".dylib", name, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // PlatformFileName — unknown library fallback
    // -----------------------------------------------------------------------

    [Fact]
    public void PlatformFileName_UnknownLibrary_DoesNotThrow()
    {
        // Should return something sensible even for unknown library names.
        var name = FFmpegLibraryResolver.PlatformFileName("unknownlib");
        Assert.NotNull(name);
        Assert.NotEmpty(name);
        Assert.Contains("unknownlib", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlatformFileName_AllRequiredLibraries_ReturnNonEmptyString()
    {
        foreach (var lib in FFmpegLibraryResolver.RequiredLibraries)
        {
            var name = FFmpegLibraryResolver.PlatformFileName(lib);
            Assert.NotNull(name);
            Assert.NotEmpty(name);
        }
    }

    // -----------------------------------------------------------------------
    // CandidatePaths
    // -----------------------------------------------------------------------

    [Fact]
    public void CandidatePaths_WithSearchDirectory_FirstCandidateIsFullPath()
    {
        var searchDir = OperatingSystem.IsWindows() ? @"C:\ffmpeg" : "/opt/ffmpeg";
        var candidates = FFmpegLibraryResolver.CandidatePaths("avutil", searchDir).ToList();

        Assert.True(candidates.Count >= 1);
        Assert.StartsWith(searchDir, candidates[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidatePaths_WithNullSearchDirectory_AllCandidatesAreBareNames()
    {
        var candidates = FFmpegLibraryResolver.CandidatePaths("avutil", null).ToList();

        // Without a search directory, all candidates should be bare names (no directory prefix).
        foreach (var c in candidates)
            Assert.DoesNotContain(Path.DirectorySeparatorChar, c);
    }

    [Fact]
    public void CandidatePaths_WithSearchDirectory_ReturnsAtLeastTwoCandidates()
    {
        var candidates = FFmpegLibraryResolver.CandidatePaths("avutil", "/some/dir").ToList();

        Assert.True(
            candidates.Count >= 2,
            "Expected at least a full-path candidate and a bare-name fallback."
        );
    }

    [Fact]
    public void CandidatePaths_WithoutSearchDirectory_ReturnsAtLeastOneCandidate()
    {
        var candidates = FFmpegLibraryResolver.CandidatePaths("avutil", null).ToList();
        Assert.True(candidates.Count >= 1);
    }

    [Fact]
    public void CandidatePaths_EmptySearchDirectory_BehavesLikeNull()
    {
        var withEmpty = FFmpegLibraryResolver.CandidatePaths("avutil", "").ToList();
        var withNull = FFmpegLibraryResolver.CandidatePaths("avutil", null).ToList();

        Assert.Equal(withNull.Count, withEmpty.Count);
    }
}
