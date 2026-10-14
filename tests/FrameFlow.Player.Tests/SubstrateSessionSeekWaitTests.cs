// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using FrameFlow.Playback;
using Step = FrameFlow.Playback.SubstrateSession.Step;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins what a playback session does with the tasks of a run a seek was waiting for when the seek
/// was cancelled (#707). It holds on to them, so the next stop waits for them: the dispose before it
/// frees the pump pipeline, the decoders and the demux session, and a seek that supersedes the
/// cancelled one before it seeks the demuxer, resets the decoders or launches a run of its own. A
/// scrub, which cancels each seek as the next is accepted, ends at its last target with one run
/// replacing another.
/// </summary>
/// <remarks>
/// <para>
/// The run is held by an audio sink that ignores cancellation until the test releases it, as a sink
/// of a caller's own may, so its graph cannot end while a seek waits for it. Audio reaches its sink
/// from inside the graph, where a video frame goes to the pacer first. The order is read from the
/// steps the session reports through <see cref="SubstrateSession.OnStep"/>, and the races are driven
/// by acting at them. Every wait is on a signal the session or a sink raises; the bound only limits a
/// failure.
/// </para>
/// <para>
/// A warm-up made again while the graph waits at the closed gates launches no second run beside it
/// either.
/// </para>
/// <para>
/// Where the run should have ended and has not, at a demuxer seek, a reset or a free, the test
/// records that and then lets the run end before the step goes on, so a regression fails here
/// rather than crashing the test host in native code.
/// </para>
/// </remarks>
public sealed class SubstrateSessionSeekWaitTests
{
    private const string AvFile = "test-av-h264-aac.mp4";
    private const string VideoFile = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly Step[] Teardown =
    [
        Step.DisposingPipeline,
        Step.DisposingVideoDecoder,
        Step.DisposingAudioDecoder,
        Step.DisposingDemux,
    ];

    private static readonly Step[] Reposition = [Step.SeekingDemux, Step.ResettingForSeek];

