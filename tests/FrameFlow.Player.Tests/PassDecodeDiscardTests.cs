using FrameFlow.Decoding;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass that decodes keyframes only (#482), checked against the keyframes the demuxer reports.
/// The hardware-decode case is in <c>FrameFlow.Inference.Dml.Tests</c>, which has the gate.
/// </summary>
public sealed class PassDecodeDiscardTests
{
    /// <summary>
    /// A keyframe a second, and the one fixture whose timestamps start past zero, so a frame that
    /// reported its container time would show.
    /// </summary>
    internal const string Clip = "test-video-h264-start-offset.ts";

    [RequiresFfmpegAndCorpusFact]
    public async Task KeyframesOnly_DeliversEachKeyframe_AtItsOwnTimestamp()
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var timeline = await VideoTimeline.ReadAsync(path!);
        Assert.True(
            timeline.Keyframes.Count > 1 && timeline.Keyframes.Count < timeline.Frames.Count,
            $"{Clip} has {timeline.Keyframes.Count} keyframes in {timeline.Frames.Count} frames, which cannot tell keyframes from the rest."
        );

        var delivered = await RunAsync(path!, DecodeDiscardLevel.KeyframesOnly);

        VideoTimeline.AssertSame(timeline.Keyframes, delivered);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task None_DeliversEveryFrame()
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var timeline = await VideoTimeline.ReadAsync(path!);

        var delivered = await RunAsync(path!, DecodeDiscardLevel.None);

        VideoTimeline.AssertSame(timeline.Frames, delivered);
    }

    /// <summary>The timestamps of the frames a software-decoded pass delivers, in order.</summary>
    private static async Task<List<TimeSpan>> RunAsync(string path, DecodeDiscardLevel level)
    {
        var sink = new TimestampSink();
        await using var pass = await FrameFlowPass
            .Create(path)
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithDecodeDiscard(level)
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pass.RunToCompletionAsync(cts.Token);
        return [.. sink.Timestamps];
    }
}
