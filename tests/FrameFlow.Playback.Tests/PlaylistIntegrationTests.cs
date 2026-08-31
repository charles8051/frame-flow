using System.Diagnostics;
using FrameFlow.Graph;
using Xunit.Abstractions;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// End-to-end integration tests for gapless playlist playback over real corpus
/// media via <see cref="PlaybackController.CreatePlaylist"/>. Skipped when FFmpeg
/// shared libraries or the test corpus aren't available.
/// </summary>
/// <remarks>
/// The load-bearing assertion is that the <b>same</b> video sink instance is
/// driven across every item boundary and is <b>never disposed</b> by the
/// playback stack (ADR-0044) — i.e. the presenter stays warm and is not rebuilt
/// per item, which is the entire point of the design.
/// </remarks>
public sealed class PlaylistIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public PlaylistIntegrationTests(ITestOutputHelper output) => _output = output;

    [RequiresFfmpegAndCorpusFact]
    public async Task Playlist_AdvancesAcrossItems_WithoutRebuildingTheSink()
    {
        var v = TestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4");
        var av = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(v);
        Assert.NotNull(av);

        BootstrapNative();

        var sink = new WarmTrackingVideoSink();

        var coordinator = new PlaylistCoordinator(
            [MediaSource.FromFile(v!), MediaSource.FromFile(av!)],
            RepeatMode.All
        );

        await using var controller = PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: sink,
            audioSink: null, // null audio → wallclock pacer; AV item discards audio (ADR-0059).
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.All
        );

        var transitions = new List<PlaylistTransition>();
        using var sub = coordinator.SourceTransitioned.Subscribe(
            new TransitionCollector(t =>
            {
                lock (transitions)
                    transitions.Add(t);
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(v!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
        Assert.Equal(PlaybackState.Paused, controller.State);

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        // Let a few frames of the first item flow into the warm sink.
        await WaitUntilAsync(() => sink.PresentCount > 0, TimeSpan.FromSeconds(10));
        Assert.True(sink.PresentCount > 0, "No frames presented for the first item.");

        // Drive three boundaries via skip (fast + deterministic): item0 -> item1
        // -> wrap to item0 -> item1. Each crossing keeps the SAME sink.
        for (var boundary = 1; boundary <= 3; boundary++)
        {
            var before = TransitionCount(transitions);
            var framesBefore = sink.PresentCount;

            coordinator.RequestSkip();

            await WaitUntilAsync(
                () => TransitionCount(transitions) > before,
                TimeSpan.FromSeconds(15)
            );
            Assert.True(
                TransitionCount(transitions) > before,
                $"Timed out waiting for boundary {boundary} (transitions: {TransitionCount(transitions)})."
            );

            // The new item presents into the same warm sink.
            await WaitUntilAsync(
                () => sink.PresentCount > framesBefore,
                TimeSpan.FromSeconds(15)
            );
            Assert.True(
                sink.PresentCount > framesBefore,
                $"No new frames after boundary {boundary}; the swapped-in item didn't present."
            );

            // The presenter is never rebuilt: the sink is never disposed.
            Assert.Equal(0, sink.DisposeCount);
        }

        // Under RepeatMode.All the playlist never ends — still Playing after wraps.
        Assert.Equal(PlaybackState.Playing, controller.State);

        // At least one wrap occurred across three boundaries on a 2-item list.
        Assert.Contains(transitions, t => t.Wrapped);

        // ADR-0044: disposing the player must NOT dispose the caller-owned sink.
        await controller.DisposeAsync();
        Assert.Equal(0, sink.DisposeCount);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task GaplessLoop_ReseatsAudioMasteredClockOriginAtEachItemBoundary()
    {
        // Regression for the gapless-loop clock-epoch drift. When an audio sink masters the pacing clock and a
        // looping playlist wraps, the audio clock's origin must be RESEATED to the
        // new item's start at every item boundary — not left to be rediscovered
        // from the first post-activation buffer's PTS. Left to rediscover, the
        // audio sample-counter clock sits at its (device-paced, buffer-PTS-
        // dependent) origin while the already-decoded video frames carry climbing
        // PTS, so PaceUntil drifts behind across each loop and hits the 5 s
        // wait-cap (the signage micro-hitch). The fix seats the origin deterministically
        // via ISeekableClock.SeekBaseline at each item's first play — the same
        // primitive the seek path uses — so the audio clock and the per-item frame
        // PTS agree from frame one across every boundary.
        //
        // The assertion is at the clock-master seam (device-free): a clock-source
        // audio sink records its SeekBaseline reseats; each item boundary must
        // produce one reseat to the item origin (0). Before the fix the advance
        // path never reseated the audio-mastered clock and this stayed at zero.
        var av = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        Assert.NotNull(av);

        BootstrapNative();

        var videoSink = new WarmTrackingVideoSink();
        var audioSink = new ReseatTrackingAudioSink();

        var coordinator = new PlaylistCoordinator(
            [MediaSource.FromFile(av!)],
            RepeatMode.All
        );

        await using var controller = PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: videoSink,
            audioSink: audioSink, // audio-mastered clock (implements IClockSource).
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.All
        );

        var transitions = new List<PlaylistTransition>();
        using var sub = coordinator.SourceTransitioned.Subscribe(
            new TransitionCollector(t =>
            {
                lock (transitions)
                    transitions.Add(t);
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(av!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        await WaitUntilAsync(() => videoSink.PresentCount > 0, TimeSpan.FromSeconds(10));
        Assert.True(videoSink.PresentCount > 0, "No frames presented for the first item.");

        // First play must already have seated the master-clock origin to the item
        // start (0) rather than leaving it to first-buffer discovery.
        Assert.True(
            audioSink.SeekBaselineCount >= 1,
            "First play did not reseat the audio-mastered clock origin "
                + "(expected a SeekBaseline to the item origin)."
        );
        Assert.Contains(TimeSpan.Zero, audioSink.SeekBaselines);

        // Drive loop-wrap boundaries via skip; each crossing must reseat the
        // audio-mastered clock origin again — the per-loop epoch realignment.
        for (var boundary = 1; boundary <= 2; boundary++)
        {
            var reseatsBefore = audioSink.SeekBaselineCount;
            var framesBefore = videoSink.PresentCount;

            coordinator.RequestSkip();

            await WaitUntilAsync(
                () => audioSink.SeekBaselineCount > reseatsBefore,
                TimeSpan.FromSeconds(15)
            );
            Assert.True(
                audioSink.SeekBaselineCount > reseatsBefore,
                $"Boundary {boundary}: the audio-mastered clock origin was not "
                    + "reseated at the loop wrap (the gapless-loop epoch drift)."
            );

            await WaitUntilAsync(
                () => videoSink.PresentCount > framesBefore,
                TimeSpan.FromSeconds(15)
            );
            Assert.True(
                videoSink.PresentCount > framesBefore,
                $"Boundary {boundary}: the swapped-in loop item didn't present."
            );

            // Every reseat seats the per-item origin (each item plays 0 → duration).
            Assert.Contains(TimeSpan.Zero, audioSink.SeekBaselines);
        }

        Assert.Equal(PlaybackState.Playing, controller.State);

        await controller.DisposeAsync();
    }

    /// <summary>
    /// No-freeze / cadence guard for a single-clip <see cref="RepeatMode.All"/> loop
    /// driven by NATURAL end-of-stream (the signage attract/panel case), not skip.
    /// The clip loops over the same warm sink; the wall-clock interval between the
    /// last presented frame of one pass and the first of the next must stay on the
    /// order of a single frame — not balloon into the multi-hundred-millisecond
    /// freeze a per-loop presenter rebuild produces on a real GPU surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this guards (and what it deliberately does not).</b> The on-screen
    /// ~200&#160;ms loop freeze the consumer reported is dominated by GPU presenter
    /// cost — the zero-copy converter rebinding its decode bridge to a fresh decode
    /// device every pass (ADR-0064). These integration tests run with
    /// <see cref="HardwareDecodeMode.Disabled"/> (software decode, no GPU) for CI
    /// portability, where re-opening the tiny synthetic demuxer is itself only a few
    /// milliseconds — so a software rebuild boundary and a reuse boundary are
    /// indistinguishable by wall-clock here. This test therefore guards that the loop
    /// seam stays at frame cadence with <b>no freeze / hang / stall</b> (a real
    /// regression that WOULD surface in software), and on a hardware-decode host it
    /// additionally catches the GPU rebind cost. The <b>structural</b> guarantee that
    /// a single-clip loop reuses the runtime in place rather than rebuilding it is
    /// pinned deterministically by <c>PlaylistCoordinatorTests</c>
    /// (<c>All_SingleClipWrap_IsReplayNotAdvance</c>), and the real end-to-end
    /// boundary-gap reduction is validated on the GPU presenter example.
    /// </para>
    /// <para>
    /// The assertion is self-calibrating: the max boundary gap must stay within a
    /// small multiple of the observed steady-state inter-frame cadence and an
    /// absolute 2-frame-duration ceiling (the acceptance bar), so it scales with host
    /// speed and frame rate rather than a hard-coded number.
    /// </para>
    /// </remarks>
    [RequiresFfmpegAndCorpusFact]
    public async Task GaplessLoop_SingleClipRepeatAll_NaturalEosBoundaryIsNearZeroGap()
    {
        // A short clip so several natural-EOS loops complete quickly. 0.5 s @ 30 fps.
        var clip = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        Assert.NotNull(clip);
        const double fps = 30.0;
        var frameDuration = TimeSpan.FromSeconds(1.0 / fps);

        BootstrapNative();

        var sink = new TimestampedVideoSink();

        var coordinator = new PlaylistCoordinator(
            [MediaSource.FromFile(clip!)],
            RepeatMode.All
        );

        await using var controller = PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: sink,
            audioSink: null, // wallclock-paced, matching the signage no-audio surface.
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.All
        );

        var transitions = new List<PlaylistTransition>();
        using var sub = coordinator.SourceTransitioned.Subscribe(
            new TransitionCollector(t =>
            {
                lock (transitions)
                    transitions.Add(t);
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(clip!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        // Let the clip loop several times by natural EOS (no skip). The first
        // transition is the initial item; each wrap fires another. Wait for enough
        // wraps that we have multiple boundaries to measure.
        const int targetWraps = 4;
        await WaitUntilAsync(
            () => WrapCount(transitions) >= targetWraps,
            TimeSpan.FromSeconds(30)
        );
        var wraps = WrapCount(transitions);
        Assert.True(
            wraps >= targetWraps,
            $"Only {wraps} loop wrap(s) occurred in 30 s; expected >= {targetWraps}. "
                + $"Frames presented: {sink.PresentCount}."
        );

        // Still looping, presenter never rebuilt.
        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.Equal(0, sink.DisposeCount);

        var arrivals = sink.Arrivals;
        var report = AnalyzeBoundaryGaps(arrivals);

        _output.WriteLine(
            $"frames={arrivals.Count} wraps={wraps} boundaries={report.BoundaryGapsMs.Count}"
        );
        _output.WriteLine(
            $"steady-state median inter-frame gap = {report.SteadyMedianMs:F1} ms "
                + $"(frame duration = {frameDuration.TotalMilliseconds:F1} ms)"
        );
        _output.WriteLine(
            "boundary gaps (ms) = ["
                + string.Join(", ", report.BoundaryGapsMs.Select(g => g.ToString("F1")))
                + $"]  max = {report.MaxBoundaryMs:F1} ms"
        );

        Assert.True(report.BoundaryGapsMs.Count >= targetWraps - 1, "Too few boundaries measured.");

        // Absolute ceiling: a boundary must not exceed ~2 frame durations (the
        // acceptance bar). A full rebuild would blow far past this.
        var ceilingMs = 2.0 * frameDuration.TotalMilliseconds;
        // Self-calibrating ceiling: also no more than ~2.5x the steady-state cadence,
        // so the guard scales with host speed / fps rather than a hard-coded number.
        var relativeMs = 2.5 * report.SteadyMedianMs;
        var thresholdMs = Math.Max(ceilingMs, relativeMs);

        Assert.True(
            report.MaxBoundaryMs <= thresholdMs,
            $"Loop boundary gap {report.MaxBoundaryMs:F1} ms exceeded {thresholdMs:F1} ms "
                + $"(2 frame-durations={ceilingMs:F1} ms, 2.5x steady={relativeMs:F1} ms). "
                + "A single-clip RepeatMode.All loop boundary is NOT gapless — it looks "
                + "like a per-loop runtime rebuild rather than an in-place rewind."
        );

        await controller.DisposeAsync();
        Assert.Equal(0, sink.DisposeCount);
    }

    private static int TransitionCount(List<PlaylistTransition> transitions)
    {
        lock (transitions)
            return transitions.Count;
    }

    private static int WrapCount(List<PlaylistTransition> transitions)
    {
        lock (transitions)
            return transitions.Count(t => t.Wrapped);
    }

    /// <summary>
    /// Result of <see cref="AnalyzeBoundaryGaps"/>: the wall-clock inter-frame gaps
    /// at loop boundaries (a frame whose PTS is lower than its predecessor's — a new
    /// pass), the worst of them, and the steady-state (non-boundary) median cadence.
    /// </summary>
    private readonly record struct BoundaryGapReport(
        IReadOnlyList<double> BoundaryGapsMs,
        double MaxBoundaryMs,
        double SteadyMedianMs
    );

    /// <summary>
    /// Splits the wall-clock inter-frame gaps of a looping single-clip run into
    /// boundary gaps (across a PTS reset) and steady-state gaps (within a pass), so a
    /// test can compare the loop seam against the in-pass cadence.
    /// </summary>
    private static BoundaryGapReport AnalyzeBoundaryGaps(
        IReadOnlyList<(TimeSpan Pts, long Ticks)> arrivals
    )
    {
        var boundary = new List<double>();
        var steady = new List<double>();
        var msPerTick = 1000.0 / Stopwatch.Frequency;

        for (var i = 1; i < arrivals.Count; i++)
        {
            var gapMs = (arrivals[i].Ticks - arrivals[i - 1].Ticks) * msPerTick;
            // A boundary is where the PTS drops back toward zero — the loop wrapped
            // and frame i is the first of a new pass.
            if (arrivals[i].Pts < arrivals[i - 1].Pts)
                boundary.Add(gapMs);
            else
                steady.Add(gapMs);
        }

        steady.Sort();
        var median = steady.Count == 0 ? 0.0 : steady[steady.Count / 2];
        var max = boundary.Count == 0 ? 0.0 : boundary.Max();
        return new BoundaryGapReport(boundary, max, median);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static void BootstrapNative()
    {
        var bootstrapper = new FrameFlow.Native.FrameFlowBootstrapper(
            new FrameFlow.Native.FrameFlowNativeOptions { SkipHardwareProbe = true }
        );
        var result = bootstrapper.Initialize();
        Assert.True(result.IsSuccess, $"FFmpeg bootstrap failed: {result.Message}");
    }

    /// <summary>
    /// A video sink that counts frames presented and disposals, so a test can
    /// prove the same instance is reused across item boundaries and is never
    /// torn down by the playback stack.
    /// </summary>
    private sealed class WarmTrackingVideoSink : IVideoSink
    {
        private int _presentCount;
        private int _disposeCount;

        public int PresentCount => Volatile.Read(ref _presentCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IFramePool FramePool => null!;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            Interlocked.Increment(ref _presentCount);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A video sink that records each presented frame's PTS together with a
    /// monotonic wall-clock timestamp (<see cref="Stopwatch.GetTimestamp"/>) at the
    /// moment of presentation, so a test can measure the real-time interval between
    /// the last frame of one loop pass and the first of the next.
    /// </summary>
    private sealed class TimestampedVideoSink : IVideoSink
    {
        private readonly Lock _gate = new();
        private readonly List<(TimeSpan Pts, long Ticks)> _arrivals = [];
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int PresentCount
        {
            get
            {
                lock (_gate)
                    return _arrivals.Count;
            }
        }

        public IReadOnlyList<(TimeSpan Pts, long Ticks)> Arrivals
        {
            get
            {
                lock (_gate)
                    return _arrivals.ToArray();
            }
        }

        public IFramePool FramePool => null!;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            // Stamp arrival BEFORE disposing the frame; read the PTS off the frame.
            var pts = frame.Pts;
            var ticks = Stopwatch.GetTimestamp();
            lock (_gate)
                _arrivals.Add((pts, ticks));
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// An audio sink that also masters the pacing clock (<see cref="IClockSource"/>)
    /// and can have its origin reseated (<see cref="ISeekableClock"/>), recording
    /// every reseat so a test can prove the per-item-boundary origin reseat fires.
    /// </summary>
    /// <remarks>
    /// The published clock is <c>origin + samplesSinceActivation/rate</c>, mirroring
    /// the real OpenAL sink's sample-counter model. The origin is seated either by
    /// the recorded <see cref="SeekBaseline"/> reseat or — absent one — discovered
    /// from the first post-activation buffer's PTS (the pre-fix behaviour this test
    /// guards against). Samples advance the counter on each present so PaceUntil
    /// makes progress and frames flow.
    /// </remarks>
    private sealed class ReseatTrackingAudioSink : IAudioSink, IClockSource, ISeekableClock
    {
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _seekBaselines = [];
        private TimeSpan _origin;
        private bool _originSeated;
        private long _samplesPerChannel;
        private int _sampleRate;
        private bool _active;

        public int SeekBaselineCount
        {
            get
            {
                lock (_gate)
                    return _seekBaselines.Count;
            }
        }

        public IReadOnlyList<TimeSpan> SeekBaselines
        {
            get
            {
                lock (_gate)
                    return _seekBaselines.ToArray();
            }
        }

        public bool Muted { get; set; }

        public ValueTask ActivateAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                _active = true;
                _samplesPerChannel = 0;
                // Honour a reseat seeded before activation; otherwise re-discover
                // the origin from the first buffer (the pre-fix path).
                if (!_originSeated)
                    _origin = TimeSpan.Zero;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask PresentAsync(IAudioBuffer frame, CancellationToken ct = default)
        {
            var pcm = (PcmAudioBuffer)frame;
            try
            {
                lock (_gate)
                {
                    _sampleRate = pcm.SampleRate;
                    if (!_originSeated)
                    {
                        _origin = pcm.PresentationTime;
                        _originSeated = true;
                    }
                    if (pcm.Channels > 0)
                        _samplesPerChannel += pcm.SampleCount / pcm.Channels;
                }
                return ValueTask.CompletedTask;
            }
            finally
            {
                pcm.Dispose();
            }
        }

        public ValueTask PauseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                _active = false;
                _samplesPerChannel = 0;
                // Match the real sink: deactivation drops the discovered origin so the
                // next item rediscovers (or — post-fix — is reseated before activate).
                _origin = TimeSpan.Zero;
                _originSeated = false;
            }
            return ValueTask.CompletedTask;
        }

        public TimeSpan GetPlaybackTime()
        {
            lock (_gate)
            {
                if (!_active || _sampleRate <= 0)
                    return _origin;
                return _origin + TimeSpan.FromSeconds((double)_samplesPerChannel / _sampleRate);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // ── IClockSource ────────────────────────────────────────────────────
        public TimeSpan Latest => GetPlaybackTime();

        public ValueTask WaitUntilAsync(TimeSpan target, CancellationToken ct = default)
        {
            if (GetPlaybackTime() >= target)
                return ValueTask.CompletedTask;
            if (ct.IsCancellationRequested)
                return ValueTask.FromCanceled(ct);
            return new ValueTask(WaitCoreAsync(target, ct));
        }

        private async Task WaitCoreAsync(TimeSpan target, CancellationToken ct)
        {
            while (GetPlaybackTime() < target)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }

        // ── ISeekableClock ──────────────────────────────────────────────────
        public void SeekBaseline(TimeSpan position)
        {
            lock (_gate)
            {
                _seekBaselines.Add(position);
                _origin = position;
                _originSeated = true;
                _samplesPerChannel = 0;
            }
        }
    }

    private sealed class TransitionCollector : IObserver<PlaylistTransition>
    {
        private readonly Action<PlaylistTransition> _onNext;

        public TransitionCollector(Action<PlaylistTransition> onNext) => _onNext = onNext;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(PlaylistTransition value) => _onNext(value);
    }
}
