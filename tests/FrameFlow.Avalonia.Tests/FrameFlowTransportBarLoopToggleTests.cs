using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;
using FrameFlow.Player;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// The transport bar's Loop toggle shows the bound transport's repeat mode and follows its
/// changes (#631). It used to show <c>LoopByDefault</c> alone, so a player looping a folder under
/// <see cref="RepeatMode.All"/> showed Loop unchecked, and a click replaced All with One.
/// </summary>
/// <remarks>
/// The bar marshals <see cref="IMediaTransport.RepeatModeChanged"/> to the UI thread by posting,
/// so <see cref="Dispatcher.RunJobs()"/> is the barrier between a change and what the toggle
/// shows. A command the bar issues starts synchronously, so the fake has recorded it by the time
/// the click or the bind returns.
/// </remarks>
public sealed class FrameFlowTransportBarLoopToggleTests
{
    [AvaloniaFact]
    public void BoundToATransportInAll_ShowsLoopAll()
    {
        var bar = new FrameFlowTransportBar
        {
            Transport = new FakeTransport { RepeatMode = RepeatMode.All },
        };

        AssertShows(bar, RepeatMode.All);
    }

    [AvaloniaFact]
    public void LoopByDefault_LeavesATransportInAll_AsItIs()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.All };

        var bar = new FrameFlowTransportBar { LoopByDefault = true, Transport = transport };
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(transport.Requested);
        AssertShows(bar, RepeatMode.All);
    }

    [AvaloniaFact]
    public void LoopByDefault_SetsATransportThatIsOff_ToOne()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.Off };

        var bar = new FrameFlowTransportBar { LoopByDefault = true, Transport = transport };
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { RepeatMode.One }, transport.Requested);
        AssertShows(bar, RepeatMode.One);
    }

    [AvaloniaFact]
    public void FollowsTheTransportsRepeatMode()
    {
        // The app sets the mode itself, as one that plays a folder under All does after binding.
        var transport = new FakeTransport { RepeatMode = RepeatMode.Off };
        var bar = new FrameFlowTransportBar { Transport = transport };

        transport.Change(RepeatMode.All);
        Dispatcher.UIThread.RunJobs();
        AssertShows(bar, RepeatMode.All);

        transport.Change(RepeatMode.Off);
        Dispatcher.UIThread.RunJobs();
        AssertShows(bar, RepeatMode.Off);
    }

    [AvaloniaFact]
    public void AClickInAll_AsksForOff_AndShowsAll_UntilTheModeChanges()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.All, Applies = false };
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Click(window, LoopButton(bar));

            Assert.Equal(new[] { RepeatMode.Off }, transport.Requested);
            AssertShows(bar, RepeatMode.All);

            transport.Change(RepeatMode.Off);
            Dispatcher.UIThread.RunJobs();
            AssertShows(bar, RepeatMode.Off);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Clicks_StepThroughOneAndAll_BackToOff()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.Off };
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            var shown = new List<RepeatMode>();
            for (var i = 0; i < 3; i++)
            {
                Click(window, LoopButton(bar));
                Dispatcher.UIThread.RunJobs();
                shown.Add(ShownMode(bar));
            }

            Assert.Equal(new[] { RepeatMode.One, RepeatMode.All, RepeatMode.Off }, transport.Requested);
            Assert.Equal(new[] { RepeatMode.One, RepeatMode.All, RepeatMode.Off }, shown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ARefusedClick_LeavesTheToggleOnTheModeInForce()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.One, Refuses = true };
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Click(window, LoopButton(bar));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { RepeatMode.All }, transport.Requested);
            AssertShows(bar, RepeatMode.One);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AChangePostedByATransportSinceUnbound_DoesNotShow()
    {
        var first = new FakeTransport { RepeatMode = RepeatMode.Off };
        var bar = new FrameFlowTransportBar { Transport = first };

        // Raised while bound, delivered after the rebind.
        first.Change(RepeatMode.All);
        bar.Transport = new FakeTransport { RepeatMode = RepeatMode.Off };
        Dispatcher.UIThread.RunJobs();

        AssertShows(bar, RepeatMode.Off);
    }

    [AvaloniaFact]
    public void Unbinding_ShowsOff_AndDisablesTheToggle()
    {
        var bar = new FrameFlowTransportBar
        {
            Transport = new FakeTransport { RepeatMode = RepeatMode.All },
        };

        bar.Transport = null;

        AssertShows(bar, RepeatMode.Off);
        Assert.False(LoopButton(bar).IsEnabled);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ToggleButton LoopButton(FrameFlowTransportBar bar) =>
        bar.Children.OfType<ToggleButton>().Single();

    // Spelled out rather than read from LoopToggle.Face, so these tests see a wrong face too.
    private static readonly Dictionary<RepeatMode, (bool IsChecked, string Label)> Faces = new()
    {
        [RepeatMode.Off] = (false, "Loop"),
        [RepeatMode.One] = (true, "Loop one"),
        [RepeatMode.All] = (true, "Loop all"),
    };

    private static void AssertShows(FrameFlowTransportBar bar, RepeatMode mode)
    {
        var button = LoopButton(bar);
        Assert.Equal(Faces[mode].IsChecked, button.IsChecked);
        Assert.Equal(Faces[mode].Label, button.Content);
    }

    private static RepeatMode ShownMode(FrameFlowTransportBar bar)
    {
        var button = LoopButton(bar);
        return Faces.Single(f => f.Value == (button.IsChecked == true, (string)button.Content!)).Key;
    }

    // The theme goes in before the bar, as an app loads it, so the toggle has a template to hit.
    private static Window Show(FrameFlowTransportBar bar)
    {
        var window = new Window { Width = 600, Height = 100 };
        window.Styles.Add(new FluentTheme());
        window.Content = bar;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Window window, Control control)
    {
        var center =
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The control is not in the window.");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    /// <summary>
    /// An <see cref="IMediaTransport"/> whose repeat mode the test drives. It records every mode
    /// asked for, and by default adopts it and reports the change, as the player does: before the
    /// command completes, on the caller's thread.
    /// </summary>
    private sealed class FakeTransport : IMediaTransport
    {
        private readonly Subject<StateTransition<RepeatMode>> _repeatModeChanged = new();

        public RepeatMode RepeatMode { get; set; }

        /// <summary>Whether a request is adopted and reported. Off, it is only recorded.</summary>
        public bool Applies { get; init; } = true;

        /// <summary>Whether a request is refused.</summary>
        public bool Refuses { get; init; }

        public List<RepeatMode> Requested { get; } = [];

        public IObservable<StateTransition<RepeatMode>> RepeatModeChanged => _repeatModeChanged;

        public void Change(RepeatMode mode)
        {
            var previous = RepeatMode;
            RepeatMode = mode;
            _repeatModeChanged.OnNext(new StateTransition<RepeatMode>(previous, mode));
        }

        public Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default)
        {
            Requested.Add(mode);
            if (Refuses)
                return Task.FromResult(Result.Fail(ErrorCategory.InvalidOperation, "refused"));
            if (Applies && mode != RepeatMode)
                Change(mode);
            return Task.FromResult(Result.Ok());
        }

        public PlaybackState State => PlaybackState.Paused;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public MediaInfo? MediaInfo => null;
        public bool SupportsVolumeControl => false;
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }

        public IObservable<PlaybackState> StateChanged { get; } = new Subject<PlaybackState>();
        public IObservable<TimeSpan> PositionTick { get; } = new Subject<TimeSpan>();
        public IObservable<LoopRestarted> LoopRestarted { get; } = new Subject<LoopRestarted>();
        public IObservable<LoopStalled> LoopStalled { get; } = new Subject<LoopStalled>();
        public IObservable<PlaybackError> ErrorOccurred { get; } = new Subject<PlaybackError>();

        public PlaybackDiagnosticsSnapshot GetDiagnostics() => PlaybackDiagnosticsSnapshot.Empty;

        public Task<Result> PlayAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());

        public Task<Result> PauseAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());

        public Task<Result> SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A minimal hot observable, delivering on the thread that pushes.</summary>
    private sealed class Subject<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];

        public IDisposable Subscribe(IObserver<T> observer)
        {
            _observers.Add(observer);
            return new Unsubscriber(() => _observers.Remove(observer));
        }

        public void OnNext(T value)
        {
            foreach (var observer in _observers.ToArray())
                observer.OnNext(value);
        }

        private sealed class Unsubscriber(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
