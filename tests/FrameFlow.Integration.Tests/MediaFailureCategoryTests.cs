using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A media file that cannot be played reports why through its <see cref="ErrorCategory"/> and
/// the FFmpeg failure behind it (#755, #757): a missing file, a text file named <c>.mp4</c>, and
/// an MP4 cut off after its first 4 KB. Each is reported once as a playlist item that could not
/// be started, and once as a failed load.
/// </summary>
/// <remarks>
/// The fixtures are written per test from the corpus clip, so nothing is checked in. These tests
/// wait on real playback, which is why they are in this suite (ADR-0072 rule 6). Each wait
/// completes on a signal; the bound only stops a failing run.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MediaFailureCategoryTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private const int TruncatedLength = 4096;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // FFmpeg's codes, as its headers define them.
    private const int NoSuchFile = -2; // AVERROR(ENOENT)
    private const int InvalidData = -1094995529; // AVERROR_INVALIDDATA

    public MediaFailureCategoryTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    public enum Fixture
    {
        Missing,
        TextNamedMp4,
        TruncatedMp4,
    }

    public static TheoryData<Fixture, ErrorCategory, int, MediaFailureStage> Expectations() =>
        new()
        {
            { Fixture.Missing, ErrorCategory.Io, NoSuchFile, MediaFailureStage.Open },
            { Fixture.TextNamedMp4, ErrorCategory.Source, InvalidData, MediaFailureStage.Open },
            { Fixture.TruncatedMp4, ErrorCategory.Source, InvalidData, MediaFailureStage.Open },
        };

    [RequiresFfmpegAndCorpusTheory]
    [MemberData(nameof(Expectations))]
    public async Task AnItemThatCannotBeStarted_ReportsWhy(
        Fixture fixture,
        ErrorCategory category,
        int code,
        MediaFailureStage stage
    )
    {
        using var file = BadFile.Write(fixture, ClipPath());
        var (first, bad, last) = (ClipSource(), MediaSource.FromFile(file.Path), ClipSource());
        await using var run = PlaylistRun.Create([first, bad, last], RepeatMode.Off);

        var lastIsCurrent = run.Transitioned(last);
        await run.PlayAsync();
        await lastIsCurrent.WaitAsync(Bound);

        var error = Assert.Single(run.Errors);
        Assert.Contains("could not be started", error.Message, StringComparison.Ordinal);
        AssertReports(error, category, code, stage);
    }

    [RequiresFfmpegAndCorpusTheory]
    [MemberData(nameof(Expectations))]
    public async Task ALoadThatFails_ReportsWhy(
        Fixture fixture,
        ErrorCategory category,
        int code,
        MediaFailureStage stage
    )
    {
        using var file = BadFile.Write(fixture, ClipPath());
        var (controller, _, _) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var load = await controller.LoadAsync(MediaSource.FromFile(file.Path));

            Assert.False(load.IsSuccess, "The load succeeded.");
            AssertReports(load.Error, category, code, stage);
        }
    }

    private static void AssertReports(
        PlaybackError error,
        ErrorCategory category,
        int code,
        MediaFailureStage stage
    )
    {
        var failure = error.MediaFailure;
        Assert.True(failure is not null, $"No media failure behind: {error}");
        Assert.Equal((category, code, stage), (error.Category, failure.ErrorCode, failure.Stage));
        Assert.Equal(error.Category, failure.Category);
    }

    private static string ClipPath()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        return path;
    }

    private static IMediaSource ClipSource() => MediaSource.FromFile(ClipPath());

    /// <summary>A fixture file under the temp directory, deleted on dispose.</summary>
    private sealed class BadFile : IDisposable
    {
        private BadFile(string path) => Path = path;

        public string Path { get; }

        public static BadFile Write(Fixture fixture, string clip)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"frameflow-failure-{Guid.NewGuid():N}.mp4"
            );
            switch (fixture)
            {
                case Fixture.Missing:
                    break;
                case Fixture.TextNamedMp4:
                    File.WriteAllText(path, "This is a text file, not a video.\n");
                    break;
                case Fixture.TruncatedMp4:
                    var bytes = File.ReadAllBytes(clip);
                    Assert.True(bytes.Length > TruncatedLength, "The clip is too short to truncate.");
                    File.WriteAllBytes(path, bytes[..TruncatedLength]);
                    break;
            }

            return new BadFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
