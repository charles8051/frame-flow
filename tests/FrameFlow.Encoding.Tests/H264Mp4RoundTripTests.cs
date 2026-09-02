using System.Diagnostics;
using System.Globalization;
using FrameFlow.Decoding;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Round-trip smoke tests for the H.264 → MP4 encoder terminal (ADR-0040):
/// synthetic BGRA frames → <see cref="Mp4VideoWriter"/> → a temp <c>.mp4</c>,
/// then verified by reopening through the demux path and (when the bundled
/// tool is present) by <c>ffprobe</c>.
/// </summary>
public sealed class H264Mp4RoundTripTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 30;
    private const int FrameCount = 30;

    public H264Mp4RoundTripTests(FfmpegBootstrapFixture _) { }

    private static H264EncoderOptions DefaultOptions() =>
        new()
        {
            FrameRateNumerator = Fps,
            FrameRateDenominator = 1,
            BitRate = 1_000_000,
            GopSize = Fps,
        };

    private static async Task WriteSyntheticClipAsync(string path, int frameCount)
    {
        await using var writer = Mp4VideoWriter.Create(path, DefaultOptions());
        for (int i = 0; i < frameCount; i++)
            await writer.WriteAsync(SyntheticFrames.CreateBgraRef(Width, Height, i, Fps));
        await writer.CompleteAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Direct write path (ADR-0052 "hand frames, await completion")
    // ─────────────────────────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task Encode_WritesValidH264Mp4_ReopenableByDemux()
    {
        var path = TempMp4Path();
        try
        {
            await WriteSyntheticClipAsync(path, FrameCount);

            Assert.True(File.Exists(path), "Output MP4 should exist.");
            Assert.True(new FileInfo(path).Length > 0, "Output MP4 should be non-empty.");

            var factory = new DemuxSessionFactory();
            await using var session = await factory.OpenAsync(MediaSource.FromFile(path));

            Assert.NotEmpty(session.MediaInfo.VideoStreams);
            Assert.Empty(session.MediaInfo.AudioStreams);

            var video = session.MediaInfo.VideoStreams[0];
            Assert.Equal("h264", video.CodecName);
            Assert.Equal(Width, video.Width);
            Assert.Equal(Height, video.Height);

            // Duration must reflect the real CFR timeline (FrameCount / Fps ≈ 1 s).
            // A wide-but-bounded window catches the "made up PTS" failure mode
            // (which collapses 30 frames into a few milliseconds) without being
            // brittle about container rounding.
            var expected = TimeSpan.FromSeconds((double)FrameCount / Fps);
            Assert.InRange(
                session.MediaInfo.Duration,
                expected - TimeSpan.FromMilliseconds(400),
                expected + TimeSpan.FromMilliseconds(400)
            );
        }
        finally
        {
            TryDelete(path);
        }
    }

    [RequiresFfmpegFact]
    public async Task Encode_ProducesReadableVideoPackets_FirstIsKeyframe()
    {
        var path = TempMp4Path();
        try
        {
            await WriteSyntheticClipAsync(path, FrameCount);

            var factory = new DemuxSessionFactory();
            await using var session = await factory.OpenAsync(MediaSource.FromFile(path));
            int videoIndex = session.MediaInfo.VideoStreams[0].StreamIndex;

            int videoPackets = 0;
            DemuxPacket? firstVideo = null;
            DemuxPacket? packet;
            while ((packet = await session.ReadPacketAsync()) is not null)
            {
                if (packet.StreamIndex != videoIndex)
                    continue;
                firstVideo ??= packet;
                videoPackets++;
            }

            Assert.True(videoPackets > 0, "Expected at least one video packet.");
            Assert.NotNull(firstVideo);
            Assert.True(firstVideo!.IsKeyFrame, "The first video packet should be a keyframe.");
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Graph composition path (ADR-0052 pipeline shape, ADR-0045 termination)
    // ─────────────────────────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task Encode_ViaGraphSinkNode_ProducesValidMp4()
    {
        var path = TempMp4Path();
        try
        {
            await using var writer = Mp4VideoWriter.Create(path, DefaultOptions());

            var graph = new FrameFlow.Graph.Graph();
            int produced = 0;
            var source = new SourceNode<VideoFrameRef>(
                "synthetic-frames",
                _ =>
                {
                    if (produced >= FrameCount)
                        return ValueTask.FromResult<VideoFrameRef?>(null);
                    var frame = SyntheticFrames.CreateBgraRef(Width, Height, produced, Fps);
                    produced++;
                    return ValueTask.FromResult<VideoFrameRef?>(frame);
                }
            );

            graph.Pipeline(source).To(writer.AsSinkNode());
            await graph.RunAsync();
            // The substrate has no per-sink EOS hook; finalize explicitly.
            await writer.CompleteAsync();

            var factory = new DemuxSessionFactory();
            await using var session = await factory.OpenAsync(MediaSource.FromFile(path));

            Assert.NotEmpty(session.MediaInfo.VideoStreams);
            var video = session.MediaInfo.VideoStreams[0];
            Assert.Equal("h264", video.CodecName);
            Assert.Equal(Width, video.Width);
            Assert.Equal(Height, video.Height);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Independent verification via the bundled ffprobe
    // ─────────────────────────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task Encode_ProducesExpectedFrameCount_PerFfprobe()
    {
        var ffprobe = TestEnvironment.FfprobePath;
        if (ffprobe is null)
            return; // Bundled ffprobe absent; the demux tests already prove validity.

        var path = TempMp4Path();
        try
        {
            await WriteSyntheticClipAsync(path, FrameCount);

            (string codec, int width, int height, int frames) = await ProbeVideoStreamAsync(
                ffprobe,
                path
            );

            Assert.Equal("h264", codec);
            Assert.Equal(Width, width);
            Assert.Equal(Height, height);
            Assert.Equal(FrameCount, frames);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Lifecycle edge cases
    // ─────────────────────────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task CompleteAsync_IsIdempotent()
    {
        var path = TempMp4Path();
        try
        {
            await using var writer = Mp4VideoWriter.Create(path, DefaultOptions());
            for (int i = 0; i < 5; i++)
                await writer.WriteAsync(SyntheticFrames.CreateBgraRef(Width, Height, i, Fps));
            await writer.CompleteAsync();
            await writer.CompleteAsync(); // second call must be a no-op

            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [RequiresFfmpegFact]
    public async Task Dispose_WithoutComplete_StillFinalizesValidMp4()
    {
        var path = TempMp4Path();
        try
        {
            // Intentionally skip CompleteAsync; DisposeAsync should finalize.
            await using (var writer = Mp4VideoWriter.Create(path, DefaultOptions()))
            {
                for (int i = 0; i < 10; i++)
                    await writer.WriteAsync(SyntheticFrames.CreateBgraRef(Width, Height, i, Fps));
            }

            var factory = new DemuxSessionFactory();
            await using var session = await factory.OpenAsync(MediaSource.FromFile(path));
            Assert.NotEmpty(session.MediaInfo.VideoStreams);
            Assert.Equal("h264", session.MediaInfo.VideoStreams[0].CodecName);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string TempMp4Path() =>
        Path.Combine(Path.GetTempPath(), $"frameflow-enc-{Guid.NewGuid():N}.mp4");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static async Task<(string Codec, int Width, int Height, int Frames)> ProbeVideoStreamAsync(
        string ffprobe,
        string path
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_name,width,height,nb_frames");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(path);

        // A staged ffprobe under runtimes/{rid}/native/ has to find its sibling FFmpeg
        // libraries. macOS binaries are patched to @loader_path by fetch-ffmpeg.cs and
        // Windows resolves DLLs beside the exe, but ELF binaries get neither: without
        // this the loader fails before ffprobe prints anything, which reads as "empty
        // output" and says nothing about why.
        var probeDir = Path.GetDirectoryName(ffprobe);
        if (!string.IsNullOrEmpty(probeDir))
        {
            foreach (var variable in new[] { "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH" })
            {
                var existing = Environment.GetEnvironmentVariable(variable);
                psi.Environment[variable] = string.IsNullOrEmpty(existing)
                    ? probeDir
                    : probeDir + Path.PathSeparator + existing;
            }
        }

        using var proc = Process.Start(psi)!;

        // Both pipes are drained before waiting. Reading one and leaving the other
        // buffered deadlocks as soon as the unread pipe fills.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await proc.WaitForExitAsync();

        // Example: "h264,320,240,30"
        var parts = stdout.Trim().Split(',', StringSplitOptions.TrimEntries);
        Assert.True(
            parts.Length >= 4,
            $"ffprobe '{ffprobe}' exited {proc.ExitCode} and did not describe the stream.\n"
                + $"  stdout: '{stdout.Trim()}'\n"
                + $"  stderr: '{stderr.Trim()}'"
        );
        return (
            parts[0],
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture)
        );
    }
}
