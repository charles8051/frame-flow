using FrameFlow.Decoding;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass bounded to a range (#483), checked against the frames the demuxer reports. The
/// decisions are <c>PassRangeCoreTests</c>'s; these run them over a file.
/// </summary>
public sealed class PassRangeTests
{
    /// <summary>A keyframe a second, at 0, 1 and 2 s, and 72 frames at 24 fps.</summary>
    private const string Clip = PassDecodeDiscardTests.Clip;

    /// <summary>
    /// The frames delivered are exactly those in the range, so the first is the first at or after
    /// the start and none is at or after the end. The decoder decodes from the keyframe at or
    /// before the start up to the end, and nothing either side. This clip's container seeks by
    /// timestamp, so a seek to a mid-GOP start lands on a frame that is not a keyframe.
    /// </summary>
    [RequiresFfmpegAndCorpusTheory]
    [InlineData(1.52, 2.5)] // mid-GOP start: decoded from the keyframe at 1 s, delivered from 1.5417 s
    [InlineData(1.0, 2.0)] // on keyframes, and a frame at exactly the end
    [InlineData(0.0, 1.0)] // an end with no start
    [InlineData(1.52, null)] // a start with no end
    [InlineData(2.9, null)] // the last GOP
    public async Task ARange_DeliversTheFramesInIt_AndDecodesOnlyWhatItNeeds(double startSeconds, double? endSeconds)
    {
        var start = TimeSpan.FromSeconds(startSeconds);
        TimeSpan? end = endSeconds is { } e ? TimeSpan.FromSeconds(e) : null;
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var timeline = await VideoTimeline.ReadAsync(path!);
        var inRange = timeline.Frames.Where(t => t >= start && (end is null || t < end)).ToList();
        var keyframe = timeline.Keyframes.Last(k => k <= start);
        var needed = timeline.Frames.Count(t => t >= keyframe && (end is null || t < end));

        var (delivered, decoded) = await RunAsync(path!, pass => pass.WithRange(start, end));

        VideoTimeline.AssertSame(inRange, delivered);
        Assert.Equal(needed, decoded);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARangeFromZero_IsTheWholeSource()
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var timeline = await VideoTimeline.ReadAsync(path!);

        var (delivered, decoded) = await RunAsync(path!, pass => pass.WithRange(TimeSpan.Zero, null));

        VideoTimeline.AssertSame(timeline.Frames, delivered);
        Assert.Equal(timeline.Frames.Count, decoded);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARangePastTheEnd_DeliversNothing_AndEnds()
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);

        var (delivered, _) = await RunAsync(
            path!,
            pass => pass.WithRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12))
        );

        Assert.Empty(delivered);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARangeOfKeyframesOnly_DeliversTheKeyframesInIt()
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var timeline = await VideoTimeline.ReadAsync(path!);
        var start = TimeSpan.FromSeconds(0.5);
        var end = TimeSpan.FromSeconds(2.5);
        var inRange = timeline.Keyframes.Where(t => t >= start && t < end).ToList();
        Assert.True(inRange.Count > 1, $"{Clip} has {inRange.Count} keyframe(s) in the range.");

        var (delivered, _) = await RunAsync(
            path!,
            pass => pass.WithRange(start, end).WithDecodeDiscard(DecodeDiscardLevel.KeyframesOnly)
        );

        VideoTimeline.AssertSame(inRange, delivered);
    }

    /// <summary>
    /// With both streams decoded, each is bounded: video to the frames in the range, audio to the
    /// buffers that start in it. Audio is not trimmed, so the buffers reach from within a buffer of
    /// the start to within a buffer of the end.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task ARangeOverAudioAndVideo_BoundsBoth()
    {
        var path = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(path);
        var timeline = await VideoTimeline.ReadAsync(path!);
        var start = TimeSpan.FromSeconds(1);
        var end = TimeSpan.FromSeconds(2);
        var video = new TimestampSink();
        var audio = new TimestampAudioSink();

        await using (var pass = await FrameFlowPass
            .Create(path!)
            .WithVideoSink(video)
            .WithAudioSink(audio)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithRange(start, end)
            .BuildAsync())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pass.RunToCompletionAsync(cts.Token);
        }

        VideoTimeline.AssertSame(timeline.Frames.Where(t => t >= start && t < end), video.Timestamps);

        var buffers = audio.Buffers.ToList();
        Assert.NotEmpty(buffers);
        Assert.All(buffers, b => Assert.InRange(b.Start, start, end - TimeSpan.FromTicks(1)));
        Assert.True(buffers[0].Start - start <= buffers[0].Duration, $"The first buffer starts at {buffers[0].Start}.");
        Assert.True(end - (buffers[^1].Start + buffers[^1].Duration) <= buffers[^1].Duration, $"The last buffer starts at {buffers[^1].Start}.");
    }

    /// <summary>
    /// The timestamps of the frames a software-decoded pass delivers, in order, and how many
    /// frames its decoder decoded.
    /// </summary>
    private static async Task<(List<TimeSpan> Delivered, long Decoded)> RunAsync(
        string path,
        Func<IPassBuilder, IPassBuilder> configure
    )
    {
        var sink = new TimestampSink();
        await using var pass = await configure(
                FrameFlowPass.Create(path).WithVideoSink(sink).WithHardwareDecode(HardwareDecodeMode.Disabled)
            )
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pass.RunToCompletionAsync(cts.Token);
        return ([.. sink.Timestamps], pass.VideoDecoder!.GetDiagnostics().FramesDecoded);
    }
}
