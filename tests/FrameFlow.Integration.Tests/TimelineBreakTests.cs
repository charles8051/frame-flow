using FrameFlow.Graph;
using FrameFlow.Graph.Core;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using static FrameFlow.Integration.Tests.Harness.TimelineRecord;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Every break in the timeline starts a new one, on real playback (ADR-0084, staging slice 1): for
/// each kind of break, the first video frame and the first audio buffer after it carry an id no
/// earlier item carried, and the items between two breaks share one.
/// </summary>
/// <remarks>
/// <para>
/// The witness is what the sinks are handed: each item's presentation time and timeline, in
/// order. The record is split into stretches on one timeline, and each test checks that the
/// stretches fall where its breaks put them: what came before a break is from the old position,
/// what came after is from the new one. A stretch is checked by what it must not contain, not by
/// how far playback is hoped to have got: it runs forward without a jump, so an item from the other
/// side of a break shows as one.
/// </para>
/// <para>
/// Video is paced, so its stretches are checked against where playback had reached. The audio
/// sink here has no device and takes each buffer at once, so audio runs ahead to the end of what a
/// run decodes; its stretches are checked for where they start and that they run forward.
/// </para>
/// <para>
/// These tests play in real time, which is why they are in this suite (ADR-0072 rule 6). Each wait
/// completes on a sink receiving an item; the bound only stops a failing run. The player is
/// disposed before the record is read, so nothing is delivered while it is.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TimelineBreakTests : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>3 s, 30 fps H.264 with AAC.</summary>
    private const string Fixture = "test-av-h264-aac.mp4";

    /// <summary>10 s, 60 fps H.264 with AAC, whose only keyframe is at zero.</summary>
    private const string OneKeyframe = "test-1080p60-h264-aac.mp4";

    /// <summary>3 s, 24 fps HEVC with B-frames, so the decoder holds frames back to reorder them.</summary>
    private const string Reordered = "test-video-h265-yuv420p.mp4";

    /// <summary>
    /// Half a frame at 30 fps. A frame's time read back from the stream can land a tick either side
    /// of the time it was written at, so one within this of a target is at it.
    /// </summary>
    private static readonly TimeSpan HalfFrame = TimeSpan.FromSeconds(1.0 / 60);

    /// <summary>The audio a run starts with is cut at its start, to within a sample.</summary>
    private static readonly TimeSpan AudioTolerance = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Where the item has reached before a break is taken.</summary>
    private static readonly TimeSpan Played = TimeSpan.FromMilliseconds(400);

    /// <summary>A break's destination, well past <see cref="Played"/>.</summary>
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The most one stretch may skip between two consecutive items: a few frames the pacer dropped.
    /// Every break here jumps by more, so an item from the other side of one shows as a jump.
    /// </summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan PastTarget = Target + TimeSpan.FromMilliseconds(200);

    public TimelineBreakTests(FfmpegBootstrapFixture fixture) => _ = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task ALoad_PlaysOnOneTimeline_FromWarmUpThroughPlay()
    {
        // The warm-up launches the item's first run, and the first play opens the gates on it
        // rather than launching another. Frames decoded at warm-up and after play are one stretch.
        var rig = await Rig.BuildAsync(Corpus(Fixture));
        await using (rig)
        {
            await rig.PlayAsync();
            await rig.WhenBothReach(Played, skip: (0, 0));
        }

        var (video, audio) = rig.Segments();
        Assert.Single(video);
        Assert.Single(audio);
        AssertSameTimelines(video, audio);
        AssertContinues(video[0], "video");
        AssertContinues(audio[0], "audio");
        Assert.Single(rig.Log.Launches);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASeekWhilePlaying_StartsANewTimeline()
    {
        var rig = await Rig.BuildAsync(Corpus(Fixture));
        await using (rig)
        {
            await rig.PlayAsync();
            await rig.WhenBothReach(Played, skip: (0, 0));
            var mark = rig.Mark();

            Assert.True((await rig.Player.SeekAsync(Target)).IsSuccess);
            await rig.WhenBothReach(PastTarget, skip: mark);
        }

        AssertOneBreak(rig, after: Target);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASeekWhilePaused_StartsANewTimeline_AtThePlayThatFollows()
    {
        var rig = await Rig.BuildAsync(Corpus(Fixture));
        await using (rig)
        {
            await rig.PlayAsync();
            await rig.WhenBothReach(Played, skip: (0, 0));
            Assert.True((await rig.Player.PauseAsync()).IsSuccess);
            var mark = rig.Mark();

            Assert.True((await rig.Player.SeekAsync(Target)).IsSuccess);
            Assert.True((await rig.Player.PlayAsync()).IsSuccess);
            await rig.WhenBothReach(PastTarget, skip: mark);
        }

        AssertOneBreak(rig, after: Target);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ALoop_StartsANewTimeline_AtEachWrap()
    {
        // A clip that ends at 0.6 s loops under RepeatMode.One by rewinding the open item and
        // running its graph again. Every wrap back to the start is a new timeline, and nothing
        // else is.
        var clip = new MediaClip(Corpus(Fixture), TimeSpan.Zero, TimeSpan.FromMilliseconds(600));
        var loops = 0;
        var twoLoops = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = await Rig.BuildAsync(clip, RepeatMode.One);
        await using (rig)
        {
            using var sub = rig.Player.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(_ =>
                {
                    if (Interlocked.Increment(ref loops) == 2)
                        twoLoops.TrySetResult();
                })
            );
            await rig.PlayAsync();
            await twoLoops.Task.WaitAsync(Bound);
            var mark = rig.Mark();
            await rig.WhenBothReach(TimeSpan.FromMilliseconds(200), skip: mark);
        }

        var (video, audio) = rig.Segments();
        AssertBreaksAreTheWraps(rig.Video.Items, "video");
        AssertBreaksAreTheWraps(rig.Audio.Items, "audio");
        Assert.True(video.Count >= 3, $"video went through {video.Count} timelines over two loops");
        AssertSameTimelines(video, audio);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AHandOffToAClipOfTheSameSource_StartsANewTimeline()
    {
        // One source object for both items, so the jump rewinds the open item in place: the same
        // session, its run number advanced.
        var source = Corpus(Fixture);
        var rig = await Rig.BuildAsync(new MediaClip(source));
        await using (rig)
        {
            var next = await rig.Player.AddAsync(new MediaClip(source, Target));
            await rig.PlayAsync();
            await rig.WhenBothReach(Played, skip: (0, 0));
            var mark = rig.Mark();

            Assert.True((await rig.Player.JumpToAsync(next)).IsSuccess);
            await rig.WhenBothReach(PastTarget, skip: mark);
        }

        AssertOneBreak(rig, after: Target);
        var launches = rig.Log.Launches;
        Assert.Equal(2, launches.Count);
        Assert.True(launches[1].RunNumber > launches[0].RunNumber, "the hand-off opened a new session");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnItemOfAnotherSource_StartsANewTimeline()
    {
        // Another source object, so the jump opens a new session for it: its run numbers start
        // again, and its timeline is still new.
        var rig = await Rig.BuildAsync(new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(1)));
        await using (rig)
        {
            var next = await rig.Player.AddAsync(Corpus(Fixture));
            await rig.PlayAsync();
            await rig.WhenBothReach(TimeSpan.FromSeconds(1) + Played, skip: (0, 0));
            var mark = rig.Mark();

            Assert.True((await rig.Player.JumpToAsync(next)).IsSuccess);
            await rig.WhenBothReach(Played, skip: mark, newTimeline: true);
        }

        AssertBackToTheStart(rig, from: TimeSpan.FromSeconds(1));
        var launches = rig.Log.Launches;
        Assert.Equal(2, launches.Count);
        Assert.Equal(0, launches[1].RunNumber);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AReplace_StartsANewTimeline()
    {
        var rig = await Rig.BuildAsync(new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(1)));
        await using (rig)
        {
            await rig.PlayAsync();
            await rig.WhenBothReach(TimeSpan.FromSeconds(1) + Played, skip: (0, 0));
            var mark = rig.Mark();

            Assert.True((await rig.Player.ReplaceAsync(new IMediaSource[] { Corpus(Fixture) })).IsSuccess);
            await rig.WhenBothReach(Played, skip: mark, newTimeline: true);
        }

        AssertBackToTheStart(rig, from: TimeSpan.FromSeconds(1));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AClipStart_PlaysOnOneTimeline_FromItsStart()
    {
        // The open positions the item at its start, and that launches nothing: the warm-up that
        // follows launches the run, so the decode from the keyframe and the play are one stretch,
        // and nothing from before the start reaches a sink under it.
        var rig = await Rig.BuildAsync(new MediaClip(Corpus(Fixture), Target));
        await using (rig)
        {
            await rig.PlayAsync();
            await rig.WhenBothReach(PastTarget, skip: (0, 0));
        }

        var (video, audio) = rig.Segments();
        Assert.Single(video);
        Assert.Single(audio);
        AssertSameTimelines(video, audio);
        AssertRunsForwardFrom(video[0], Target, HalfFrame, "video");
        AssertRunsForwardFrom(audio[0], Target, AudioTolerance, "audio");
        Assert.Single(rig.Log.Launches);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnItemPreparedAhead_AndPromotedAfterThePlayingItemLooped_StartsANewTimeline()
    {
        // The prepared item launches its run, and mints its id, at warm-up, before the playing
        // item's later loops mint theirs (#674). Taken in presentation order it is still the next
        // timeline for both followers.
        var playing = new MediaClip(Corpus(Fixture), TimeSpan.Zero, TimeSpan.FromMilliseconds(600));
        var prepared = new MediaClip(Corpus(Fixture), Target);
        var loopsAfterPrepare = 0;
        var looped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareDone = 0;
        var rig = await Rig.BuildAsync(playing, RepeatMode.One);
        await using (rig)
        {
            var next = await rig.Player.AddAsync(prepared);
            using var sub = rig.Player.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(_ =>
                {
                    if (Volatile.Read(ref prepareDone) == 1 && Interlocked.Increment(ref loopsAfterPrepare) == 2)
                        looped.TrySetResult();
                })
            );
            await rig.PlayAsync();
            await rig.WhenBothReach(TimeSpan.FromMilliseconds(100), skip: (0, 0));

            Assert.True((await rig.Player.PrepareAsync(next)).IsSuccess);
            Volatile.Write(ref prepareDone, 1);
            await looped.Task.WaitAsync(Bound);
            var mark = rig.Mark();

            Assert.True((await rig.Player.JumpToAsync(next)).IsSuccess);
            await rig.WhenBothReach(PastTarget, skip: mark);
        }

        var (video, audio) = rig.Segments();
        AssertSameTimelines(video, audio);
        Assert.True(video.Count >= 3, $"video went through {video.Count} timelines");
        var promoted = video[^1];
        var lastLoop = video[^2];
        AssertRunsForwardFrom(promoted, Target, HalfFrame, "video");
        AssertRunsForwardFrom(audio[^1], Target, AudioTolerance, "audio");
        Assert.All(video.SkipLast(1).SelectMany(s => s.Pts), pts => Assert.True(pts < TimeSpan.FromMilliseconds(600)));

        // The scenario: the promoted item's run launched before the last loop's did.
        var launches = rig.Log.Launches.Select(l => l.Timeline).ToList();
        Assert.Contains(promoted.Timeline, launches);
        Assert.Contains(lastLoop.Timeline, launches);
        Assert.True(
            launches.IndexOf(promoted.Timeline) < launches.IndexOf(lastLoop.Timeline),
            "the prepared item was not launched before the playing item's last loop"
        );

        // Both followers, fed in presentation order, take it as the next timeline.
        var follower = default(TimelineFollower);
        var screen = new PresentedTimeline();
        foreach (var segment in video.SkipLast(1))
        {
            follower = follower.Follow(segment.Timeline);
            screen = screen.Present(segment.Timeline);
        }

        Assert.True(follower.StartsNewTimeline(promoted.Timeline));
        Assert.Equal(TimelineRelation.Upcoming, screen.Classify(promoted.Timeline));
        screen = screen.Present(promoted.Timeline);
        Assert.Equal(TimelineRelation.Current, screen.Classify(promoted.Timeline));
        Assert.Equal(TimelineRelation.Past, screen.Classify(lastLoop.Timeline));
    }

    [RequiresFfmpegAndCorpusTheory]
    // The audio sink stops taking audio at the old position, as a device with a full buffer does,
    // so the audio decoder holds the packets read past it. AAC packets decode on their own, so one
    // left in the queue would come out under the new id.
    [InlineData(OneKeyframe, true)]
    // Video alone, so the pump is paced by the video decoder and has not reached the end, and with
    // B-frames, so the codec holds decoded frames back to put them in presentation order.
    [InlineData(Reordered, false)]
    public async Task ARepositionWithQueuedPacketsAndCodecDelay_PutsNothingFromTheOldPositionOnTheNewTimeline(
        string fixture,
        bool withAudio
    )
    {
        // At the seek the decoders hold the old position: packets queued, and frames in the codec.
        // The seek goes back to before where playback had reached, so anything from the old
        // position that came out under the new id would be later than the target, and come before
        // it.
        var from = TimeSpan.FromSeconds(2);
        var to = TimeSpan.FromMilliseconds(500);
        var rig = await Rig.BuildAsync(new MediaClip(Corpus(fixture)), withAudio: withAudio);
        await using (rig)
        {
            rig.AudioSink.HoldFrom(from);
            await rig.PlayAsync();
            await rig.WhenBothReach(from, skip: (0, 0));
            var mark = rig.Mark();

            Assert.True((await rig.Player.SeekAsync(to)).IsSuccess);
            await rig.WhenBothReach(to + TimeSpan.FromMilliseconds(300), skip: mark, newTimeline: true);
        }

        // The new timeline starts at the target and runs on from it without a jump, so nothing from
        // the old position is on it, first or later.
        var (video, audio) = rig.Segments();
        Assert.Equal(2, video.Count);
        AssertContinuesFrom(video[1], to, HalfFrame, "video");
        if (withAudio)
        {
            Assert.Equal(2, audio.Count);
            AssertSameTimelines(video, audio);
            AssertContinuesFrom(audio[1], to, AudioTolerance, "audio");
        }
    }

    // ── Assertions ────────────────────────────────────────────────────

    /// <summary>
    /// Exactly one break, on both streams: before it, a stretch that runs on without a jump, so
    /// nothing from <paramref name="after"/> is on the old timeline; after it, both streams starting
    /// at <paramref name="after"/> and running on.
    /// </summary>
    private static void AssertOneBreak(Rig rig, TimeSpan after)
    {
        var (video, audio) = rig.Segments();
        Assert.Equal(2, video.Count);
        Assert.Equal(2, audio.Count);
        AssertSameTimelines(video, audio);

        AssertContinues(video[0], "video");
        AssertContinuesFrom(video[1], after, HalfFrame, "video");
        AssertContinues(audio[0], "audio");
        AssertContinuesFrom(audio[1], after, AudioTolerance, "audio");
    }

    /// <summary>
    /// Exactly one break, to another item that plays from its beginning: the stretch before it
    /// starts at <paramref name="from"/>, and the one after starts again before it.
    /// </summary>
    private static void AssertBackToTheStart(Rig rig, TimeSpan from)
    {
        var (video, audio) = rig.Segments();
        Assert.Equal(2, video.Count);
        Assert.Equal(2, audio.Count);
        AssertSameTimelines(video, audio);

        AssertRunsForwardFrom(video[0], from, HalfFrame, "video");
        AssertRunsForwardFrom(audio[0], from, AudioTolerance, "audio");
        AssertContinues(video[1], "video");
        AssertContinues(audio[1], "audio");
        Assert.True(video[1].Pts[0] < from - HalfFrame, $"video after the break starts at {video[1].Pts[0]}");
        Assert.True(audio[1].Pts[0] < from - AudioTolerance, $"audio after the break starts at {audio[1].Pts[0]}");
    }

    /// <summary>A stretch that starts at <paramref name="start"/> and runs forward from it.</summary>
    private static void AssertRunsForwardFrom(Segment segment, TimeSpan start, TimeSpan tolerance, string stream)
    {
        Assert.True(
            (segment.Pts[0] - start).Duration() <= tolerance,
            $"{stream}: a timeline starts at {segment.Pts[0]}, not {start}: {Describe(segment)}"
        );
        AssertRunsForward(segment, stream);
    }

    /// <summary>
    /// A stretch that starts at <paramref name="start"/> and runs on from it without a jump wider
    /// than <see cref="MaxGap"/>.
    /// </summary>
    private static void AssertContinuesFrom(Segment segment, TimeSpan start, TimeSpan tolerance, string stream)
    {
        AssertRunsForwardFrom(segment, start, tolerance, stream);
        AssertContinues(segment, stream);
    }

    /// <summary>
    /// A stretch that runs forward without a jump wider than <see cref="MaxGap"/>: an item from the
    /// other side of a break would be one.
    /// </summary>
    private static void AssertContinues(Segment segment, string stream)
    {
        AssertRunsForward(segment, stream);
        for (var i = 1; i < segment.Pts.Count; i++)
        {
            Assert.True(
                segment.Pts[i] - segment.Pts[i - 1] <= MaxGap,
                $"{stream}: {segment.Timeline} jumps from {segment.Pts[i - 1]} to {segment.Pts[i]}"
            );
        }
    }

    /// <summary>A stretch whose presentation times never go back: one continuous timeline.</summary>
    private static void AssertRunsForward(Segment segment, string stream)
    {
        for (var i = 1; i < segment.Pts.Count; i++)
        {
            Assert.True(
                segment.Pts[i] >= segment.Pts[i - 1],
                $"{stream}: {segment.Timeline} goes back from {segment.Pts[i - 1]} to {segment.Pts[i]}"
            );
        }
    }

    /// <summary>
    /// The timeline changes exactly where the presentation time goes back to the start of the
    /// loop, and nowhere else.
    /// </summary>
    private static void AssertBreaksAreTheWraps(IReadOnlyList<Stamped> items, string stream)
    {
        AssertEachBreakStartsANewTimeline(items, stream);
        for (var i = 1; i < items.Count; i++)
        {
            var wraps = items[i].Pts < items[i - 1].Pts;
            var breaks = items[i].Timeline != items[i - 1].Timeline;
            Assert.True(
                wraps == breaks,
                $"{stream}: at item {i}, {items[i - 1].Pts} on {items[i - 1].Timeline} then {items[i].Pts} on {items[i].Timeline}"
            );
        }
    }

    private static MediaSource Corpus(string name) =>
        MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(name)!);

    /// <summary>A player over recording sinks, software decode and a log of its launches.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(IMediaPlayer player, TimelineRecordingVideoSink video, TimelineRecordingAudioSink audio, LaunchLog log, bool withAudio)
        {
            Player = player;
            VideoSink = video;
            AudioSink = audio;
            Log = log;
            WithAudio = withAudio;
        }

        /// <summary>Whether the player was given the audio sink.</summary>
        public bool WithAudio { get; }

        public IMediaPlayer Player { get; }

        public TimelineRecordingVideoSink VideoSink { get; }

        public TimelineRecordingAudioSink AudioSink { get; }

        public StampRecorder Video => VideoSink.Recorder;

        public StampRecorder Audio => AudioSink.Recorder;

        public LaunchLog Log { get; }

        public static Task<Rig> BuildAsync(IMediaSource source, RepeatMode repeat = RepeatMode.Off) =>
            BuildAsync(new MediaClip(source), repeat);

        public static async Task<Rig> BuildAsync(MediaClip clip, RepeatMode repeat = RepeatMode.Off, bool withAudio = true)
        {
            var video = new TimelineRecordingVideoSink();
            var audio = new TimelineRecordingAudioSink();
            var log = new LaunchLog();
            var builder = FrameFlowPlayer
                .Create()
                .WithMedia(clip)
                .WithVideoSink(video)
                .WithHardwareDecode(HardwareDecodeMode.Disabled)
                .WithRepeatMode(repeat)
                .WithLogger(log);
            if (withAudio)
                builder = builder.WithAudioSink(audio);
            var player = await builder.BuildPlayerAsync();
            return new Rig(player, video, audio, log, withAudio);
        }

        public async Task PlayAsync() => Assert.True((await Player.PlayAsync()).IsSuccess);

        /// <summary>How many items each sink has had, to wait past.</summary>
        public (int Video, int Audio) Mark() => (Video.Count, Audio.Count);

        /// <summary>
        /// Waits until each sink has been handed an item at or past <paramref name="pts"/> since
        /// <paramref name="skip"/>, on a timeline that items before the mark were not on when
        /// <paramref name="newTimeline"/> is set.
        /// </summary>
        public async Task WhenBothReach(TimeSpan pts, (int Video, int Audio) skip, bool newTimeline = false)
        {
            var seenVideo = newTimeline ? Video.Items.Take(skip.Video).Select(i => i.Timeline).ToHashSet() : [];
            var seenAudio = newTimeline ? Audio.Items.Take(skip.Audio).Select(i => i.Timeline).ToHashSet() : [];
            try
            {
                await Task.WhenAll(
                        Video.WhenDelivered(i => i.Pts >= pts && !seenVideo.Contains(i.Timeline), skip.Video),
                        WithAudio
                            ? Audio.WhenDelivered(i => i.Pts >= pts && !seenAudio.Contains(i.Timeline), skip.Audio)
                            : Task.CompletedTask
                    )
                    .WaitAsync(Bound);
            }
            catch (TimeoutException)
            {
                // Say what did arrive, so a run that stalled reads differently from one that leaked.
                Assert.Fail(
                    $"the sinks did not reach {pts} within {Bound}. Since the mark, video: {Since(Video, skip.Video)}; "
                        + $"audio: {(WithAudio ? Since(Audio, skip.Audio) : "none")}"
                );
            }
        }

        private static string Since(StampRecorder recorder, int skip)
        {
            var items = recorder.Items.Skip(skip).ToList();
            if (items.Count == 0)
                return "nothing";
            var segments = new List<Segment>();
            foreach (var item in items)
            {
                if (segments.Count > 0 && segments[^1].Timeline == item.Timeline)
                    ((List<TimeSpan>)segments[^1].Pts).Add(item.Pts);
                else
                    segments.Add(new Segment(item.Timeline, new List<TimeSpan> { item.Pts }));
            }
            return string.Join(", ", segments.Select(Describe));
        }

        public (IReadOnlyList<Segment> Video, IReadOnlyList<Segment> Audio) Segments() =>
            (
                AssertEachBreakStartsANewTimeline(Video.Items, "video"),
                WithAudio ? AssertEachBreakStartsANewTimeline(Audio.Items, "audio") : []
            );

        public ValueTask DisposeAsync() => Player.DisposeAsync();
    }
}
