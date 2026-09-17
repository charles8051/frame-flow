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

    private static IMediaSource StillFor(TimeSpan dwell) =>
        File(Still) with
        {
            InputFormat = "image2",
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framerate"] = $"1/{dwell.TotalSeconds:0}",
            },
        };

    [RequiresFfmpegAndCorpusFact]
    public async Task AQueueOfUnlikeItems_PlaysThroughOnOnePlayer()
    {
        var audio = File(AudioOnly);
        var still = StillFor(Dwell);
        var video = File(Video);

        var videoSink = new HarnessVideoSink();
        var audioSink = new HarnessAudioSink();

        await using var player = await FrameFlowPlayer
            .Create([audio, still, video])
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
        Assert.Same(still, toStill.Source);
        Assert.Equal(1, toStill.Index);
        Assert.Equal(Dwell, toStill.MediaInfo.Duration);
        Assert.Single(toStill.MediaInfo.VideoStreams);
        Assert.Empty(toStill.MediaInfo.AudioStreams);

        // The clip after it: both streams, on the same warm presenter, with no teardown
        // between them.
        var toVideo = seen[1].Transition;
        Assert.Same(video, toVideo.Source);
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

        await using var pump = new FakeDevicePump(device);
        await using var player = await FrameFlowPlayer
            .Create([File(AudioOnly)])
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await endedTcs.Task.WaitAsync(cts.Token);
        var atEnded = sw.Elapsed;

        // Ended means the last buffer has been handed to the sink, not that the device has
        // finished playing it, so the clip is still coming out of the speaker here. Waiting
        // for the device to run dry is what makes the elapsed time comparable to the clip's
        // length rather than short of it by the sink's queue depth.
        Assert.True(await DrainAsync(device), "The device still held unplayed audio.");
        sw.Stop();

        Assert.Equal(PlaybackState.Ended, player.State);

        // A floor, for the same reason as above. Unpaced, the whole clip is handed over in
        // milliseconds, so this separates the two by more than an order of magnitude.
        var floor = expected - TimeSpan.FromMilliseconds(400);
        Assert.True(
            sw.Elapsed >= floor,
            $"Play to a drained device took {sw.Elapsed.TotalSeconds:F3}s (Ended at "
                + $"{atEnded.TotalSeconds:F3}s) for a {expected.TotalSeconds:F3}s audio-only "
                + "item, so it was not paced."
        );
    }

    /// <summary>
    /// Waits for the fake device to play out everything queued on it. Bounded so a device
    /// that never drains fails the test rather than hanging it.
    /// </summary>
    private static async Task<bool> DrainAsync(FakeOpenAlDevice device)
    {
        for (int i = 0; i < 400; i++)
        {
            if (device.AllQueuedAudioPlayed)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return device.AllQueuedAudioPlayed;
    }
}
