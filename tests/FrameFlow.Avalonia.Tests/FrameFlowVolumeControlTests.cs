using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;
using FrameFlow.Player;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Covers <see cref="FrameFlowVolumeControl"/>'s gating on
/// <see cref="IMediaPlayer.SupportsVolumeControl"/> (ADR-0065 §4).
/// </summary>
/// <remarks>
/// <para>
/// This is the branch ADR-0065 exists to make possible, and it had no coverage
/// of any kind. The unit tests in <c>FrameFlow.Player.Tests</c> pin the player's
/// side of the capability — that <c>SupportsVolumeControl</c> is false and the
/// writes no-op — but nothing asserted that the widget actually disables itself.
/// Every runnable example attaches <c>OpenAlAudioSink</c>, which implements
/// <see cref="IVolumeControl"/>, so no example reaches the false branch either.
/// </para>
/// <para>
/// The failure this prevents is the original defect in a new place: a slider
/// that looks live and changes nothing. Against a capture sink or an
/// audio-disabled player, that is exactly what the pre-ADR-0065 control did.
/// </para>
/// </remarks>
public sealed class FrameFlowVolumeControlTests
{
    [AvaloniaFact]
    public void DisablesItself_WhenPlayerHasNoVolumeControl()
    {
        var control = new FrameFlowVolumeControl
        {
            MediaPlayer = new FakePlayer { SupportsVolumeControl = false },
        };

        Assert.False(MuteButton(control).IsEnabled);
        Assert.False(SliderOf(control).IsEnabled);
    }

    [AvaloniaFact]
    public void EnablesItself_WhenPlayerHasVolumeControl()
    {
        var control = new FrameFlowVolumeControl
        {
            MediaPlayer = new FakePlayer { SupportsVolumeControl = true },
        };

        Assert.True(MuteButton(control).IsEnabled);
        Assert.True(SliderOf(control).IsEnabled);
    }

    [AvaloniaFact]
    public void StaysDisabled_WhenNoPlayerAttached()
    {
        var control = new FrameFlowVolumeControl();

        Assert.False(MuteButton(control).IsEnabled);
        Assert.False(SliderOf(control).IsEnabled);
    }

    [AvaloniaFact]
    public void Rebinding_ToAGainlessPlayer_DisablesAndResetsTheDisplay()
    {
        // The stale-visuals case: bind to a player with gain, move the slider,
        // then rebind to one without. Leaving the previous player's position and
        // mute glyph on screen would describe a player that is no longer
        // attached.
        var control = new FrameFlowVolumeControl
        {
            MediaPlayer = new FakePlayer
            {
                SupportsVolumeControl = true,
                Volume = 0.25f,
                Muted = true,
            },
        };

        Assert.True(SliderOf(control).IsEnabled);
        Assert.Equal(0.25, SliderOf(control).Value, precision: 3);
        Assert.Equal(true, MuteButton(control).IsChecked);

        control.MediaPlayer = new FakePlayer { SupportsVolumeControl = false };

        Assert.False(SliderOf(control).IsEnabled);
        Assert.False(MuteButton(control).IsEnabled);
        Assert.Equal(1.0, SliderOf(control).Value, precision: 3);
        Assert.Equal(false, MuteButton(control).IsChecked);
    }

    [AvaloniaFact]
    public void Rebinding_ToNull_ResetsToUnityAndUnmuted()
    {
        var control = new FrameFlowVolumeControl
        {
            MediaPlayer = new FakePlayer
            {
                SupportsVolumeControl = true,
                Volume = 0.4f,
                Muted = true,
            },
        };

        control.MediaPlayer = null;

        Assert.False(SliderOf(control).IsEnabled);
        Assert.Equal(1.0, SliderOf(control).Value, precision: 3);
        Assert.Equal(false, MuteButton(control).IsChecked);
    }

    [AvaloniaFact]
    public void SeedsFromTheAttachedPlayer_WhenSupported()
    {
        // Volume and mute persist across player lifetimes via the audio-sink
        // singleton, so an existing setting must be reflected on bind rather
        // than reset to the control's defaults.
        var control = new FrameFlowVolumeControl
        {
            MediaPlayer = new FakePlayer
            {
                SupportsVolumeControl = true,
                Volume = 0.6f,
                Muted = false,
            },
        };

        Assert.Equal(0.6, SliderOf(control).Value, precision: 3);
        Assert.Equal(false, MuteButton(control).IsChecked);
    }

    private static ToggleButton MuteButton(FrameFlowVolumeControl c) =>
        c.Children.OfType<ToggleButton>().Single();

    private static Slider SliderOf(FrameFlowVolumeControl c) =>
        c.Children.OfType<Slider>().Single();

    /// <summary>
    /// Minimal <see cref="IMediaPlayer"/>. Only the volume projection is
    /// exercised; the transport and observables are never touched.
    /// </summary>
    private sealed class FakePlayer : IMediaPlayer
    {
        public bool SupportsVolumeControl { get; init; }
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }

        public PlaybackState State => PlaybackState.Idle;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public MediaInfo MediaInfo => default!;

        public IObservable<PlaybackState> StateChanged { get; } = new Never<PlaybackState>();
        public IObservable<TimeSpan> PositionChanged { get; } = new Never<TimeSpan>();
        public IObservable<LoopStalled> LoopStalled { get; } = new Never<LoopStalled>();
        public IObservable<PlaybackDiagnosticsSnapshot> Diagnostics { get; } =
            new Never<PlaybackDiagnosticsSnapshot>();

        public PlaybackDiagnosticsSnapshot PollDiagnostics() => PlaybackDiagnosticsSnapshot.Empty;

        public Task PlayAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Never<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => Noop.Instance;

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();

            public void Dispose() { }
        }
    }
}
