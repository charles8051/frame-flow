using FrameFlow.Decoding;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A container whose timestamps do not start at zero plays on the same timeline as one
/// that does (#358).
/// </summary>
/// <remarks>
/// <para>
/// Containers are not required to start at zero, and MPEG-TS conventionally starts at
/// 1.4s. Everything above the demuxer works in media time, which does start at zero, and
/// nothing converted between the two: frames arrived carrying container timestamps a
/// second and a half ahead of a clock seated at zero, so playback held them, presented 7
/// of 20, ran its position past the clip's own duration and never reached
/// <see cref="PlaybackState.Ended"/>.
/// </para>
/// <para>
/// The two errors partially cancel, which is what makes this easy to assert vacuously. A
/// seek to a position lands 1.4s early in the media <i>and</i> the frames there report
/// timestamps 1.4s late, so "the frame after seeking to 2.5s is at about 2.5s" passes on
/// the broken build. The assertions below are the ones that do not cancel: where the
/// first frame sits, whether the last frame is inside the duration the container
/// reported, and whether every frame arrives at all.
/// </para>
/// <para>
/// The arithmetic itself is <c>MediaTimeOriginTests</c>, which needs no container. This
/// covers the part that only a real file can answer: that the offset is read from the
/// container, applied to the packets, and inverted for seeks.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(ContentCaptureCollection.Name)]
public sealed class StartTimeOffsetTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string OffsetClip = "test-video-h264-start-offset.ts";
    private const string HowToGenerate = "Run: dotnet run scripts/generate-test-corpus.cs";

    [RequiresCorpusFileFact(OffsetClip, HowToGenerate)]
    public async Task NonZeroStartTime_PlaysTheWholeClipOnAZeroBasedTimeline()
    {
        var expectation = IntegrationTestHelper.GetCorpusExpectation(OffsetClip);
        Assert.True(expectation is not null, $"No corpus expectation for {OffsetClip}.");
        var expectedFrames = expectation!.ExpectedVideoFrames!.Value;

        var capture = await PlaybackHarness.PlayCorpusFileAsync(OffsetClip);

        Assert.True(
            capture.LoadResult.IsSuccess,
            $"LoadAsync failed: {capture.LoadResult.Error?.Message}"
        );
        Assert.True(
            capture.PlayResult.IsSuccess,
            $"PlayAsync failed: {capture.PlayResult.Error?.Message}"
        );

        // Reaching Ended at all is the headline symptom: the run used to sit in Playing
        // indefinitely with its position climbing past the duration.
        Assert.Equal(PlaybackState.Ended, capture.FinalState);

        var pipeline = capture.Diagnostics.Pipeline;
        var decoder = pipeline.Stream.VideoDecoder;

        // Video-only clip, so a packet is a frame and the accounting below is sound.
        Assert.True(
            pipeline.Stream.Demux.PacketsRead == expectedFrames,
            $"demuxed {pipeline.Stream.Demux.PacketsRead} packets from a video-only clip of "
                + $"{expectedFrames} frames, so the accounting below would not be sound."
        );

        var accounted =
            decoder.FramesDecoded
            + decoder.PacketsDroppedForBackpressure
            + decoder.PacketsDroppedToGopResync;
        Assert.True(
            accounted == expectedFrames,
            $"{expectedFrames} frames in the file, {accounted} accounted for — "
                + $"{decoder.FramesDecoded} decoded, {decoder.PacketsDroppedForBackpressure} "
                + $"shed, {decoder.PacketsDroppedToGopResync} dropped to GOP resync."
        );

        Assert.NotEmpty(capture.Video);

        // The clip opens at the start of its own timeline, not 1.4s into it. This is the
        // assertion the container offset fails directly.
        var first = capture.Video[0].Pts;
        Assert.True(
            first < TimeSpan.FromMilliseconds(250),
            $"first frame presented at {first.TotalSeconds:F3}s. A container that starts at "
                + "1.4s is being played on its own timestamps instead of media time, so the "
                + "clock spends the opening of every clip waiting."
        );

        // And it closes inside the duration it reported. Container timestamps overshoot by
        // exactly the offset, which is what drove the position past the end.
        var last = capture.Video[^1].Pts;
        Assert.True(
            last <= capture.Diagnostics.Duration,
            $"last frame presented at {last.TotalSeconds:F3}s in a clip of "
                + $"{capture.Diagnostics.Duration.TotalSeconds:F3}s."
        );
    }

    /// <summary>
    /// Seeking is the same conversion in the other direction, so it has to be the exact
    /// inverse or a seek lands somewhere other than where it was asked for.
    /// </summary>
    /// <remarks>
    /// The witness here is the timeline the resulting frames land on rather than the
    /// position they land at. On the broken build the seek went 1.4s too early and the
    /// frames it reached reported timestamps 1.4s too late, so the arrival position looked
    /// right while the frames ran past the end of the clip.
    /// </remarks>
    [RequiresCorpusFileFact(OffsetClip, HowToGenerate)]
    public async Task SeekingIntoTheClip_StaysWithinItsTimeline()
    {
        var target = TimeSpan.FromSeconds(2);
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile(OffsetClip)!
            );
            Assert.True((await controller.LoadAsync(source)).IsSuccess);

            // Seek before the first play: the deterministic branch, with no race between
            // the seek and frames already in flight.
            Assert.True((await controller.SeekAsync(target)).IsSuccess);

            // Barrier on the controller's own end-of-playback transition rather than a
            // duration, and subscribe before Play so the transition cannot be missed.
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(transition =>
                {
                    if (
                        transition.Current
                        is PlaybackState.Ended
                            or PlaybackState.Unloaded
                            or PlaybackState.Error
                    )
                        ended.TrySetResult();
                })
            );

            Assert.True((await controller.PlayAsync()).IsSuccess);

            var duration = controller.GetDiagnostics().Duration;

            // The timeout only bounds a failure; the test stays correct if it were far
            // longer, since the assertion reads what the barrier above waited for.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ended.Task.WaitAsync(cts.Token);

            Assert.Equal(PlaybackState.Ended, controller.State);

            var presented = video.PresentedPts;
            Assert.NotEmpty(presented);

            Assert.All(
                presented,
                pts =>
                    Assert.True(
                        pts <= duration,
                        $"a frame at {pts.TotalSeconds:F3}s presented from a clip of "
                            + $"{duration.TotalSeconds:F3}s after seeking to "
                            + $"{target.TotalSeconds:F1}s. The seek and the frame timestamps "
                            + "are on different timelines."
                    )
            );

            // The seek went forward, to the keyframe at or before the target rather than
            // back to the start of the clip.
            Assert.True(
                presented[^1] >= target,
                $"playback ended at {presented[^1].TotalSeconds:F3}s without reaching the "
                    + $"{target.TotalSeconds:F1}s it was seeked to."
            );
        }
    }
}
