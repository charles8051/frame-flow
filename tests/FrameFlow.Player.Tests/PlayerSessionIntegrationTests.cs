using FrameFlow.Graph;
using FrameFlow.Video;

namespace FrameFlow.Player.Tests;

/// <summary>
/// End-to-end integration tests for the builder + session against
/// real corpus files (skipped when FFmpeg shared libraries or the
/// test corpus aren't available).
/// </summary>
public sealed class PlayerSessionIntegrationTests
{
    [RequiresFfmpegAndCorpusFact]
    public async Task BuildAsync_VideoOnlyFile_OpensWithoutPlaying()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        var sink = new CountingVideoSink(_ => { });

        await using var session = await FrameFlowPlayer
            .Open(path!)
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        Assert.Single(session.Info.VideoStreams);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task BuildAsync_VideoOnlyFile_PlaysToCompletion()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        var presented = 0;
        var sink = new CountingVideoSink(() => Interlocked.Increment(ref presented));

        await using var session = await FrameFlowPlayer
            .Open(path!)
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        // Stream metadata is available before PlayToCompletionAsync.
        Assert.Single(session.Info.VideoStreams);
        Assert.Empty(session.Info.AudioStreams);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.PlayToCompletionAsync(cts.Token);

        // The corpus file is 3 seconds @ 24fps → 72 frames (give or take
        // codec-dependent end-of-stream behavior).
        Assert.InRange(presented, 60, 80);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task BuildAsync_AudioOnlyFile_PlaysToCompletion()
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-mp3.mp3");
        Assert.NotNull(path);

        var buffers = 0;
        var sink = new CountingAudioSink(() => Interlocked.Increment(ref buffers));

        await using var session = await FrameFlowPlayer
            .Open(path!)
            .WithAudioSink(sink)
            .BuildAsync();

        Assert.Empty(session.Info.VideoStreams);
        Assert.Single(session.Info.AudioStreams);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.PlayToCompletionAsync(cts.Token);

        // 3s MP3 → at least a few buffers; exact count is codec-dependent.
        Assert.True(buffers > 0, $"Expected at least one audio buffer; got {buffers}.");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task BuildAsync_VideoAndAudio_BothSinksDriven()
    {
        var path = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(path);

        var videoCount = 0;
        var audioCount = 0;
        var videoSink = new CountingVideoSink(() => Interlocked.Increment(ref videoCount));
        var audioSink = new CountingAudioSink(() => Interlocked.Increment(ref audioCount));

        await using var session = await FrameFlowPlayer
            .Open(path!)
            .WithVideoSink(videoSink)
            .WithAudioSink(audioSink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        Assert.NotEmpty(session.Info.VideoStreams);
        Assert.NotEmpty(session.Info.AudioStreams);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.PlayToCompletionAsync(cts.Token);

        Assert.True(videoCount > 0, $"Expected video frames; got {videoCount}.");
        Assert.True(audioCount > 0, $"Expected audio buffers; got {audioCount}.");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ConfigureVideo_AppliesOperatorInChain()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        var receivedSizes = new List<(int W, int H)>();
        var sink = new CountingVideoSink(
            frame =>
            {
                lock (receivedSizes)
                    receivedSizes.Add((frame.Width, frame.Height));
            }
        );

        await using var session = await FrameFlowPlayer
            .Open(path!)
            .ConfigureVideo(chain => chain.Then(VideoOperators.Resize("resize", 160, 120)))
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.PlayToCompletionAsync(cts.Token);

        // Every frame the sink saw should be the resized dimensions.
        Assert.NotEmpty(receivedSizes);
        Assert.All(receivedSizes, dims => Assert.Equal((160, 120), dims));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayToCompletionAsync_SecondCall_Throws()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        var sink = new CountingVideoSink(_ => { });

        await using var session = await FrameFlowPlayer
            .Open(path!)
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.PlayToCompletionAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.PlayToCompletionAsync(CancellationToken.None)
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayToCompletionAsync_NoSinkAttached_Throws()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        await using var session = await FrameFlowPlayer.Open(path!).BuildAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.PlayToCompletionAsync(CancellationToken.None)
        );
    }

    // ─── Sinks ──────────────────────────────────────────────────────

    private sealed class CountingVideoSink : IVideoSink
    {
        private readonly Action<IVideoFrame> _onPresent;

        public CountingVideoSink(Action onPresent)
            : this(_ => onPresent()) { }

        public CountingVideoSink(Action<IVideoFrame> onPresent)
        {
            _onPresent = onPresent;
        }

        public IFramePool FramePool => null!;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            _onPresent(frame);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingAudioSink : IAudioSink
    {
        private readonly Action _onBuffer;

        public CountingAudioSink(Action onBuffer)
        {
            _onBuffer = onBuffer;
        }

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            _onBuffer();
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
