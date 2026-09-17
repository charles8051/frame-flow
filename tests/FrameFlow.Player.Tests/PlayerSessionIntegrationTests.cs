using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback;
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
            .Create(path!)
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
            .Create(path!)
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
            .Create(path!)
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
            .Create(path!)
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
            .Create(path!)
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
            .Create(path!)
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

        await using var session = await FrameFlowPlayer.Create(path!).BuildAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.PlayToCompletionAsync(CancellationToken.None)
        );
    }

    // ADR-0059 on the BuildAsync path. The demux pump feeds every decoder's bounded
    // packet queue and waits when one is full. A stream with a decoder but no sink
    // has no graph branch draining its queue, so once that queue fills the pump
    // stops and the stream that does have a sink never reaches end of stream.
    //
    // The queue of the stream without a sink is shrunk to one packet, so the stall
    // does not depend on the fixture holding more packets than the default depth.
    // At that depth a decoder built for a discarded stream also stalls: the packet
    // the probe buffered before the discard fills the queue, and the end-of-stream
    // flush then waits on it. The packet count pins the discard itself.

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayToCompletionAsync_AvFileWithVideoSinkOnly_PresentsEveryVideoFrame()
    {
        var path = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(path);

        var presented = 0;
        var sink = new CountingVideoSink(() => Interlocked.Increment(ref presented));

        await using var session = await ((PlayerBuilder)FrameFlowPlayer.Create(path!))
            .WithDecoderOptions(audio: new AudioDecoderOptions { PacketQueueCapacity = 1 })
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        Assert.NotEmpty(session.Info.AudioStreams);

        await PlayOrFailOnStallAsync(session, () => $"{presented} video frames presented");

        var packets = await CountPacketsAsync(path!);
        Assert.Equal(packets.Video, presented);
        AssertDiscarded(session, played: packets.Video, discarded: packets.Audio);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayToCompletionAsync_AvFileWithAudioSinkOnly_PlaysToEnd()
    {
        var path = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(path);

        var buffers = 0;
        var sink = new CountingAudioSink(() => Interlocked.Increment(ref buffers));

        await using var session = await ((PlayerBuilder)FrameFlowPlayer.Create(path!))
            .WithDecoderOptions(video: new VideoDecoderOptions { PacketQueueCapacity = 1 })
            .WithAudioSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        Assert.NotEmpty(session.Info.VideoStreams);

        await PlayOrFailOnStallAsync(session, () => $"{buffers} audio buffers presented");

        var packets = await CountPacketsAsync(path!);
        Assert.True(buffers > 0, $"Expected audio buffers; got {buffers}.");
        AssertDiscarded(session, played: packets.Audio, discarded: packets.Video);
    }

    // The stream without a sink is discarded at the demuxer, so the pump reads the
    // played stream's packets plus the few the probe buffered before the discard.
    private static void AssertDiscarded(PlayerSession session, int played, int discarded)
    {
        var read = session.GetDemuxDiagnostics().PacketsRead;
        Assert.True(
            read - played < discarded / 10,
            $"The pump read {read} packets: {played} from the played stream and "
                + $"{read - played} of {discarded} from the stream with no sink."
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayToCompletionAsync_CancelledWhilePumpIsParkedOnFullQueue_Returns()
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-aac.m4a");
        Assert.NotNull(path);

        await using var session = await ((PlayerBuilder)FrameFlowPlayer.Create(path!))
            .WithDecoderOptions(audio: new AudioDecoderOptions { PacketQueueCapacity = 1 })
            .WithAudioSink(new BlockingAudioSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        using var cts = new CancellationTokenSource();
        var play = session.PlayToCompletionAsync(cts.Token);

        // The sink never returns a buffer, so once the pump parks on the full audio
        // queue nothing drains it again.
        var parked = session.WaitUntilPumpParkedAsync();
        Assert.Same(parked, await Task.WhenAny(play, parked).WaitAsync(TimeSpan.FromSeconds(30)));

        // Cancelling stops the graph. The pump then finalizes the decoders, whose
        // flush marker has to give up on the full queue for the run to return.
        cts.Cancel();
        var thrown = await Record.ExceptionAsync(() => play.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.False(thrown is TimeoutException, "PlayToCompletionAsync did not return after cancellation.");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    // The timeout only bounds a stalled run. A healthy run ends at end of stream.
    // The bound is on the wait, not a cancellation token, so it holds even for a
    // session that does not unwind when cancelled.
    private static async Task PlayOrFailOnStallAsync(PlayerSession session, Func<string> progress)
    {
        using var cts = new CancellationTokenSource();
        var play = session.PlayToCompletionAsync(cts.Token);
        try
        {
            await play.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            Assert.Fail(
                $"PlayToCompletionAsync did not reach end of stream ({progress()}). "
                    + "The stream without a sink has a decoder whose full packet queue blocked the demux pump."
            );
        }
    }

    private static async Task<(int Video, int Audio)> CountPacketsAsync(string path)
    {
        await using var demux = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path));
        var videoIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        var audioIndex = demux.MediaInfo.AudioStreams[0].StreamIndex;

        int video = 0,
            audio = 0;
        while (await demux.ReadPacketAsync() is { } packet)
        {
            if (packet.StreamIndex == videoIndex)
                video++;
            else if (packet.StreamIndex == audioIndex)
                audio++;
        }
        return (video, audio);
    }

    // ─── Sinks ──────────────────────────────────────────────────────

    [RequiresFfmpegAndCorpusFact]
    public async Task BuildPlayerAsync_ReturnsAWorkingStateMachine()
    {
        // The second terminal on the same chain. See issue #99.
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        var presented = 0;
        var sink = new CountingVideoSink(() => Interlocked.Increment(ref presented));

        await using var player = await FrameFlowPlayer
            .Create(path!)
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();

        Assert.Single(player.MediaInfo.VideoStreams);
        Assert.True(player.Duration > TimeSpan.Zero);

        // Pause and seek are the whole reason this terminal exists —
        // PlayerSession has neither.
        await player.PlayAsync();
        await player.PauseAsync();
        Assert.Equal(PlaybackState.Paused, player.State);

        await player.SeekAsync(TimeSpan.FromSeconds(1));
        Assert.True(player.Position >= TimeSpan.Zero);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task BuildPlayerAsync_NarrowedChain_CarriesPlayerOnlyOptions()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        var sink = new CountingVideoSink(() => { });
        var clock = new PlaybackClock();

        // WithRepeatMode narrows to IMediaPlayerBuilder; the shared
        // options still chain afterwards, and BuildPlayerAsync is the
        // only terminal the narrowed interface offers.
        await using var player = await FrameFlowPlayer
            .Create(path!)
            .WithRepeatMode(RepeatMode.All)
            .WithClock(clock)
            .WithAudioActivation(false)
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();

        Assert.Single(player.MediaInfo.VideoStreams);
    }

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

    /// <summary>Holds the first buffer until the graph cancels.</summary>
    private sealed class BlockingAudioSink : IAudioSink
    {
        public async ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetCanceled(ct)))
                await cancelled.Task.ConfigureAwait(false);
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
