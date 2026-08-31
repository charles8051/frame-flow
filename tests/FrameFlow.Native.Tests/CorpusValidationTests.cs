using System.Diagnostics;
using FrameFlow.Native;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Validates that generated test corpus files are well-formed and contain the
/// expected streams. These tests require both FFmpeg and generated corpus files.
/// </summary>
public sealed class CorpusValidationTests
{
    // ── File existence ────────────────────────────────────────────────────────

    [RequiresCorpusFact]
    public void Corpus_ContainsExpectedFiles()
    {
        var expectedFiles = new[]
        {
            // Category 1: Basic video formats
            "test-video-h264-yuv420p.mp4",
            "test-video-h265-yuv420p.mp4",
            "test-video-vp9-yuv420p.webm",
            "test-video-av1-yuv420p.mkv",
            // Category 2: Basic audio formats
            "test-audio-aac.m4a",
            "test-audio-mono-aac.m4a",
            "test-audio-mp3.mp3",
            "test-audio-opus.ogg",
            "test-audio-flac.flac",
            // Category 3: Combined A/V
            "test-av-h264-aac.mp4",
            "test-av-h264-aac.mkv",
            // Category 5: Edge cases
            "test-subsecond.mp4",
            "test-audio-only.mp4",
            "test-video-only.mp4",
            "test-fps-24.mp4",
            "test-fps-30.mp4",
            "test-fps-60.mp4",
            "test-1080p-h264-aac.mp4",
            "test-multi-audio.mkv",
            "test-with-subtitles.mkv",
        };

        var dir = TestEnvironment.CorpusDir;
        foreach (var file in expectedFiles)
        {
            Assert.True(
                File.Exists(Path.Combine(dir, file)),
                $"Expected corpus file missing: {file}. Run scripts/generate-test-corpus.cs"
            );
        }
    }