    [RequiresFfmpegAndCorpusFact]
    public async Task ADisposeAfterASeekWasCancelledWaitingForTheRun_FreesNothingUntilTheRunHasEnded()
    {
        await using var rig = await Rig.PlayingAsync();
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));

        // The dispose waits for the run the seek left, and the run ends once the sink lets go.
        rig.Steps.At(Step.WaitingForRun, rig.Audio.Release);

        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Empty(rig.RunStillGoingAt);
        Assert.Equal(Teardown, rig.Steps.Taken.Where(Teardown.Contains));
        Assert.True(rig.Steps.Quiet, "A task of the run was still going after the dispose returned.");
        Assert.Empty(rig.Faults);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASeekSupersedingOneCancelledWhileWaiting_WaitsForTheRun_BeforeItTouchesTheDemuxer()
    {
        await using var rig = await Rig.PlayingAsync();
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));

        rig.Steps.At(Step.WaitingForRun, rig.Audio.Release);

        await rig.Session.SeekAsync(TimeSpan.FromSeconds(2)).AsTask().WaitAsync(Bound);

        Assert.Empty(rig.RunStillGoingAt);
        var taken = rig.Steps.Taken;
        Assert.Equal(Reposition, taken.Where(Reposition.Contains));
        Assert.Equal(1, rig.Steps.MostPumpsAtOnce);
        Assert.Equal(1, rig.Steps.MostGraphsAtOnce);

        // The superseding seek launched the next run, after it had sought the demuxer.
        var reset = Array.IndexOf(taken, Step.ResettingForSeek);
        Assert.Equal(2, taken.Count(s => s == Step.PumpStarted));
        Assert.True(Array.LastIndexOf(taken, Step.PumpStarted) > reset, "The next run's pump started before the reset.");
        Assert.True(Array.LastIndexOf(taken, Step.GraphStarted) > reset, "The next run's graph started before the reset.");
        Assert.Equal(1, rig.Session.RunNumber);
        Assert.Empty(rig.Faults);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AScrub_EndsAtItsLastTarget_WithOneRunReplacingAnother()
    {
        // Each seek but the last is cancelled while it waits for the run, as a scrub does when it
        // sends the next seek as soon as the last is accepted. Only the last repositions.
        TimeSpan[] superseded = [TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.6)];
        var last = TimeSpan.FromSeconds(2);

        await using var rig = await Rig.PlayingAsync();
        var firstTimeline = await rig.Video.FirstTimeline.WaitAsync(Bound);
        foreach (var target in superseded)
            await rig.SeekCutShortAsync(target);

        var runWhileScrubbing = rig.Session.RunNumber;
        rig.Steps.At(Step.WaitingForRun, rig.Audio.Release);

        await rig.Session.SeekAsync(last).AsTask().WaitAsync(Bound);
        var landed = await rig.Video.FirstFrameAfter(firstTimeline).WaitAsync(Bound);
        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Empty(rig.RunStillGoingAt);
        Assert.Equal(0, runWhileScrubbing); // no seek it superseded waited out the run, so it is still the run
        Assert.Equal(1, rig.Session.RunNumber);
        Assert.True(landed >= last, $"The scrub landed at {landed}, before its last target {last}.");

        var taken = rig.Steps.Taken;
        Assert.Equal(Reposition, taken.Where(Reposition.Contains)); // once, for the last seek
        Assert.Equal(2, taken.Count(s => s == Step.PumpStarted));
        Assert.Equal(2, taken.Count(s => s == Step.GraphStarted));
        Assert.Equal(1, rig.Steps.MostPumpsAtOnce);
        Assert.Equal(1, rig.Steps.MostGraphsAtOnce);
        Assert.True(rig.Steps.Quiet, "A task of a run was still going after the dispose returned.");
        Assert.Empty(rig.Faults);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AWarmUpAgain_AfterThePumpHasReadTheFile_LaunchesNoRunBesideTheGraph()
    {
        // The pump reads a short file to its end during the warm-up, and the graph waits at the
        // closed gates for the play. A run launched now would decode beside that graph on the
        // same decoders.
        await using var rig = await Rig.OpenAsync(VideoFile);
        var pumpEnded = rig.Steps.Next(Step.PumpEnded);
        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        await pumpEnded.WaitAsync(Bound);

        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);

        var taken = rig.Steps.Taken;
        Assert.Equal(1, taken.Count(s => s == Step.PumpStarted));
        Assert.Equal(1, taken.Count(s => s == Step.GraphStarted));
        Assert.Equal(1, rig.Steps.MostGraphsAtOnce);
        Assert.Empty(rig.Faults);
    }

    private static string Corpus(string name) =>
        TestEnvironment.GetCorpusFile(name) ?? throw new InvalidOperationException($"{name} is missing.");

    /// <summary>A session playing <see cref="AvFile"/>, its run held by the audio sink.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Exception> _faults = new();
        private readonly ConcurrentQueue<Step> _stillGoing = new();

        private Rig()
        {
            Session = new SubstrateSession(
                Video,
                Audio,
                new PlaybackClock(),
                new SessionCallbacks(
                    OnEndOfStream: static () => { },
                    OnWorkerFaulted: _faults.Enqueue,
                    OnBufferReady: static () => { },
                    OnBufferUnderrun: static () => { },
                    OnRecoverableError: error => _faults.Enqueue(error.Inner ?? new InvalidOperationException(error.Message)),
                    OnCurrentItemChanged: static _ => { },
                    OnLoopRestarted: static (_, _) => { },
                    OnItemFailed: static (_, _, _) => { }
                ),
                HardwareDecodeMode.Disabled
            );
            Steps = StepLog.On(Session);
            CheckRunEndedAt([.. Reposition, .. Teardown]);
        }

        public RecordingVideoSink Video { get; } = new();

        public HoldingAudioSink Audio { get; } = new();

        public SubstrateSession Session { get; }

        public StepLog Steps { get; }

        public IReadOnlyCollection<Exception> Faults => _faults;

        /// <summary>The steps taken while a task of a run was still going, where none should have been.</summary>
        public IReadOnlyCollection<Step> RunStillGoingAt => _stillGoing;

        /// <summary>A session that has opened <paramref name="file"/>, and launched no run.</summary>
        public static async Task<Rig> OpenAsync(string file)
        {
            var rig = new Rig();
            await rig.Session.InitializeAsync(MediaSource.FromFile(Corpus(file))).AsTask().WaitAsync(Bound);
            return rig;
        }

        public static async Task<Rig> PlayingAsync()
        {
            var rig = await OpenAsync(AvFile);
            await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
            await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);
            await rig.Audio.Entered.WaitAsync(Bound); // the graph is in the sink, and stays there
            return rig;
        }

        /// <summary>
        /// Seeks to <paramref name="target"/>, and cancels the seek once it waits for the run.
        /// </summary>
        public async Task SeekCutShortAsync(TimeSpan target)
        {
            using var cancel = new CancellationTokenSource();
            var waiting = Steps.Next(Step.WaitingForRun);
            var seek = Session.SeekAsync(target, cancel.Token).AsTask();

            var first = await Task.WhenAny(waiting, seek).WaitAsync(Bound);
            Assert.True(first == waiting, $"The seek to {target} did not wait for the run.");

            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek.WaitAsync(Bound));
        }

        /// <summary>
        /// At each of <paramref name="steps"/>, records the step if a task of a run is still going,
        /// then lets the run end and waits for it before the step goes on.
        /// </summary>
        private void CheckRunEndedAt(IEnumerable<Step> steps)
        {
            foreach (var step in steps)
            {
                Steps.At(
                    step,
                    () =>
                    {
                        if (Steps.Quiet)
                            return;
                        _stillGoing.Enqueue(step);
                        Audio.Release();
                        Steps.WhenQuiet.Wait(Bound);
                    }
                );
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Whatever a test left held is let go, so the session's dispose does not wait on it.
            Audio.Release();
            await Session.DisposeAsync().AsTask().WaitAsync(Bound);
            await Video.DisposeAsync();
            await Audio.DisposeAsync();
        }
    }

    /// <summary>
    /// The steps a session takes, in order, with an action to run at any of them, and how many of
    /// its runs' pumps and graphs are going.
    /// </summary>
    private sealed class StepLog
    {
        private readonly Lock _gate = new();
        private readonly List<Step> _taken = [];
        private readonly Dictionary<Step, Action> _actions = [];
        private readonly Dictionary<Step, TaskCompletionSource> _next = [];
        private int _pumps;
        private int _graphs;
        private int _mostPumps;
        private int _mostGraphs;
        private TaskCompletionSource _quiet = Done();

        public static StepLog On(SubstrateSession session)
        {
            var log = new StepLog();
            session.OnStep = log.Take;
            return log;
        }

        public Step[] Taken
        {
            get
            {
                lock (_gate)
                    return [.. _taken];
            }
        }

        /// <summary>Whether no pump and no graph of any run is going.</summary>
        public bool Quiet
        {
            get
            {
                lock (_gate)
                    return _pumps == 0 && _graphs == 0;
            }
        }

        /// <summary>Completes once no pump and no graph is going.</summary>
        public Task WhenQuiet
        {
            get
            {
                lock (_gate)
                    return _quiet.Task;
            }
        }

        public int MostPumpsAtOnce
        {
            get
            {
                lock (_gate)
                    return _mostPumps;
            }
        }

        public int MostGraphsAtOnce
        {
            get
            {
                lock (_gate)
                    return _mostGraphs;
            }
        }

        /// <summary>Runs <paramref name="action"/> each time the session takes <paramref name="step"/>, on its thread.</summary>
        public void At(Step step, Action action)
        {
            lock (_gate)
                _actions[step] = action;
        }

        /// <summary>Completes the next time the session takes <paramref name="step"/>.</summary>
        public Task Next(Step step)
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _next[step] = next;
            return next.Task;
        }

        private void Take(Step step)
        {
            Action? action;
            TaskCompletionSource? next;
            lock (_gate)
            {
                _taken.Add(step);
                switch (step)
                {
                    case Step.PumpStarted:
                        _mostPumps = Math.Max(_mostPumps, ++_pumps);
                        break;
                    case Step.PumpEnded:
                        _pumps--;
                        break;
                    case Step.GraphStarted:
                        _mostGraphs = Math.Max(_mostGraphs, ++_graphs);
                        break;
                    case Step.GraphEnded:
                        _graphs--;
                        break;
                }

                if (_pumps == 0 && _graphs == 0)
                    _quiet.TrySetResult();
                else if (_quiet.Task.IsCompleted)
                    _quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                _actions.TryGetValue(step, out action);
                _next.Remove(step, out next);
            }

            next?.TrySetResult();
            action?.Invoke();
        }

        private static TaskCompletionSource Done()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.TrySetResult();
            return done;
        }
    }

    /// <summary>
    /// An audio sink that holds the first buffer it is given, ignoring cancellation, until it is
    /// released, and holds nothing after that.
    /// </summary>
    private sealed class HoldingAudioSink : IAudioSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holding;

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public async ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            if (Interlocked.Exchange(ref _holding, 1) != 0)
                return;
            _entered.TrySetResult();
            await _released.Task.ConfigureAwait(false);
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A video sink that records the timeline of the first frame, and the first frame of another.</summary>
    private sealed class RecordingVideoSink : IVideoSink
    {
        private readonly TaskCompletionSource<TimelineId> _firstTimeline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<(TimelineId Timeline, TimeSpan Pts)> _frames = new();
        private readonly Lock _gate = new();
        private (TimelineId After, TaskCompletionSource<TimeSpan> Landed)? _waiting;

        public Task<TimelineId> FirstTimeline => _firstTimeline.Task;

        /// <summary>Completes with the presentation time of the first frame on a timeline after <paramref name="timeline"/>.</summary>
        public Task<TimeSpan> FirstFrameAfter(TimelineId timeline)
        {
            var landed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                foreach (var (frameTimeline, pts) in _frames)
                {
                    if (frameTimeline != timeline)
                    {
                        landed.TrySetResult(pts);
                        return landed.Task;
                    }
                }
                _waiting = (timeline, landed);
            }
            return landed.Task;
        }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            var timeline = frame.Timeline;
            var pts = frame.Pts;
            frame.Dispose();
            _firstTimeline.TrySetResult(timeline);
            lock (_gate)
            {
                _frames.Enqueue((timeline, pts));
                if (_waiting is { } waiting && timeline != waiting.After)
                {
                    waiting.Landed.TrySetResult(pts);
                    _waiting = null;
                }
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
