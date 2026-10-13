using System.Collections.Concurrent;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass is one run, so everything it decodes is on one timeline of its own, video and audio
/// alike (ADR-0084, decision 2).
/// </summary>
public sealed class PassTimelineTests
{
    private const string Fixture = "test-av-h264-aac.mp4";

    [RequiresFfmpegAndCorpusFact]
    public async Task APass_PutsEverythingItDecodesOnOneTimeline_AndTheNextPassOnAnother()
    {
        var first = await RunPassAsync();
        var second = await RunPassAsync();

        var firstTimeline = AssertOneTimeline(first);
        var secondTimeline = AssertOneTimeline(second);
        Assert.NotEqual(firstTimeline, secondTimeline);
    }

    private static TimelineId AssertOneTimeline((TimelineId[] Video, TimelineId[] Audio) pass)
    {
        Assert.NotEmpty(pass.Video);
        Assert.NotEmpty(pass.Audio);
        var timeline = pass.Video[0];
        Assert.NotEqual(TimelineId.None, timeline);
        Assert.All(pass.Video, id => Assert.Equal(timeline, id));
        Assert.All(pass.Audio, id => Assert.Equal(timeline, id));
        return timeline;
    }

    private static async Task<(TimelineId[] Video, TimelineId[] Audio)> RunPassAsync()
    {
        var path = TestEnvironment.GetCorpusFile(Fixture);
        Assert.NotNull(path);
        var video = new TimelineVideoSink();
        var audio = new TimelineAudioSink();

        await using var pass = await FrameFlowPass
            .Create(path!)
            .WithVideoSink(video)
            .WithAudioSink(audio)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pass.RunToCompletionAsync(cts.Token);

        return (video.Timelines.ToArray(), audio.Timelines.ToArray());
    }

    private sealed class TimelineVideoSink : IVideoSink
    {
        public ConcurrentQueue<TimelineId> Timelines { get; } = new();

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            Timelines.Enqueue(frame.Timeline);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimelineAudioSink : IAudioSink
    {
        public ConcurrentQueue<TimelineId> Timelines { get; } = new();

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            Timelines.Enqueue(buffer.Timeline);
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
