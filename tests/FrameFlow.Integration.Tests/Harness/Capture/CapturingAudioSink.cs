using FrameFlow.Media;
using FrameFlow.Graph;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// <see cref="IAudioSink"/> that retains every <see cref="PcmAudioBuffer"/>
/// the playback runtime writes to it as a heap-copied <see cref="AudioCapture"/>.
/// Intended for content-level integration tests; see
/// <see cref="PlaybackInvariants"/> for the assertions that consume this.
/// </summary>
/// <remarks>
/// <para>
/// Differs from <see cref="HarnessAudioSink"/> in one axis only: this sink
/// keeps the sample bytes. Everything else (lifecycle accounting, playback
/// clock model, pause/resume semantics) is the same shape. Tests that
/// don't need content stay on <see cref="HarnessAudioSink"/>; tests that
/// need content opt into this one.
/// </para>
/// <para>
/// Thread safety: <see cref="WriteAsync"/> is called from the playback
/// audio worker on a single dedicated task, so the underlying list is
/// not contended. <see cref="GetPlaybackTime"/> is called from the video
/// worker concurrently; it only reads <see cref="_sampleRate"/> and the
/// derived sample counter, which are guarded by <see cref="Volatile"/>
/// reads. The captured-list snapshot exposed via <see cref="Captures"/>
/// is intended for post-playback inspection only.
/// </para>
/// </remarks>
internal sealed class CapturingAudioSink : IAudioSink
{
    private readonly List<AudioCapture> _captures = new();
    private readonly Lock _capturesLock = new();

    private long _samplesPerChannel;
    private int _sampleRate;
    private int _isActive;
    private bool _paused;
    private long _baselineSamplesPerChannel;

    public CapturingAudioSink()
    {
    }

    public bool Muted { get; set; }

    /// <summary>
    /// Snapshot of all captured blocks in arrival order. Safe to read
    /// after playback completes; do not enumerate concurrently with
    /// active <see cref="WriteAsync"/> calls — copy first if needed.
    /// </summary>
    public IReadOnlyList<AudioCapture> Captures
    {
        get
        {
            lock (_capturesLock)
            {
                return _captures.ToArray();
            }
        }
    }

    /// <summary>Total captured blocks since construction.</summary>
    public int BlockCount
    {
        get
        {
            lock (_capturesLock)
            {
                return _captures.Count;
            }
        }
    }

    public ValueTask ActivateAsync(CancellationToken ct = default)
    {
        Volatile.Write(ref _isActive, 1);
        _baselineSamplesPerChannel = _paused
            ? Volatile.Read(ref _samplesPerChannel)
            : _baselineSamplesPerChannel;
        _paused = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask PresentAsync(IAudioBuffer frame, CancellationToken ct = default)
    {
        var block = (PcmAudioBuffer)frame;
        try
        {
            // Snapshot the samples out of the pooled buffer so the capture
            // outlives the buffer's refcount lifetime. After this call
            // returns the sink disposes the block (per IFrameSink contract)
            // which decrements the refcount; the captured short[] is
            // independent.
            var samplesSpan = block.Samples.Span;
            var copy = new short[samplesSpan.Length];
            samplesSpan.CopyTo(copy);

            var capture = new AudioCapture(
                Pts: block.PresentationTime,
                InterleavedSamples: copy,
                SampleRate: block.SampleRate,
                Channels: block.Channels
            );

            lock (_capturesLock)
            {
                _captures.Add(capture);
            }

            Volatile.Write(ref _sampleRate, block.SampleRate);
            if (block.Channels > 0)
            {
                Interlocked.Add(ref _samplesPerChannel, samplesSpan.Length / block.Channels);
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            block.Dispose();
        }
    }

    public ValueTask PauseAsync(CancellationToken ct = default)
    {
        _paused = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(CancellationToken ct = default)
    {
        Volatile.Write(ref _isActive, 0);
        _baselineSamplesPerChannel = Volatile.Read(ref _samplesPerChannel);
        _paused = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns playback time derived from the cumulative sample count.
    /// This matches <see cref="HarnessAudioSink"/>'s behaviour: the sink
    /// acts as the AV-sync master clock for the playback runtime.
    /// </summary>
    /// <remarks>
    /// Called from the video worker once per decoded frame. Reading the
    /// volatile counter + sample rate without a lock is intentional —
    /// the assertion library never relies on this value mid-playback,
    /// only on the captured PCM after the fact.
    /// </remarks>
    public TimeSpan GetPlaybackTime()
    {
        var rate = Volatile.Read(ref _sampleRate);
        if (rate <= 0)
            return TimeSpan.Zero;
        var samples = Volatile.Read(ref _samplesPerChannel);
        return TimeSpan.FromSeconds((double)samples / rate);
    }

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _isActive, 0);
        return ValueTask.CompletedTask;
    }
}
