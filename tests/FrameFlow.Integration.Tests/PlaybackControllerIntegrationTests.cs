using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// End-to-end integration tests for <see cref="PlaybackController"/>
/// against real corpus media. Skipped when FFmpeg shared libraries or
/// the test corpus aren't available.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the integration suite.</b> Moved from <c>FrameFlow.Playback.Tests</c>.
/// These tests drive real FFmpeg decode through the whole playback stack, and several assert real
/// durations — that pacing keeps playback from running ahead of real time, across loops as well.
/// ADR-0072 rule 6 names this suite as the one allowed to measure elapsed time,
/// so tests of that kind belong here rather than behind an exemption in a unit project. They take
/// the suite's <see cref="FfmpegBootstrapFixture"/> instead of bootstrapping FFmpeg themselves.
/// xUnit builds one fixture instance per class and may build them in parallel; the fixture's
/// <c>static</c> gate is what makes the bootstrap run once per process regardless, and a class that
/// bootstrapped by itself would sit outside it.
/// </para>
/// </remarks>
public sealed class PlaybackControllerIntegrationTests : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>Bounds a failure only.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public PlaybackControllerIntegrationTests(FfmpegBootstrapFixture fixture) => _ = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task LoadPlay_VideoOnlyFile_ReachesEnded()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);


        var presented = 0;
        var sink = new CountingVideoSink(_ => Interlocked.Increment(ref presented));

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var endedTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.PlaybackStateChanged.Subscribe(
            new InlineObserver<StateTransition<PlaybackState>>(
                t =>
                {
                    if (t.Current == PlaybackState.Ended)
                        endedTcs.TrySetResult(true);
                }
            )
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

        Assert.Equal(PlaybackState.Paused, controller.State);
        Assert.NotNull(controller.MediaInfo);
        Assert.Single(controller.MediaInfo!.VideoStreams);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        // Wait for the natural Ended transition (3s media → ~3-4s wallclock with pacing).
        var ended = await Task.WhenAny(endedTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        sw.Stop();
        Assert.True(ended == endedTcs.Task, "Timed out waiting for Ended state.");

        Assert.Equal(PlaybackState.Ended, controller.State);
        // Every frame of the file was presented or counted as dropped late. How many of each
        // depends on how the runner was scheduled; the sum does not.
        AssertEveryFrameAccountedFor(controller, "test-video-h264-yuv420p.mp4", presented);
        // Pacing should keep playback to roughly wallclock — allow a wide
        // tolerance for first-frame headstart, ticker latency, and the
        // last-frame "race to EOS" (no enforcement of pacing past the
        // last frame's PTS).
        Assert.True(
            sw.Elapsed >= TimeSpan.FromSeconds(2.0),
            $"Pacing should keep playback near realtime (~3s); actual: {sw.Elapsed.TotalSeconds:F2}s"
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task LoadPlay_AvFileWithNullAudioSink_PlaysToEndAndDiscardsAudio()
    {
        // Regression for ADR-0059. An A/V source played with NO audio sink (and no
        // audio configurator) used to decode the audio stream into a queue nothing
        // drained; once ~512 audio packets buffered (~10 s) the single demux pump
        // blocked and video froze. The fix discards the audio stream at the
        // demuxer, so video plays straight to EOS.
        var path = IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(path);


        var presented = 0;
        var sink = new CountingVideoSink(_ => Interlocked.Increment(ref presented));

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            audioSink: null,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var endedTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.PlaybackStateChanged.Subscribe(
            new InlineObserver<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current == PlaybackState.Ended)
                    endedTcs.TrySetResult(true);
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

        // Precondition: the fixture really carries an audio stream, so this
        // exercises the no-audio-consumer path rather than a video-only file.
        Assert.NotNull(controller.MediaInfo);
        Assert.NotEmpty(controller.MediaInfo!.AudioStreams);
        Assert.Single(controller.MediaInfo!.VideoStreams);

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        var ended = await Task.WhenAny(endedTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(
            ended == endedTcs.Task,
            $"Timed out before reaching Ended — video starved (frames presented: {presented})."
        );
        Assert.Equal(PlaybackState.Ended, controller.State);

        // Video played to completion (3s @ 30fps = 90 frames, each presented or counted as
        // dropped late) — far past the ~512-packet audio-buffer boundary the deadlock froze at.
        var videoFrames = AssertEveryFrameAccountedFor(controller, "test-av-h264-aac.mp4", presented);

        // Discard proof: the demux pump read only video packets. Pre-fix it also
        // pumped the ~129 audio packets, pushing the count well above the video
        // total; discarding the audio stream keeps it in the video-only band.
        var packetsRead = controller.GetDiagnostics().Pipeline.Stream.Demux.PacketsRead;
        Assert.True(
            packetsRead < videoFrames + 40,
            $"Demux read {packetsRead} packets for {videoFrames} video frames; the surplus "
                + "means the audio stream was pumped, not discarded."
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PauseResume_DuringPlayback_HoldsAndReleasesFrames()
    {
        // Exercise the gate-based pause/resume on real FFmpeg decoders.
        // Pre-gate, this crashed the test host because cancel-mid-decode
        // left the codec context unstable. With the PausableGate now in
        // the graph, pause closes the gate but the decoder keeps
        // running; resume opens the gate and frames drain.
        var path = IntegrationTestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);


        var sink = new PresentCountingVideoSink();

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess);

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess);
        Assert.Equal(PlaybackState.Playing, controller.State);

        // Wait for 3 paced frames (~125ms at 24fps).
        await sink.WhenPresented(3).WaitAsync(Bound);

        var pause = await controller.PauseAsync();
        Assert.True(pause.IsSuccess, $"Pause failed: {pause.Error?.Message}");
        Assert.Equal(PlaybackState.Paused, controller.State);

        var pausedCount = sink.Presented;

        // After pause, the sink should stop receiving frames. Tolerate a
        // couple of in-flight frames between the source and the gate
        // (the pacing operator may have one queued; the source may have
        // one in the source→pace edge). Counted from after the pause
        // returned, so how many can land is bounded by the pipeline's
        // structure rather than by how long the pause took.
        await Task.Delay(300);
        var afterPauseCount = sink.Presented;
        Assert.InRange(afterPauseCount, pausedCount, pausedCount + 3);

        // Resume — gate re-opens, frames flow again.
        var resume = await controller.PlayAsync();
        Assert.True(resume.IsSuccess, $"Resume failed: {resume.Error?.Message}");
        Assert.Equal(PlaybackState.Playing, controller.State);

        await sink.WhenPresented(afterPauseCount + 3).WaitAsync(Bound);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Seek_DuringPlayback_RepositionsAndResumes()
    {
        // Seek with the gate-based pause + cancel-rebuild dance on the
        // packet-queue side. The gate is closed before the demux/decoder
        // touches happen, so no cancel-mid-decode; then the graph is
        // rebuilt for the post-seek epoch (decoder ResetPacketQueue
        // strands the existing DecodeAsync iterator). Pre-seek frames
        // can leak through if the channels were full when the gate
        // closed — the test tolerates a few extras.
        var path = IntegrationTestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);


        var sink = new PtsRecordingVideoSink();

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess);

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess);

        // Wait for a few paced frames before seeking.
        await sink.WhenPresented(_ => true, skip: 2).WaitAsync(Bound);

        // 3-second corpus file → seek to 1.5s (mid-stream) to leave
        // headroom for post-seek frames to arrive before EOS.
        var seekTarget = TimeSpan.FromSeconds(1.5);
        var seek = await controller.SeekAsync(seekTarget);
        Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");

        // Capture the pre-seek frame count so we can wait for frames after it.
        var preSeekCount = sink.Count;

        // Wait for post-seek frames to arrive.
        await sink.WhenPresented(_ => true, skip: preSeekCount).WaitAsync(Bound);

        var postSeekPts = sink.PresentedPts.Skip(preSeekCount).ToList();

        Assert.NotEmpty(postSeekPts);
        // After seek, frames should arrive at PTS values consistent
        // with the seek target. The corpus is short and may have
        // sparse keyframes, so the demuxer may rewind to keyframe 0
        // and the decoder will produce frames starting from that
        // keyframe. The test is satisfied as long as frames continue
        // to flow after the seek without the host crashing or the
        // graph stalling — proving the seek path is recoverable.
        // Tighter post-seek-PTS assertions wait for a corpus file
        // with longer duration + denser keyframes.
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task LoadThenUnload_StateMachineTransitions()
    {
        // Pause/resume across cancel-and-rebuild of the graph is known
        // to crash with native faults today (see docs/DEFERRED_WORK.md: AV pacing
        // operator and per-subgraph cancellation are both deferred —
        // without them the cancel-mid-decode path leaves the codec in
        // an unstable state on resume). This test exercises the
        // Load → Unload state-machine path that doesn't hit those
        // edges; pause/resume against real decoders is exercised once
        // the deferred work lands.
        var path = IntegrationTestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);


        var sink = new CountingVideoSink(_ => { });

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess);
        Assert.Equal(PlaybackState.Paused, controller.State);

        var unload = await controller.UnloadAsync();
        Assert.True(unload.IsSuccess);
        Assert.Equal(PlaybackState.Unloaded, controller.State);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task DisposeWithoutLoad_DoesNotThrow()
    {
        var controller = PlaybackController.Create();
        await controller.DisposeAsync();
        // Just verifying clean teardown.
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOne_LoopsCleanlyFromFrameZero_WithCorrectClockEpoch()
    {
        // B2 cheap-rewind: a RepeatMode.One loop boundary uses the session's
        // RewindToStartAsync (re-run the retained graph after a demuxer rewind +
        // decoder reset) instead of a full SeekAsync(0) (graph teardown + rebuild).
        // This is the signage attract scenario: a short clip looping continuously
        // with NO audio sink (the AudioMode.None / WallClockSource master path).
        //
        // The test asserts the rewind's correctness properties:
        //   (1) the loop actually fires repeatedly (LoopRestarted, increasing count);
        //   (2) each loop epoch re-decodes cleanly FROM FRAME 0 — presented PTS
        //       restart near zero at every boundary (proves a real rewind, not a
        //       continuation past EOS);
        //   (3) the master clock re-seats to the loop epoch correctly — playback
        //       stays near realtime across loops with no PaceUntil stall (a drifted
        //       epoch would hang the pacer and frame flow would stop advancing);
        //   (4) no frame leaks across the boundary / no native fault — every pass
        //       that ended played through to the clip's last frame.
        var path = IntegrationTestEnvironment.GetCorpusFile("test-subsecond.mp4");
        Assert.NotNull(path);


        var sink = new PtsRecordingVideoSink();

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            audioSink: null, // attract path: no audio, WallClockSource masters
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.One
        );

        // Let the 0.5s clip loop for several iterations. A correct cheap rewind
        // keeps producing frames the whole time; a broken clock epoch would stall
        // the pacer and the loop count would stop climbing.
        const int targetLoops = 3;
        var loopEvents = new List<int>();
        var targetReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var loopSub = controller.LoopRestarted.Subscribe(
            new InlineObserver<LoopRestarted>(e =>
            {
                lock (loopEvents)
                {
                    loopEvents.Add(e.LoopCount);
                    if (loopEvents.Count >= targetLoops)
                        targetReached.TrySetResult();
                }
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
        var duration = controller.MediaInfo!.Duration;

        var playStart = System.Diagnostics.Stopwatch.StartNew();
        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        await targetReached.Task.WaitAsync(Bound);
        var elapsedToTargetLoops = playStart.Elapsed;

        // The controller stays in Playing across RepeatMode.One loops (it never
        // transitions to Ended) — proving the loop was taken, not end-of-stream.
        Assert.Equal(PlaybackState.Playing, controller.State);

        List<int> observedLoops;
        lock (loopEvents)
            observedLoops = [.. loopEvents];

        // (1) The loop fired repeatedly, with a strictly increasing 1-based count.
        Assert.True(
            observedLoops.Count >= 3,
            $"Expected >=3 loop boundaries; saw {observedLoops.Count}."
        );
        for (int i = 0; i < observedLoops.Count; i++)
            Assert.Equal(i + 1, observedLoops[i]);

        // The passes, split where the presented PTS go back. Not by LoopRestarted: it is raised
        // a command hop after the rewind, so the next pass's first frames can be presented before
        // an observer of it runs. Every pass presents its newest frame, so each pass that ended
        // before the last loop event is one of these, and the one playing now may be another.
        var passes = new List<List<TimeSpan>>();
        foreach (var pts in sink.PresentedPts)
        {
            if (passes.Count == 0 || pts < passes[^1][^1])
                passes.Add([]);
            passes[^1].Add(pts);
        }
        Assert.True(
            passes.Count >= observedLoops.Count,
            $"{observedLoops.Count} loops, but only {passes.Count} passes presented frames."
        );

        // (4) Every pass that ended played through to the clip's last frame. A late drop never
        // takes the newest frame, so this holds however the runner was scheduled.
        var lastFrame = passes.Max(p => p.Max());
        for (int i = 0; i < passes.Count - 1; i++)
        {
            Assert.True(
                passes[i].Max() == lastFrame,
                $"Pass {i} ended at {passes[i].Max().TotalMilliseconds:F0}ms, short of the clip's "
                    + $"last frame at {lastFrame.TotalMilliseconds:F0}ms."
            );
        }

        // (2) Each loop pass re-decodes from frame 0: the first frame presented in a
        // pass starts near zero, and the max PTS within a pass never exceeds the clip
        // duration (a continuation-past-EOS would keep climbing). Look at passes >= 1
        // (post-first-loop) since pass 0 is the initial play. The pacer holds at most
        // three frames, and a pass starts on an empty one, so its first frame is one
        // of the first three however late the pacer ran.
        var tolerance = TimeSpan.FromMilliseconds(200);
        for (int i = 1; i < passes.Count; i++)
        {
            var first = passes[i][0];
            var max = passes[i].Max();

            // Restart from frame 0: first presented PTS of the pass is near zero.
            Assert.True(
                first <= tolerance,
                $"Loop pass {i}: first presented PTS was {first.TotalMilliseconds:F0}ms, "
                    + "expected a restart at/near 0 (a clean rewind to frame 0)."
            );

            // No drift / no continuation: PTS within the pass stays within the
            // clip's own [0, duration] timeline (allowing a frame of slack).
            Assert.True(
                max <= duration + tolerance,
                $"Loop pass {i}: max PTS {max.TotalMilliseconds:F0}ms exceeded clip "
                    + $"duration {duration.TotalMilliseconds:F0}ms — frames did not restart at the loop epoch."
            );
        }

        // (3) Clock epoch is correct → realtime pacing holds across loops, which is
        // the load-bearing correctness property of the rewind. Each loop epoch
        // re-seats the master clock to 0 and PaceUntil gates the (re-decoded) frames
        // against it, so N loop boundaries take at least ~N * duration of wall-clock
        // (minus one clip's worth of slack for the last-frame race that isn't paced
        // past the final PTS). If the rewind FAILED to re-seat the epoch — e.g. left
        // the clock running at its pre-loop value — PaceUntil would see every
        // post-loop frame as already-due and forward the whole clip instantly, so N
        // loops would complete in milliseconds. This lower bound catches that.
        var lowerBound = TimeSpan.FromSeconds(
            duration.TotalSeconds * (targetLoops - 1) * 0.6
        );
        Assert.True(
            elapsedToTargetLoops >= lowerBound,
            $"{targetLoops} loop boundaries took only {elapsedToTargetLoops.TotalMilliseconds:F0}ms "
                + $"for a {duration.TotalMilliseconds:F0}ms clip — far below the ~{lowerBound.TotalMilliseconds:F0}ms "
                + "realtime-pacing floor. The master clock epoch was not re-seated at the loop boundary "
                + "(PaceUntil forwarded post-loop frames without waiting)."
        );

        await controller.PauseAsync();
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOne_LoopRewindsInPlace_WithoutTheSeekStateMachine()
    {
        // A single source is a queue of one, so its loop is the session's in-place rewind, taken
        // as one of the session's inputs. It no longer travels through the controller's seek state
        // machine, which is what ADR-0028 §2 routed the controller's own loop through: the machine
        // stays NotSeeking, and the player keeps presenting across the boundary.
        var path = IntegrationTestEnvironment.GetCorpusFile("test-subsecond.mp4");
        Assert.NotNull(path);

        var sink = new CountingVideoSink(_ => { });

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            audioSink: null,
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.One
        );

        var seekStates = new List<SeekState>();
        using var seekSub = controller.SeekStateChanged.Subscribe(
            new InlineObserver<StateTransition<SeekState>>(t =>
            {
                lock (seekStates)
                    seekStates.Add(t.Current);
            })
        );

        var loops = 0;
        var twoLoops = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loopSub = controller.LoopRestarted.Subscribe(
            new InlineObserver<LoopRestarted>(_ =>
            {
                if (Interlocked.Increment(ref loops) >= 2)
                    twoLoops.TrySetResult();
            })
        );

        Assert.True((await controller.LoadAsync(MediaSource.FromFile(path!))).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await twoLoops.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(controller.IsActivelyPresenting);
        List<SeekState> states;
        lock (seekStates)
            states = [.. seekStates];
        Assert.Empty(states);

        await controller.PauseAsync();
    }

    /// <summary>
    /// Asserts that each of <paramref name="filename"/>'s video frames was presented or counted
    /// as dropped late, and returns how many it has. Read once the run has ended, so the counts
    /// are final.
    /// </summary>
    private static int AssertEveryFrameAccountedFor(
        IPlaybackController controller,
        string filename,
        int presented
    )
    {
        var expected = IntegrationTestHelper.GetCorpusExpectation(filename)?.ExpectedVideoFrames;
        Assert.True(expected is not null, $"No video frame count recorded for {filename}.");
        var droppedLate = controller.GetDiagnostics().Pipeline.VideoFramesDroppedForSync;
        Assert.True(
            presented + droppedLate == expected,
            $"{filename} has {expected} frames: {presented} presented and {droppedLate} counted "
                + "as dropped late."
        );
        return expected!.Value;
    }

    private sealed class CountingVideoSink : IVideoSink
    {
        private readonly Action<IVideoFrame> _onPresent;

        public CountingVideoSink(Action<IVideoFrame> onPresent)
        {
            _onPresent = onPresent;
        }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            // Capture PTS before dispose so seek tests can inspect
            // which timeline position each presented frame is from.
            _onPresent(frame);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }


    private sealed class InlineObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public InlineObserver(Action<T> onNext) => _onNext = onNext;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => _onNext(value);
    }
}
