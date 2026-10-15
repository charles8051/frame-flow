using System.Collections.Concurrent;
using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Preparing an item ahead (media-clips decision 8) on real media: what is prepared leaves the
/// item that is playing alone, and an advance to it starts on the prepared runtime.
/// </summary>
/// <remarks>
/// <para>
/// The items are separate source objects, so an advance between them opens or promotes a runtime
/// rather than rewinding the open one in place. The fixture is a 10 s 1080p60 file with one
/// keyframe, at zero, so a runtime positioned at 7 s decodes from zero.
/// </para>
/// <para>
/// A promotion is read from the session's own log line, raised as the prepared runtime becomes the
/// current one. The shared audio sink's lifecycle counters are read after the player is disposed,
/// which awaits every prepared runtime's disposal, so a disposal that touched the sink has happened
/// by then.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MediaClipPrepareTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-1080p60-h264-aac.mp4";
    private static readonly TimeSpan HalfFrame = TimeSpan.FromSeconds(1.0 / 120);
    private static readonly TimeSpan PastStart = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestSample = TimeSpan.FromSeconds(1.0 / 8000); // at 8 kHz

    private readonly FfmpegBootstrapFixture _fixture;

    public MediaClipPrepareTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task PreparingItems_LeavesThePlayingItemAlone_AndAJumpStartsOnThePreparedRuntime()
    {
        var a = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(2));
        var b = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(7));
        // c starts on the keyframe, so its first decoded frame is the one it waits at its gate with
        // once it is prepared.
        var c = new MediaClip(Corpus(Fixture));
        var video = new PtsRecordingVideoSink();
        var audio = new HarnessAudioSink();
        var log = new SessionLog();
        var player = await FrameFlowPlayer
            .Create()
            .WithMedia(a)
            .WithVideoSink(video)
            .WithAudioSink(audio)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithLogger(log)
            .BuildPlayerAsync();

        int deactivations;
        await using (player)
        {
            var bItem = await player.AddAsync(b);
            var cItem = await player.AddAsync(c);

            Assert.True((await player.PlayAsync()).IsSuccess);
            await video.WhenPresented(pts => pts >= a.Start + PastStart).WaitAsync(Bound);
            var pauses = audio.PauseCount;
            deactivations = audio.DeactivateCount;
            var before = player.Position;

            // b, then c in its place, then b again: each replaces and disposes the last.
            Assert.True((await player.PrepareAsync(bItem)).IsSuccess);
            Assert.True((await player.PrepareAsync(cItem)).IsSuccess);
            Assert.True((await player.PrepareAsync(bItem)).IsSuccess);

            // Positioned at 7 s and at zero, they moved neither the position clock nor the audio sink.
            var during = player.Position;
            Assert.True(during >= before && during < b.Start, $"the position moved from {before} to {during}");
            Assert.Equal(pauses, audio.PauseCount);
            Assert.DoesNotContain(video.PresentedPts, pts => pts < a.Start - HalfFrame || pts >= b.Start);
            Assert.Equal(0, log.Promotions);

            // Until b's first frame, anything presented after the jump is a playing on until it is
            // disposed, at or past where it had reached. b decodes from its keyframe at zero, so a
            // frame of b from before its start would fall below that.
            var aReached = video.PresentedPts.Max();
            var mark = video.Count;
            var beforeJump = TimeProvider.System.GetUtcNow();
            Assert.True((await player.JumpToAsync(bItem)).IsSuccess);
            await video.WhenPresented(pts => pts >= b.Start + PastStart, skip: mark).WaitAsync(Bound);

            Assert.Equal(1, log.Promotions);
            var beforeB = video.PresentedPts.Skip(mark).TakeWhile(pts => pts < b.Start - HalfFrame).ToList();
            Assert.True(
                beforeB.All(pts => pts >= aReached - HalfFrame),
                $"after the jump, frames fell back below {aReached}: {string.Join(", ", beforeB)}"
            );

            // The shared clocks were seated on b's start when its runtime was attached, which the
            // jump did after it began, so they have run on from there for no longer than the time
            // measured since. That time is read off the position clock's own source, after the
            // position, so the bound holds however long the runner took.
            var position = player.Position;
            var sinceJump = TimeProvider.System.GetUtcNow() - beforeJump;
            Assert.InRange(position, b.Start, b.Start + sinceJump);
        }

        // a's runtime deactivated the sink when the jump disposed it, and b's when the player was
        // disposed. The prepared runtimes that were replaced never activated it, and left it alone.
        Assert.Equal(deactivations + 2, audio.DeactivateCount);

        // Nothing from before a's start reached either sink: not a's decode from the keyframe at
        // zero, and not what c, prepared at zero, held at its gates when it was replaced.
        Assert.DoesNotContain(video.PresentedPts, pts => pts < a.Start - HalfFrame);
        Assert.True(audio.EarliestBlockPts >= a.Start, $"audio from {audio.EarliestBlockPts} was presented");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerThatPreparesItsNextItem_StartsItOnThePreparedRuntime()
    {
        var a = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(9.5));
        var b = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5));
        var video = new PtsRecordingVideoSink();
        var log = new SessionLog();
        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia(a)
            .WithVideoSink(video)
            .WithAudioSink(new HarnessAudioSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithNextItemPrepared()
            .WithLogger(log)
            .BuildPlayerAsync();
        await player.AddAsync(b);

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = player.StateChanged.Subscribe(
            new ActionObserver<PlaybackState>(s =>
            {
                if (s == PlaybackState.Ended)
                    ended.TrySetResult();
            })
        );

        Assert.True((await player.PlayAsync()).IsSuccess);
        await ended.Task.WaitAsync(Bound);

        Assert.Equal(1, log.Promotions);
        Assert.Contains(video.PresentedPts, pts => pts >= b.Start && pts < b.End!.Value);
        Assert.DoesNotContain(
            video.PresentedPts,
            pts => !(pts >= a.Start - HalfFrame && pts < a.End!.Value) && !(pts >= b.Start - HalfFrame && pts < b.End!.Value)
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AClipsDecodeFromItsKeyframe_ReachesNeitherAConfiguratorNorTheAudioSink()
    {
        // The decoder walks from the keyframe at zero. What it decodes before the start is
        // dropped where it is decoded, so it is not held at the gates while the item waits, and
        // a tap the consumer wires ahead of the sink never sees it.
        var clip = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(7));
        var tapped = new ConcurrentQueue<TimeSpan>();
        var video = new PtsRecordingVideoSink();
        var audio = new HarnessAudioSink();
        var player = await FrameFlowPlayer
            .Create()
            .WithMedia(clip)
            .WithVideoSink(video)
            .WithAudioSink(audio)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .ConfigureVideo(chain =>
                chain.Then(
                    new OperatorNode<IVideoFrame, IVideoFrame>(
                        "tap",
                        (frame, _) =>
                        {
                            tapped.Enqueue(frame.Pts);
                            return ValueTask.FromResult<IVideoFrame?>(frame);
                        },
                        holding: FrameHolding.InFlight
                    )
                )
            )
            .BuildPlayerAsync();

        await using (player)
        {
            Assert.True((await player.PlayAsync()).IsSuccess);

            // Frames reach the tap in order, before the sink, so once the sink has a frame past
            // the start, every frame decoded before it has passed the tap. The audio sink's first
            // block is the first the run delivers.
            await video.WhenPresented(pts => pts >= clip.Start + PastStart).WaitAsync(Bound);
            await audio.FirstBlockPresented.WaitAsync(Bound);
        }

        Assert.DoesNotContain(tapped, pts => pts < clip.Start - HalfFrame);

        // The block that crosses the start is cut there rather than dropped, so the audio starts
        // within a sample of it.
        Assert.True(
            audio.EarliestBlockPts >= clip.Start && audio.EarliestBlockPts < clip.Start + LongestSample,
            $"audio from {audio.EarliestBlockPts} was presented"
        );
    }

    private static MediaSource Corpus(string name) =>
        MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(name)!);

    /// <summary>Counts the playlist session's promotions of a prepared runtime.</summary>
    private sealed class SessionLog : ILoggerFactory, ILogger
    {
        private int _promotions;

        public int Promotions => Volatile.Read(ref _promotions);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider) { }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (eventId.Name == "LogPromoted")
                Interlocked.Increment(ref _promotions);
        }

        public void Dispose() { }
    }
}
