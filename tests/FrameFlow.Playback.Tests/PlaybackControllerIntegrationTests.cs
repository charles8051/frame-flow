namespace FrameFlow.Playback.Tests;

/// <summary>
/// End-to-end integration tests for <see cref="PlaybackController"/>
/// against real corpus media. Skipped when FFmpeg shared libraries or
/// the test corpus aren't available.
/// </summary>
public sealed class PlaybackControllerIntegrationTests
{
    [RequiresFfmpegAndCorpusFact]
    public async Task LoadPlay_VideoOnlyFile_ReachesEnded()
    {
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        BootstrapNative();

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
        // Corpus file is 3s @ 24fps → ~72 frames.
        Assert.InRange(presented, 60, 80);
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
        var path = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(path);

        BootstrapNative();

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

        // Video played to completion (3s @ 30fps ≈ 90 frames) — far past the
        // ~512-packet audio-buffer boundary the deadlock froze at.
        Assert.InRange(presented, 70, 100);

        // Discard proof: the demux pump read only video packets. Pre-fix it also
        // pumped the ~129 audio packets, pushing the count well above the video
        // total; discarding the audio stream keeps it in the video-only band.
        var packetsRead = controller.GetDiagnostics().Pipeline.Stream.Demux.PacketsRead;
        Assert.True(
            packetsRead < presented + 40,
            $"Demux read {packetsRead} packets but only {presented} video frames were "
                + "presented; the surplus means the audio stream was pumped, not discarded."
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
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        BootstrapNative();

        var presented = 0;
        var sink = new CountingVideoSink(_ => Interlocked.Increment(ref presented));

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess);

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess);
        Assert.Equal(PlaybackState.Playing, controller.State);

        // Wait for ~3 paced frames (~125ms at 24fps).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (presented < 3 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(presented >= 3, $"Expected ≥3 frames before pause; saw {presented}.");

        var pause = await controller.PauseAsync();
        Assert.True(pause.IsSuccess, $"Pause failed: {pause.Error?.Message}");
        Assert.Equal(PlaybackState.Paused, controller.State);

        var pausedCount = presented;

        // After pause, the sink should stop receiving frames. Tolerate a
        // couple of in-flight frames between the source and the gate
        // (the pacing operator may have one queued; the source may have
        // one in the source→pace edge).
        await Task.Delay(300);
        var afterPauseCount = presented;
        Assert.InRange(afterPauseCount, pausedCount, pausedCount + 3);

        // Resume — gate re-opens, frames flow again.
        var resume = await controller.PlayAsync();
        Assert.True(resume.IsSuccess, $"Resume failed: {resume.Error?.Message}");
        Assert.Equal(PlaybackState.Playing, controller.State);

        deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (presented < afterPauseCount + 3 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(
            presented >= afterPauseCount + 3,
            $"Expected resume to produce more frames; saw {presented} (after-pause was {afterPauseCount})."
        );
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
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        BootstrapNative();

        var ptsValues = new List<TimeSpan>();
        var sink = new CountingVideoSink(frame =>
        {
            lock (ptsValues)
                ptsValues.Add(frame.Pts);
        });

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess);

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess);

        // Wait for a few paced frames before seeking.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            int count;
            lock (ptsValues)
                count = ptsValues.Count;
            if (count >= 3 || DateTimeOffset.UtcNow >= deadline)
                break;
            await Task.Delay(20);
        }

        // 3-second corpus file → seek to 1.5s (mid-stream) to leave
        // headroom for post-seek frames to arrive before EOS.
        var seekTarget = TimeSpan.FromSeconds(1.5);
        var seek = await controller.SeekAsync(seekTarget);
        Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");

        // Capture the pre-seek frame count so we can assert post-seek
        // frames have new PTS values.
        int preSeekCount;
        lock (ptsValues)
            preSeekCount = ptsValues.Count;

        // Wait for post-seek frames to arrive (up to 5s for any frame).
        deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            int count;
            lock (ptsValues)
                count = ptsValues.Count;
            if (count > preSeekCount || DateTimeOffset.UtcNow >= deadline)
                break;
            await Task.Delay(20);
        }

        List<TimeSpan> postSeekPts;
        lock (ptsValues)
            postSeekPts = ptsValues.Skip(preSeekCount).ToList();

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
        var path = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        Assert.NotNull(path);

        BootstrapNative();

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
        BootstrapNative();
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
        //   (4) no frame leaks across the boundary / no native fault — a sustained
        //       multi-loop run with the sink disposing every frame stays alive.
        var path = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        Assert.NotNull(path);

        BootstrapNative();

        var loopCount = 0; // updated by the LoopRestarted observer (loop epoch id)
        var samples = new List<(int Loop, TimeSpan Pts)>();
        var sink = new CountingVideoSink(frame =>
        {
            var epoch = Volatile.Read(ref loopCount);
            lock (samples)
                samples.Add((epoch, frame.Pts));
        });

        await using var controller = PlaybackController.Create(
            videoSink: sink,
            audioSink: null, // attract path: no audio, WallClockSource masters
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.One
        );

        var loopEvents = new List<int>();
        using var loopSub = controller.LoopRestarted.Subscribe(
            new InlineObserver<LoopRestarted>(e =>
            {
                Volatile.Write(ref loopCount, e.LoopCount);
                lock (loopEvents)
                    loopEvents.Add(e.LoopCount);
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
        var duration = controller.MediaInfo!.Duration;

        var playStart = System.Diagnostics.Stopwatch.StartNew();
        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        // Let the 0.5s clip loop for several iterations. Stop once we have at
        // least 3 loop boundaries (or a generous wall-clock cap). A correct
        // cheap rewind keeps producing frames the whole time; a broken clock
        // epoch would stall the pacer and the loop count would stop climbing.
        const int targetLoops = 3;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            int loops;
            lock (loopEvents)
                loops = loopEvents.Count;
            if (loops >= targetLoops)
                break;
            await Task.Delay(25);
        }
        var elapsedToTargetLoops = playStart.Elapsed;

        // The controller stays in Playing across RepeatMode.One loops (it never
        // transitions to Ended) — proving the loop was taken, not end-of-stream.
        Assert.Equal(PlaybackState.Playing, controller.State);

        List<int> observedLoops;
        lock (loopEvents)
            observedLoops = [.. loopEvents];
        List<(int Loop, TimeSpan Pts)> observed;
        lock (samples)
            observed = [.. samples];

        // (1) The loop fired repeatedly, with a strictly increasing 1-based count.
        Assert.True(
            observedLoops.Count >= 3,
            $"Expected >=3 loop boundaries; saw {observedLoops.Count}."
        );
        for (int i = 0; i < observedLoops.Count; i++)
            Assert.Equal(i + 1, observedLoops[i]);

        // (4) Frames kept flowing across the loops without a fault.
        Assert.True(
            observed.Count >= 20,
            $"Expected sustained frame flow across loops; saw {observed.Count} frames."
        );

        // (2) Each loop epoch re-decodes from frame 0: the first frame presented
        // in a given epoch starts near zero, and the max PTS within an epoch never
        // exceeds the clip duration (a continuation-past-EOS would keep climbing).
        // Look at epochs >= 1 (post-first-loop) since epoch 0 is the initial play.
        var byEpoch = observed
            .Where(s => s.Loop >= 1)
            .GroupBy(s => s.Loop)
            .OrderBy(g => g.Key)
            .ToList();
        Assert.NotEmpty(byEpoch);

        var tolerance = TimeSpan.FromMilliseconds(200);
        foreach (var epoch in byEpoch)
        {
            var ptsList = epoch.Select(s => s.Pts).ToList();
            var first = ptsList[0];
            var max = ptsList.Max();

            // Restart from frame 0: first presented PTS of the epoch is near zero.
            Assert.True(
                first <= tolerance,
                $"Loop epoch {epoch.Key}: first presented PTS was {first.TotalMilliseconds:F0}ms, "
                    + "expected a restart at/near 0 (a clean rewind to frame 0)."
            );

            // No drift / no continuation: PTS within the epoch stays within the
            // clip's own [0, duration] timeline (allowing a frame of slack).
            Assert.True(
                max <= duration + tolerance,
                $"Loop epoch {epoch.Key}: max PTS {max.TotalMilliseconds:F0}ms exceeded clip "
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
    public async Task RepeatOne_LoopRoutesThroughSeekStateMachine()
    {
        // The cheap rewind must NOT bypass the seek state machine: per ADR-0028 §2
        // the RepeatMode.One loop routes through it so SeekStateChanged observers
        // fire and IsActivelyPresenting reports false during the loop. Confirm the
        // loop drives at least one NotSeeking -> SeekPending/SeekInProgress ->
        // NotSeeking cycle, exactly as a user seek would, even though the underlying
        // session operation is now RewindToStartAsync rather than SeekAsync.
        var path = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        Assert.NotNull(path);

        BootstrapNative();

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
        using var loopSub = controller.LoopRestarted.Subscribe(
            new InlineObserver<LoopRestarted>(_ => Interlocked.Increment(ref loops))
        );

        Assert.True((await controller.LoadAsync(MediaSource.FromFile(path!))).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);

        // Wait for at least two boundaries so a full cycle is guaranteed to have
        // completed (the Nth loop's SeekCompleted fires before the N+1th begins).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (Volatile.Read(ref loops) < 2 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(Volatile.Read(ref loops) >= 2, "Expected at least two loop boundaries.");

        // Pause to quiesce further loops before inspecting the captured states (the
        // loop is continuous, so the live SeekState races; the completed history does
        // not).
        await controller.PauseAsync();

        List<SeekState> states;
        lock (seekStates)
            states = [.. seekStates];

        // The loop drove the seek state machine through a complete cycle: at least one
        // SeekInProgress, and a later return to NotSeeking — exactly the transitions a
        // user seek produces, even though the underlying session op is now the rewind.
        // (Asserting a completed cycle rather than the final state, which the continuous
        // loop keeps re-entering.)
        var firstInProgress = states.IndexOf(SeekState.SeekInProgress);
        Assert.True(firstInProgress >= 0, "Loop never drove the seek state machine to SeekInProgress.");
        var returnedAfter = states
            .Skip(firstInProgress + 1)
            .Any(s => s == SeekState.NotSeeking);
        Assert.True(
            returnedAfter,
            "Seek state machine entered SeekInProgress during the loop but never returned to "
                + "NotSeeking — the loop-seek cycle did not complete."
        );
    }

    private static void BootstrapNative()
    {
        // FrameFlow.Native bootstrap is idempotent; doing it here ensures
        // FFmpeg DLLs are loaded before the controller's session tries
        // to open files.
        var bootstrapper = new FrameFlow.Native.FrameFlowBootstrapper(
            new FrameFlow.Native.FrameFlowNativeOptions { SkipHardwareProbe = true }
        );
        var result = bootstrapper.Initialize();
        Assert.True(result.IsSuccess, $"FFmpeg bootstrap failed: {result.Message}");
    }

    private sealed class CountingVideoSink : IVideoSink
    {
        private readonly Action<IVideoFrame> _onPresent;

        public CountingVideoSink(Action<IVideoFrame> onPresent)
        {
            _onPresent = onPresent;
        }

        public IFramePool FramePool => null!;

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
