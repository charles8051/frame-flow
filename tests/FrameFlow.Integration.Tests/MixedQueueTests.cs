using System.Diagnostics;
using FrameFlow.Audio.TestKit;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// One queue whose items do not agree on what streams they have: audio with no video, a
/// still with no audio, and an ordinary clip with both.
/// </summary>
/// <remarks>
/// <para>
/// Every queue fixture in this suite was one clip repeated, so the presenter never had to
/// survive the stream layout changing under it. The mixed case is the one a host actually
/// builds, a playlist of music, slides and video through one warm presenter, and it is what
/// #248 and #257 together were for: a still is a queue item of a known length rather than
/// one frame that ends as soon as it is presented.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MixedQueueTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string AudioOnly = "test-audio-only.mp4";
    private const string Still = "test-still.png";
    private const string Video = "test-subsecond.mp4";

    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(1);

    private static MediaSource File(string name)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(name);
        Assert.True(path is not null, $"Corpus file {name} not found.");
        return MediaSource.FromFile(path!);
    }

    private static IMediaSource StillFor(TimeSpan dwell)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Still);
        Assert.True(path is not null, $"Corpus file {Still} not found.");
        return MediaSource.FromStill(path!, dwell);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AQueueOfUnlikeItems_PlaysThroughOnOnePlayer()
    {
        var audio = File(AudioOnly);
        var still = StillFor(Dwell);
        var video = File(Video);

        var videoSink = new HarnessVideoSink();
        var audioSink = new HarnessAudioSink();

        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia([audio, still, video])
            .WithVideoSink(videoSink)
            .WithAudioSink(audioSink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();

        Assert.Equal(3, player.GetPlaylist().Playlist.Count);

        // The first item is current from the build, so it raises no transition. The two that
        // follow do, and each carries the MediaInfo the presenter switched to.
        var transitions = new List<(PlaylistTransition Transition, long Timestamp)>();
        var gate = new Lock();
        using var tSub = player.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(t =>
            {
                lock (gate)
                    transitions.Add((t, Stopwatch.GetTimestamp()));
            })
        );

        var errors = new List<PlaybackError>();
        using var eSub = player.ErrorOccurred.Subscribe(
            new ActionObserver<PlaybackError>(errors.Add)
        );

        var endedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        long endedTimestamp = 0;
        using var sSub = player.StateChanged.Subscribe(
            new ActionObserver<PlaybackState>(s =>
            {
                if (s is PlaybackState.Ended or PlaybackState.Error)
                {
                    Interlocked.CompareExchange(ref endedTimestamp, Stopwatch.GetTimestamp(), 0);
                    endedTcs.TrySetResult();
                }
            })
        );

        Assert.Same(audio, player.CurrentSource);

        var play = await player.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await endedTcs.Task.WaitAsync(cts.Token);

        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Empty(errors);

        (PlaylistTransition Transition, long Timestamp)[] seen;
        lock (gate)
            seen = [.. transitions];

        Assert.Equal(2, seen.Length);

        // The still: video and no audio, and a length it has only because the source carries
        // demuxer options (#248).
        var toStill = seen[0].Transition;
        Assert.Same(still, toStill.Item.Source);
        Assert.Equal(1, toStill.Index);
        Assert.Equal(Dwell, toStill.MediaInfo.Duration);
        Assert.Single(toStill.MediaInfo.VideoStreams);
        Assert.Empty(toStill.MediaInfo.AudioStreams);

        // The clip after it: both streams, on the same warm presenter, with no teardown
        // between them.
        var toVideo = seen[1].Transition;
        Assert.Same(video, toVideo.Item.Source);
        Assert.Equal(2, toVideo.Index);
        Assert.Single(toVideo.MediaInfo.VideoStreams);
        Assert.Single(toVideo.MediaInfo.AudioStreams);

        // The still stayed up for its dwell rather than being handed straight on. A floor
        // only: a busy machine makes this longer, never shorter, so it fails in the
        // direction the regression lies in.
        var held = Stopwatch.GetElapsedTime(seen[0].Timestamp, seen[1].Timestamp);
        Assert.True(
            held >= Dwell - TimeSpan.FromMilliseconds(150),
            $"The still was current for {held.TotalSeconds:F3}s of its {Dwell.TotalSeconds:F0}s "
                + "dwell, so it was not held."
        );

        // And the clip after it was paced too, rather than the queue draining at decode speed
        // once the still was out of the way.
        var lastLeg = Stopwatch.GetElapsedTime(
            seen[1].Timestamp,
            Volatile.Read(ref endedTimestamp)
        );
        Assert.True(
            lastLeg >= TimeSpan.FromMilliseconds(350),
            $"The final clip ran in {lastLeg.TotalSeconds:F3}s against its 0.5s length."
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnAudioOnlyItem_IsPacedByTheSinkThatCarriesTheClock()
    {
        // An item with no video has no video pacer to hold it, so the only thing that can
        // pace it is the audio sink, through backpressure: OpenAlAudioSink.PresentAsync waits
        // for a buffer to recycle once every pooled one is queued, and the device returns them
        // at the rate it plays them. A sink that accepts everything immediately — a capture
        // double, a null sink — applies none, and a three-second clip is handed over in about
        // fifty milliseconds. That is why this uses the sink that ships rather than a
        // stand-in, and it is not the master clock: blocking the session's upgrade of the
        // master to the audio sink's IClockSource leaves this test passing.
        var device = new FakeOpenAlDevice();
        var audioSink = FakeOpenAlSink.Create(device);
        var videoSink = new HarnessVideoSink();

        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia([File(AudioOnly)])
            .WithVideoSink(videoSink)
            .WithAudioSink(audioSink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();

        var expected = player.Duration;
        Assert.True(expected > TimeSpan.Zero, "The audio-only item reported no duration.");

        var endedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sSub = player.StateChanged.Subscribe(
            new ActionObserver<PlaybackState>(s =>
            {
                if (s is PlaybackState.Ended or PlaybackState.Error)
                    endedTcs.TrySetResult();
            })
        );

        var sw = Stopwatch.StartNew();
        Assert.True((await player.PlayAsync()).IsSuccess);

        // The device plays nothing until its pump starts, so every buffer the sink queues stays
        // queued, and the producer fills the sink's pool and is held back. Waiting for that makes
        // the pacing visible at any speed. With the device playing from the start, a producer
        // that a loaded runner held back never got the whole pool ahead of the device, and the
        // sink had nothing to push back on.
        await WhenHeldBackAsync(audioSink);
        await using var pump = new FakeDevicePump(device);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await endedTcs.Task.WaitAsync(cts.Token);
        var atEnded = sw.Elapsed;

        // The assertions are on what the DEVICE had played by the time the pipeline said
        // Ended, not on how long anything took. Ended waits for the sink's drain (#609), so the
        // device has played the clip out by then: nothing still queued, nothing held back.
        //
        // Measured before the drain: 1.880s of 3.000s played at Ended, which is the whole clip
        // less the ~1.1s the sink runs ahead. Whoever acted on Ended by tearing the sink down
        // cut that second off.
        var playedAtEnded = PlayedDuration(device);
        var queuedAtEnded = !device.AllQueuedAudioPlayed;
        var backpressureWaits = audioSink.BackpressureCount;
        sw.Stop();

        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.False(
            queuedAtEnded,
            $"Ended fired at {atEnded.TotalSeconds:F3}s with audio still queued on the device, "
                + $"after {playedAtEnded.TotalSeconds:F3}s of the {expected.TotalSeconds:F3}s "
                + "clip, so the end of the item had not been heard."
        );
        Assert.True(
            playedAtEnded >= expected - TimeSpan.FromMilliseconds(400),
            $"The device played {playedAtEnded.TotalSeconds:F3}s of a "
                + $"{expected.TotalSeconds:F3}s clip."
        );

        // The pacing, named rather than inferred. With Ended waiting for the drain, what the
        // device has played at Ended no longer says whether the producer was throttled, so the
        // sink says it: PresentAsync counts every time it had to wait for a buffer to recycle,
        // and a non-zero count is the sink reporting that it held the producer back, rather
        // than a producer that happened to be slow for some unrelated reason.
        Assert.True(
            backpressureWaits > 0,
            "The sink never waited for a buffer, so whatever paced this was not its backpressure."
        );
    }

    /// <summary>
    /// Completes once the sink has had to wait for a buffer to recycle. The sink raises no
    /// signal for it, so its counter is read until it moves; the count only rises, and the bound
    /// only stops a sink that never pushes back.
    /// </summary>
    private static async Task WhenHeldBackAsync(FrameFlow.Audio.OpenAL.OpenAlAudioSink sink)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (sink.BackpressureCount == 0)
        {
            try
            {
                await Task.Delay(10, bound.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(
                    "The sink filled its pool with the device playing nothing and never held "
                        + "the producer back."
                );
            }
        }
    }

    private static TimeSpan PlayedDuration(FakeOpenAlDevice device)
    {
        int rate = device.PlayedSampleRate;
        int channels = device.PlayedChannels;
        if (rate <= 0 || channels <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)device.PlayedSamples.Count / channels / rate);
    }
}