    [RequiresCorpusFact]
    public void Corpus_AllFilesNonEmpty()
    {
        var dir = TestEnvironment.CorpusDir;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var info = new FileInfo(file);
            Assert.True(info.Length > 0, $"Corpus file is empty: {Path.GetFileName(file)}");
        }
    }

    // Fixtures that need a GPL FFmpeg build. The pinned runtime
    // (scripts/runtime-manifest.json) is LGPL, configured --disable-libx264
    // --disable-libx265, and libopenh264 encodes neither 4:4:4 chroma nor
    // B-frames. The generator reports these as UNAVL rather than failing, so a
    // test asserting their presence would fail on a correctly-primed checkout
    // and tell the developer to re-run the script that just declined to make
    // them. These tests verify the files when a GPL build produced them and
    // stand down otherwise.
    private static readonly string[] GplEncoderFiles =
    [
        "test-video-h264-yuv444p.mp4",
        "test-pts-b-frames.mp4",
    ];

    [RequiresCorpusFileFact("test-pts-b-frames.mp4", requiresFfprobe: true)]
    public void BFrameFile_ActuallyContainsBFrames()
    {
        // Size alone proves nothing here. The fixture exists to exercise PTS
        // reordering, and an encoder can produce a valid H.264 file with no B
        // pictures at all — libopenh264 does exactly that when handed "-bf 2".
        // Asserting only that the file is non-empty would let the
        // characteristic the fixture is named for disappear silently.
        var file = TestEnvironment.GetCorpusFile("test-pts-b-frames.mp4")!;
        Assert.True(new FileInfo(file).Length > 0, "Corpus file is empty: test-pts-b-frames.mp4");

        var hasB = RunFfprobe(
            "-v quiet -select_streams v:0 -show_entries stream=has_b_frames -of csv=p=0",
            file
        ).Trim();

        Assert.True(
            int.TryParse(hasB, out var depth) && depth > 0,
            $"test-pts-b-frames.mp4 reports has_b_frames={hasB}. The fixture is named "
                + "for frame reordering; an encoder that dropped B-frames would make every "
                + "test using it assert the opposite of its intent."
        );
    }

    // ── Stream probing (requires ffprobe) ─────────────────────────────────────

    [RequiresCorpusFact]
    public void VideoOnly_HasNoAudioStream()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-only.mp4");
        Assert.NotNull(file);

        var streams = ProbeStreams(file);
        Assert.Contains("video", streams);
        Assert.DoesNotContain("audio", streams);
    }

    [RequiresCorpusFact]
    public void AudioOnly_HasNoVideoStream()
    {
        var file = TestEnvironment.GetCorpusFile("test-audio-only.mp4");
        Assert.NotNull(file);

        var streams = ProbeStreams(file);
        Assert.Contains("audio", streams);
        Assert.DoesNotContain("video", streams);
    }

    [RequiresCorpusFact]
    public void CombinedAV_HasBothStreams()
    {
        var file = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(file);

        var streams = ProbeStreams(file);
        Assert.Contains("video", streams);
        Assert.Contains("audio", streams);
    }

    [RequiresCorpusFact]
    public void MultiAudio_HasMultipleAudioStreams()
    {
        var file = TestEnvironment.GetCorpusFile("test-multi-audio.mkv");
        Assert.NotNull(file);

        var streams = ProbeStreams(file);
        var audioCount = streams.Count(s => s == "audio");
        Assert.True(audioCount >= 2, $"Expected at least 2 audio streams, found {audioCount}");
    }

    [RequiresCorpusFact]
    public void SubtitleFile_HasSubtitleStream()
    {
        var file = TestEnvironment.GetCorpusFile("test-with-subtitles.mkv");
        Assert.NotNull(file);

        var streams = ProbeStreams(file);
        Assert.Contains("subtitle", streams);
    }

    [RequiresCorpusFact]
    public void SubsecondFile_HasShortDuration()
    {
        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        Assert.NotNull(file);

        var duration = ProbeDuration(file);
        if (duration.HasValue)
        {
            Assert.True(duration.Value < 1.5, $"Sub-second file has duration {duration.Value}s");
        }
    }

    [RequiresCorpusFact]
    public void HighRes_Has1080pResolution()
    {
        var file = TestEnvironment.GetCorpusFile("test-1080p-h264-aac.mp4");
        Assert.NotNull(file);

        var resolution = ProbeResolution(file);
        if (resolution.HasValue)
        {
            Assert.Equal(1920, resolution.Value.Width);
            Assert.Equal(1080, resolution.Value.Height);
        }
    }

    // ── Codec identification ──────────────────────────────────────────────────

    [RequiresCorpusFact]
    [Trait("Category", "Codec")]
    public void H264File_UsesH264Codec()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(file);

        var codec = ProbeVideoCodec(file);
        Assert.Equal("h264", codec);
    }

    [RequiresCorpusFact]
    [Trait("Category", "Codec")]
    public void H265File_UsesHevcCodec()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-h265-yuv420p.mp4");
        Assert.NotNull(file);

        var codec = ProbeVideoCodec(file);
        Assert.Equal("hevc", codec);
    }

    [RequiresCorpusFact]
    [Trait("Category", "Codec")]
    public void Vp9File_UsesVp9Codec()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-vp9-yuv420p.webm");
        Assert.NotNull(file);

        var codec = ProbeVideoCodec(file);
        Assert.Equal("vp9", codec);
    }

    [RequiresCorpusFact]
    [Trait("Category", "Codec")]
    public void Av1File_UsesAv1Codec()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-av1-yuv420p.mkv");
        Assert.NotNull(file);

        var codec = ProbeVideoCodec(file);
        Assert.Equal("av1", codec);
    }

    // ── Pixel format verification ─────────────────────────────────────────────

    [RequiresCorpusFact]
    [Trait("Category", "PixelFormat")]
    public void H264_Yuv420p_HasCorrectPixelFormat()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(file);

        var pixFmt = ProbePixelFormat(file);
        Assert.Equal("yuv420p", pixFmt);
    }

    [RequiresCorpusFileFact("test-video-h264-yuv444p.mp4")]
    [Trait("Category", "PixelFormat")]
    public void H264_Yuv444p_HasCorrectPixelFormat()
    {
        // The 4:4:4 chroma is exactly what must be verified here, because
        // libopenh264 accepts the request and writes yuv420p. Skipped rather
        // than passed when the fixture is absent — see GplEncoderFiles.
        var file = TestEnvironment.GetCorpusFile("test-video-h264-yuv444p.mp4")!;

        var pixFmt = ProbePixelFormat(file);
        Assert.Equal("yuv444p", pixFmt);
    }

    // ── Frame rate verification ───────────────────────────────────────────────

    [RequiresCorpusFact]
    [Trait("Category", "FrameRate")]
    public void Fps24_HasCorrectFrameRate()
    {
        AssertFrameRate("test-fps-24.mp4", 24);
    }

    [RequiresCorpusFact]
    [Trait("Category", "FrameRate")]
    public void Fps30_HasCorrectFrameRate()
    {
        AssertFrameRate("test-fps-30.mp4", 30);
    }

    [RequiresCorpusFact]
    [Trait("Category", "FrameRate")]
    public void Fps60_HasCorrectFrameRate()
    {
        AssertFrameRate("test-fps-60.mp4", 60);
    }

    private static void AssertFrameRate(string fileName, double expectedFps)
    {
        var file = TestEnvironment.GetCorpusFile(fileName);
        Assert.NotNull(file);

        var fps = ProbeFrameRate(file);
        if (fps.HasValue)
        {
            Assert.True(
                Math.Abs(fps.Value - expectedFps) < 0.1,
                $"Expected ~{expectedFps}fps, got {fps.Value}fps for {fileName}"
            );
        }
    }

    // ── ffprobe helpers ───────────────────────────────────────────────────────


    private static string RunFfprobe(string args, string filePath)
    {
        var ffprobe = TestEnvironment.FfprobePath;
        if (ffprobe is null)
            return string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"{args} \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return string.Empty;

        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(10_000);

        return output;
    }

    /// <summary>Returns the list of stream types (e.g., "video", "audio", "subtitle").</summary>
    private static List<string> ProbeStreams(string filePath)
    {
        var output = RunFfprobe("-v quiet -show_entries stream=codec_type -of csv=p=0", filePath);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
    }

    private static double? ProbeDuration(string filePath)
    {
        var output = RunFfprobe("-v quiet -show_entries format=duration -of csv=p=0", filePath);

        return double.TryParse(output, out var d) ? d : null;
    }

    private static (int Width, int Height)? ProbeResolution(string filePath)
    {
        var output = RunFfprobe(
            "-v quiet -select_streams v:0 -show_entries stream=width,height -of csv=p=0",
            filePath
        );

        var parts = output.Split(',');
        if (
            parts.Length >= 2
            && int.TryParse(parts[0], out var w)
            && int.TryParse(parts[1], out var h)
        )
            return (w, h);

        return null;
    }

    private static string? ProbeVideoCodec(string filePath)
    {
        var output = RunFfprobe(
            "-v quiet -select_streams v:0 -show_entries stream=codec_name -of csv=p=0",
            filePath
        );

        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    private static string? ProbePixelFormat(string filePath)
    {
        var output = RunFfprobe(
            "-v quiet -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0",
            filePath
        );

        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    private static double? ProbeFrameRate(string filePath)
    {
        var output = RunFfprobe(
            "-v quiet -select_streams v:0 -show_entries stream=r_frame_rate -of csv=p=0",
            filePath
        );

        if (string.IsNullOrWhiteSpace(output))
            return null;

        // Frame rate is returned as a fraction like "24/1" or "30000/1001"
        var parts = output.Trim().Split('/');
        if (
            parts.Length == 2
            && double.TryParse(parts[0], out var num)
            && double.TryParse(parts[1], out var den)
            && den > 0
        )
        {
            return num / den;
        }

        return double.TryParse(output.Trim(), out var fps) ? fps : null;
    }
}
