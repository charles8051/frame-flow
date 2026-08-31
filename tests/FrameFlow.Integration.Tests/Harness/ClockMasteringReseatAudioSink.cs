using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// An <see cref="IAudioSink"/> that also <b>masters the pacing clock</b>
/// (<see cref="IClockSource"/>) and can have its timeline origin reseated
/// (<see cref="ISeekableClock"/>) — the integration-test analogue of
/// <c>OpenAlAudioSink</c>'s sample-counter clock. Because it implements
/// <see cref="IClockSource"/>, <c>SubstrateSession</c> selects it as the
/// master clock for an item that has audio (the audio-mastered / panel path),
/// exactly as the real OpenAL sink is selected on a signage surface that
/// attaches audio at all. The plain <see cref="HarnessAudioSink"/> deliberately does
/// <i>not</i> implement <see cref="IClockSource"/>, so it leaves the wallclock
/// mastering and never exercises this path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock model (mirrors the real sink).</b> The published clock is
/// <c>origin + samplesSinceActivation / sampleRate</c>. The origin is seated one
/// of two ways:
/// </para>
/// <list type="bullet">
/// <item>
/// by a <see cref="SeekBaseline"/> reseat seeded before activation — the
/// deterministic path the seek / loop-seek path drives
/// (<c>SubstrateSession.SeekAsync</c> →
/// <c>(_clockSource as ISeekableClock)?.SeekBaseline(target)</c>); or
/// </item>
/// <item>
/// absent a reseat, <b>re-discovered</b> from the first post-activation
/// buffer's PTS — the pre-fix gapless-loop behaviour B5 guards against, where the
/// audio clock sits at a device-paced, buffer-PTS-dependent origin while the
/// already-decoded video frames carry climbing PTS, so <c>PaceUntil</c> drifts
/// behind across every loop boundary.
/// </item>
/// </list>
/// <para>
/// <see cref="DeactivateAsync"/> drops the discovered origin (matching the real
/// sink, which clears <c>_baseSourceTimeCaptured</c> and any unconsumed pending
/// seed on deactivate), so a loop that does not reseat would re-discover at the
/// next activation. The sample counter advances on every <see cref="PresentAsync"/>
/// so the published clock climbs, letting <c>PaceUntil</c> make progress and frames
/// flow across loops — the test measures origin <i>seating</i>, not the wait-cap.
/// </para>
/// </remarks>
internal sealed class ClockMasteringReseatAudioSink : IAudioSink, IClockSource, ISeekableClock
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _seekBaselines = [];

    // ── Clock state (origin + sample counter), all under _gate ──────────
    private TimeSpan _origin;
    private bool _originSeated;
    private long _samplesPerChannel;
    private int _sampleRate;
    private bool _active;

    // ── Lifecycle counters ──────────────────────────────────────────────
    private int _activateCount;
    private int _deactivateCount;
    private int _blockCount;

    public int ActivateCount
    {
        get
        {
            lock (_gate)
                return _activateCount;
        }
    }

    public int DeactivateCount
    {
        get
        {
            lock (_gate)
                return _deactivateCount;
        }
    }

    public int BlockCount
    {
        get
        {
            lock (_gate)
                return _blockCount;
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    /// <summary>Number of <see cref="SeekBaseline"/> reseats recorded so far.</summary>
    public int SeekBaselineCount
    {
        get
        {
            lock (_gate)
                return _seekBaselines.Count;
        }
    }

    /// <summary>Every origin a <see cref="SeekBaseline"/> reseat seated, in order.</summary>
    public IReadOnlyList<TimeSpan> SeekBaselines
    {
        get
        {
            lock (_gate)
                return _seekBaselines.ToArray();
        }
    }

    public bool Muted { get; set; }

    // ── IAudioSink ──────────────────────────────────────────────────────

    public ValueTask ActivateAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _activateCount++;
            _active = true;
            _samplesPerChannel = 0;
            // Honour a reseat seeded before activation (origin = seek/loop target);
            // otherwise leave the origin to be re-discovered from the first buffer
            // (the pre-fix path) by clearing the seated flag.
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
                _blockCount++;
                _sampleRate = pcm.SampleRate;
                if (!_originSeated)
                {
                    // Re-discover the origin from the first post-activation buffer's
                    // PTS — the behaviour a deterministic reseat must pre-empt.
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
            _deactivateCount++;
            _active = false;
            _samplesPerChannel = 0;
            // Match the real sink: deactivation drops the discovered origin and any
            // unconsumed seed, so the next activation re-discovers (pre-fix) or is
            // reseated before activate (post-fix).
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

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            _active = false;
        return ValueTask.CompletedTask;
    }

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
