using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// A content-capturing audio sink that <b>also masters the pacing clock</b> — the §7.3
/// sink that closes the gap where the content invariants
/// (<see cref="PlaybackInvariants"/>) were only ever verified on the <i>wallclock</i>-master
/// path. It merges two existing harness sinks:
/// </summary>
/// <list type="bullet">
/// <item><see cref="CapturingAudioSink"/>'s sample retention — every
/// <see cref="PcmAudioBuffer"/> is heap-copied into an <see cref="AudioCapture"/> so the
/// content assertions can run over the captured PCM; and</item>
/// <item><see cref="ClockMasteringReseatAudioSink"/>'s clock model — it implements
/// <see cref="IClockSource"/> + <see cref="ISeekableClock"/>, so
/// <c>SubstrateSession</c> selects it as the master clock for an item with audio (the
/// audio-mastered path the real <c>OpenAlAudioSink</c> takes), instead of leaving the
/// session's wallclock as master.</item>
/// </list>
/// <remarks>
/// <para>
/// <b>Why this sink exists.</b> <see cref="CapturingAudioSink"/> retains content but is
/// deliberately <i>not</i> an <see cref="IClockSource"/>, so the content-capture tests run
/// with the session's wallclock as master and never exercise the audio sample-counter clock
/// that paces video in production. <see cref="ClockMasteringReseatAudioSink"/> is an
/// <see cref="IClockSource"/> but captures no content. Neither alone can answer "do the
/// content invariants hold when the audio sink is the master clock?" — this sink does, by
/// being both at once. It mirrors the real <c>OpenAlAudioSink</c>, which is simultaneously
/// the content destination and the master clock.
/// </para>
/// <para>
/// <b>Clock model (mirrors the real sink).</b> The published clock is
/// <c>origin + samplesSinceActivation / sampleRate</c>. The origin is seated either by a
/// <see cref="SeekBaseline"/> reseat seeded before activation (the deterministic seek /
/// loop-seek path) or, absent a reseat, re-discovered from the first post-activation
/// buffer's PTS. <see cref="DeactivateAsync"/> drops the discovered origin and any
/// unconsumed seed (matching the real sink clearing its origin/seed on deactivate). This is
/// the same arithmetic <see cref="AudioCapture"/>-free
/// <see cref="ClockMasteringReseatAudioSink"/> uses; the only addition here is the content
/// capture.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="PresentAsync"/> runs on the playback audio worker (a
/// single dedicated task); the clock state and capture list are both guarded by
/// <see cref="_gate"/>. <see cref="GetPlaybackTime"/> / <see cref="Latest"/> are read from
/// the video worker concurrently and take the same lock. <see cref="Captures"/> is a
/// post-playback snapshot.
/// </para>
/// </remarks>
internal sealed class ContentCapturingClockMasterAudioSink : IAudioSink, IClockSource, ISeekableClock
{
    private readonly Lock _gate = new();
    private readonly List<AudioCapture> _captures = new();

    // ── Clock state (origin + sample counter), all under _gate ──────────
    private TimeSpan _origin;
    private bool _originSeated;
    private long _samplesPerChannel;
    private int _sampleRate;
    private bool _active;

    public bool Muted { get; set; }

    /// <summary>
    /// Snapshot of all captured blocks in arrival order. Safe to read after playback
    /// completes; do not enumerate concurrently with active <see cref="PresentAsync"/>
    /// calls.
    /// </summary>
    public IReadOnlyList<AudioCapture> Captures
    {
        get
        {
            lock (_gate)
                return _captures.ToArray();
        }
    }

    /// <summary>Total captured blocks since construction.</summary>
    public int BlockCount
    {
        get
        {
            lock (_gate)
                return _captures.Count;
        }
    }

    // ── IAudioSink ──────────────────────────────────────────────────────

    public ValueTask ActivateAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _active = true;
            _samplesPerChannel = 0;
            // Honour a reseat seeded before activation (origin = seek/loop target);
            // otherwise leave the origin to be re-discovered from the first buffer.
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
            // Snapshot the samples out of the pooled buffer so the capture outlives the
            // buffer's refcount lifetime (the sink disposes the block below, per the
            // IFrameSink contract; the captured short[] is independent).
            var samplesSpan = pcm.Samples.Span;
            var copy = new short[samplesSpan.Length];
            samplesSpan.CopyTo(copy);

            var capture = new AudioCapture(
                Pts: pcm.PresentationTime,
                InterleavedSamples: copy,
                SampleRate: pcm.SampleRate,
                Channels: pcm.Channels
            );

            lock (_gate)
            {
                _captures.Add(capture);

                _sampleRate = pcm.SampleRate;
                if (!_originSeated)
                {
                    // Re-discover the origin from the first post-activation buffer's PTS
                    // when no reseat seeded it (the fresh-play path).
                    _origin = pcm.PresentationTime;
                    _originSeated = true;
                }
                if (pcm.Channels > 0)
                    _samplesPerChannel += samplesSpan.Length / pcm.Channels;
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
            // Match the real sink: deactivation drops the discovered origin and any
            // unconsumed seed, so the next activation re-discovers (fresh) or is reseated
            // before activate (seek/loop).
            _origin = TimeSpan.Zero;
            _originSeated = false;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns playback time as <c>origin + samplesSinceActivation / sampleRate</c> — the
    /// sink acts as the AV-sync master clock for the playback runtime, exactly as the real
    /// OpenAL sink does.
    /// </summary>
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
            _origin = position;
            _originSeated = true;
            _samplesPerChannel = 0;
        }
    }
}
