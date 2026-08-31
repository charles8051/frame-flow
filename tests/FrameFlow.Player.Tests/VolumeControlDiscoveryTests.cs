using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using FrameFlow.Player;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Covers <see cref="IMediaPlayer.SupportsVolumeControl"/> and the
/// no-op-and-round-trip behaviour of <see cref="IMediaPlayer.Volume"/> /
/// <see cref="IMediaPlayer.Muted"/> when the attached audio sink has no gain
/// stage.
/// </summary>
/// <remarks>
/// The gain capability used to be a <c>bool</c> on an
/// <c>AudioSinkCapabilities</c> record that no production code read, with a
/// documented fallback of accepting the write and silently dropping it. It is
/// now <see cref="IVolumeControl"/>, and these tests pin the two things that
/// were previously unobservable: that a consumer can discover the absence, and
/// that a consumer who ignores the discovery is not silently lied to on
/// read-back.
/// </remarks>
public sealed class VolumeControlDiscoveryTests
{
    [Fact]
    public async Task SupportsVolumeControl_False_WhenSinkHasNoGainStage()
    {
        await using var player = NewPlayer(new GainlessAudioSink());
        Assert.False(player.SupportsVolumeControl);
    }

    [Fact]
    public async Task SupportsVolumeControl_False_WhenNoAudioSinkAttached()
    {
        await using var player = NewPlayer(audioSink: null);
        Assert.False(player.SupportsVolumeControl);
    }

    [Fact]
    public async Task SupportsVolumeControl_True_ForASinkImplementingIVolumeControl()
    {
        await using var player = NewPlayer(new GainAudioSink());
        Assert.True(player.SupportsVolumeControl);
    }

    [Fact]
    public async Task Volume_RoundTrips_WhenUnsupported()
    {
        await using var player = NewPlayer(new GainlessAudioSink());

        player.Volume = 0.25f;

        // The write reached no gain stage, but a UI reading back to render a
        // slider position or a speaker glyph must see what the user chose, not
        // a value they never set.
        Assert.Equal(0.25f, player.Volume);
    }

    [Fact]
    public async Task Muted_RoundTrips_WhenUnsupported()
    {
        await using var player = NewPlayer(new GainlessAudioSink());

        Assert.False(player.Muted);
        player.Muted = true;
        Assert.True(player.Muted);
    }

    [Fact]
    public async Task Volume_DoesNotThrow_WhenUnsupported()
    {
        await using var player = NewPlayer(new GainlessAudioSink());

        // A consumer that ignores SupportsVolumeControl should not crash over
        // a cosmetic control. Documented no-op, not a throw.
        var ex = Record.Exception(() =>
        {
            player.Volume = 0.5f;
            player.Muted = true;
        });

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-0.1f)]
    public async Task Volume_RejectsInvalidGain_EvenWhenUnsupported(float invalid)
    {
        await using var player = NewPlayer(new GainlessAudioSink());

        // The no-op rule covers an absent capability, not invalid input. If the
        // detached path skipped validation, IMediaPlayer.Volume would be the
        // one route by which a caller could read back NaN.
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Volume = invalid);
    }

    [Fact]
    public async Task Volume_ReachesTheSink_WhenSupported()
    {
        var sink = new GainAudioSink();
        await using var player = NewPlayer(sink);

        player.Volume = 0.75f;
        player.Muted = true;

        Assert.Equal(0.75f, sink.Volume);
        Assert.True(sink.Muted);
        Assert.Equal(0.75f, player.Volume);
        Assert.True(player.Muted);
    }

    [Fact]
    public async Task Volume_DefaultsToUnity_WhenUnsupported()
    {
        await using var player = NewPlayer(new GainlessAudioSink());

        // Unity, not zero. The previous shape returned 0f for a missing sink,
        // which reads as "muted" to a glyph that buckets by level.
        Assert.Equal(1.0f, player.Volume);
    }

    private static MediaPlayerCore NewPlayer(IAudioSink? audioSink) =>
        new(new StubController(), audioSink, ownedProvider: null, NullLogger.Instance);

    // ── Doubles ──────────────────────────────────────────────────────────────

    /// <summary>A sink that records or discards audio: no gain stage, so no
    /// <see cref="IVolumeControl"/>.</summary>
    private sealed class GainlessAudioSink : IAudioSink
    {
        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A sink that owns a gain stage.</summary>
    private sealed class GainAudioSink : IAudioSink, IVolumeControl
    {
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Inert controller. These tests exercise only the volume projection, which
    /// never touches the controller.
    /// </summary>
    private sealed class StubController : IPlaybackController
    {
        public Task<Result> LoadAsync(IMediaSource source, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> UnloadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> PlayAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> PauseAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public PlaybackState State => PlaybackState.Idle;
        public SeekState SeekingState => SeekState.NotSeeking;
        public RepeatMode RepeatMode => RepeatMode.Off;
        public bool IsActivelyPresenting => false;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public MediaInfo? MediaInfo => null;

        public IObservable<StateTransition<PlaybackState>> PlaybackStateChanged { get; } =
            new NeverObservable<StateTransition<PlaybackState>>();
        public IObservable<StateTransition<SeekState>> SeekStateChanged { get; } =
            new NeverObservable<StateTransition<SeekState>>();
        public IObservable<StateTransition<RepeatMode>> RepeatModeChanged { get; } =
            new NeverObservable<StateTransition<RepeatMode>>();
        public IObservable<LoopRestarted> LoopRestarted { get; } =
            new NeverObservable<LoopRestarted>();
        public IObservable<LoopStalled> LoopStalled { get; } = new NeverObservable<LoopStalled>();
        public IObservable<PlaybackError> ErrorOccurred { get; } =
            new NeverObservable<PlaybackError>();
        public IObservable<TimeSpan> PositionTick { get; } = new NeverObservable<TimeSpan>();

        public PlaybackDiagnosticsSnapshot GetDiagnostics() => PlaybackDiagnosticsSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An observable that never produces and never completes.</summary>
    private sealed class NeverObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose() { }
        }
    }
}
