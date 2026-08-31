using FrameFlow.Media;
using FrameFlow.Graph;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// Instrumented <see cref="IAudioSink"/> for integration tests.
/// Tracks block counts, sample counts, lifecycle calls, and maintains correct
/// <see cref="GetPlaybackTime"/> across pause/resume/deactivate/activate cycles.
/// </summary>
internal sealed class HarnessAudioSink : IAudioSink
{
    public HarnessAudioSink()
    {
    }

    // ── Sample tracking ─────────────────────────────────────────────
    private long _baselineSamplesPerChannel;
    private long _sessionSamplesPerChannel;
    private int _sampleRate;
    private bool _paused;

    // ── Counters ────────────────────────────────────────────────────
    private int _blockCount;
    private int _activateCount;
    private int _pauseCount;
    private int _resumeCount;
    private int _deactivateCount;
    private int _isActive;
    private TimeSpan _lastBlockPts;

    // ── Thread-safe read accessors ──────────────────────────────────
    public int BlockCount => Volatile.Read(ref _blockCount);
    public long TotalSamplesPerChannel =>
        Volatile.Read(ref _baselineSamplesPerChannel)
        + Volatile.Read(ref _sessionSamplesPerChannel);
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public int ActivateCount => Volatile.Read(ref _activateCount);
    public int PauseCount => Volatile.Read(ref _pauseCount);
    public int ResumeCount => Volatile.Read(ref _resumeCount);
    public int DeactivateCount => Volatile.Read(ref _deactivateCount);
    public bool IsActive => Volatile.Read(ref _isActive) == 1;
    public TimeSpan LastBlockPts => _lastBlockPts;
    public TimeSpan TimeAtPause { get; private set; }

    public double DecodedDurationSeconds =>
        _sampleRate > 0 ? (double)TotalSamplesPerChannel / _sampleRate : 0;

    public bool Muted { get; set; }

    // ── IAudioSink ──────────────────────────────────────────────────

    public ValueTask ActivateAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _activateCount);
        Volatile.Write(ref _isActive, 1);
        // On resume (paused → activate), carry forward pre-pause samples.
        // On fresh start (deactivate → activate), baseline is already zeroed.
        _baselineSamplesPerChannel = _paused
            ? TotalSamplesPerChannel
            : Volatile.Read(ref _baselineSamplesPerChannel);
        _sessionSamplesPerChannel = 0;
        _paused = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask PresentAsync(IAudioBuffer frame, CancellationToken ct = default)
    {
        var block = (PcmAudioBuffer)frame;
        try
        {
            Interlocked.Increment(ref _blockCount);
            Volatile.Write(ref _sampleRate, block.SampleRate);
            if (block.Channels > 0)
                Interlocked.Add(ref _sessionSamplesPerChannel, block.SampleCount / block.Channels);
            _lastBlockPts = block.PresentationTime;
            return ValueTask.CompletedTask;
        }
        finally
        {
            block.Dispose();
        }
    }

    public ValueTask PauseAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pauseCount);
        TimeAtPause = GetPlaybackTime();
        _paused = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _resumeCount);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _deactivateCount);
        Volatile.Write(ref _isActive, 0);
        // Preserve decoded samples across loop restarts. PlaybackSession calls
        // DeactivateAsync() before each loop re-activation, so zeroing the
        // counters here would collapse multi-iteration playback down to a
        // single pass in integration assertions.
        _baselineSamplesPerChannel = TotalSamplesPerChannel;
        _sessionSamplesPerChannel = 0;
        _paused = false;
        return ValueTask.CompletedTask;
    }

    public TimeSpan GetPlaybackTime() =>
        _sampleRate > 0
            ? TimeSpan.FromSeconds((double)TotalSamplesPerChannel / _sampleRate)
            : TimeSpan.Zero;

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _isActive, 0);
        return ValueTask.CompletedTask;
    }
}
