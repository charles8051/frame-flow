using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;
using FrameFlow.Player;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// A chrome control that leaves the visual tree and comes back with the same transport follows it
/// again (#643). The badge, the stream summary, the transport bar and the player view let go of
/// their subscriptions on detach, and used to take them up only when <c>Transport</c> was set, so
/// after a reattach they kept showing what they showed at the detach.
/// </summary>
/// <remarks>
/// The controls marshal the transport's observables to the UI thread by posting, so
/// <see cref="Dispatcher.RunJobs()"/> is the barrier between a change and what a control shows.
/// Each detach and reattach is checked against the window, so a test cannot pass by never leaving
/// the tree.
/// </remarks>
public sealed class ChromeReattachTests
{
    // ── State badge ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void StateBadge_AfterAReattach_FollowsTheTransport()
    {
        var transport = new FakeTransport { State = PlaybackState.Paused };
        var badge = new FrameFlowStateBadge { Transport = transport };
        var window = Show(badge);
        try
        {
            Reattach(window, badge);

            transport.Push(PlaybackState.Playing);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Playing", badge.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StateBadge_AfterAReattach_ShowsAChangeMadeWhileItWasOut()
    {
        var transport = new FakeTransport { State = PlaybackState.Paused };
        var badge = new FrameFlowStateBadge { Transport = transport };
        var window = Show(badge);
        try
        {
            Detach(window, badge);
            transport.Push(PlaybackState.Playing);
            Dispatcher.UIThread.RunJobs();
            Attach(window, badge);

            Assert.Equal("Playing", badge.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StateBadge_HoldsOneSubscription_InTheTree_AndNoneOut()
    {
        var transport = new FakeTransport();
        // Bound before it joins the tree, so the first attach finds the bind's subscription live.
        var badge = new FrameFlowStateBadge { Transport = transport };
        var window = Show(badge);
        try
        {
            Assert.Equal(1, transport.StateChangedSubject.Count);

            Detach(window, badge);
            Assert.Equal(0, transport.StateChangedSubject.Count);

            Attach(window, badge);
            Assert.Equal(1, transport.StateChangedSubject.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StateBadge_ATransportBoundWhileOut_IsFollowedFromTheReturn()
    {
        var first = new FakeTransport { State = PlaybackState.Paused };
        var badge = new FrameFlowStateBadge { Transport = first };
        var window = Show(badge);
        try
        {
            Detach(window, badge);
            var second = new FakeTransport { State = PlaybackState.Playing };
            badge.Transport = second;

            Assert.Equal(0, first.StateChangedSubject.Count);
            Assert.Equal(0, second.StateChangedSubject.Count);

            Attach(window, badge);
            Assert.Equal(1, second.StateChangedSubject.Count);
            Assert.Equal("Playing", badge.Text);

            second.Push(PlaybackState.Ended);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ended", badge.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StateBadge_AChangePostedBeforeTheDetach_DoesNotOverwriteWhatTheReattachRead()
    {
        var transport = new FakeTransport { State = PlaybackState.Paused };
        var badge = new FrameFlowStateBadge { Transport = transport };
        var window = Show(badge);
        try
        {
            // Raised while in the tree and posted, still undelivered when the badge leaves.
            transport.Push(PlaybackState.Playing);
            Detach(window, badge);
            // Raised with nobody subscribed: only the reattach's read sees it.
            transport.Push(PlaybackState.Ended);
            Attach(window, badge);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Ended", badge.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StateBadge_AChangePostedByATransportSinceUnbound_DoesNotShow()
    {
        var first = new FakeTransport { State = PlaybackState.Paused };
        var badge = new FrameFlowStateBadge { Transport = first };

        // Raised while bound, delivered after the rebind.
        first.Push(PlaybackState.Playing);
        badge.Transport = new FakeTransport { State = PlaybackState.Idle };
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Idle", badge.Text);
    }

    // ── Stream summary ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void StreamSummary_AfterAReattach_FollowsTheTransport()
    {
        var transport = new FakeTransport { State = PlaybackState.Loading };
        var summary = new FrameFlowStreamSummary { Transport = transport };
        var window = Show(summary);
        try
        {
            Reattach(window, summary);

            transport.MediaInfo = Clip;
            transport.Push(PlaybackState.Paused);
            Dispatcher.UIThread.RunJobs();

            Assert.StartsWith("h264  1920x1080", summary.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StreamSummary_AfterAReattach_ShowsMediaLoadedWhileItWasOut()
    {
        var transport = new FakeTransport { State = PlaybackState.Loading };
        var summary = new FrameFlowStreamSummary { Transport = transport };
        var window = Show(summary);
        try
        {
            Detach(window, summary);
            transport.MediaInfo = Clip;
            transport.Push(PlaybackState.Paused);
            Dispatcher.UIThread.RunJobs();
            Attach(window, summary);

            Assert.StartsWith("h264  1920x1080", summary.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Transport bar ────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TransportBar_AfterAReattach_FollowsTheTransportsState()
    {
        var transport = new FakeTransport { State = PlaybackState.Paused };
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Reattach(window, bar);

            transport.Push(PlaybackState.Playing);
            Dispatcher.UIThread.RunJobs();

            Assert.False(ButtonNamed(bar, "Play").IsEnabled);
            Assert.True(ButtonNamed(bar, "Pause").IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TransportBar_AfterAReattach_FollowsTheTransportsRepeatMode()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.Off };
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Reattach(window, bar);

            transport.ChangeRepeatMode(RepeatMode.All);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Loop all", LoopButton(bar).Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TransportBar_AfterAReattach_ShowsChangesMadeWhileItWasOut()
    {
        var transport = new FakeTransport { State = PlaybackState.Paused, RepeatMode = RepeatMode.Off };
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Detach(window, bar);
            transport.Push(PlaybackState.Playing);
            transport.ChangeRepeatMode(RepeatMode.All);
            Dispatcher.UIThread.RunJobs();
            Attach(window, bar);

            Assert.True(ButtonNamed(bar, "Pause").IsEnabled);
            Assert.Equal("Loop all", LoopButton(bar).Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TransportBar_AReattach_DoesNotAskForLoopByDefaultAgain()
    {
        var transport = new FakeTransport { RepeatMode = RepeatMode.Off };
        var bar = new FrameFlowTransportBar { LoopByDefault = true, Transport = transport };
        var window = Show(bar);
        try
        {
            Assert.Equal(new[] { RepeatMode.One }, transport.Requested);
            // The app turns looping off after the bind; the bar is not to turn it back on.
            transport.ChangeRepeatMode(RepeatMode.Off);
            Dispatcher.UIThread.RunJobs();

            Reattach(window, bar);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { RepeatMode.One }, transport.Requested);
            Assert.Equal(RepeatMode.Off, transport.RepeatMode);
            Assert.Equal("Loop", LoopButton(bar).Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TransportBar_HoldsOneSubscriptionEach_InTheTree_AndNoneOut()
    {
        var transport = new FakeTransport();
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Assert.Equal(1, transport.StateChangedSubject.Count);
            Assert.Equal(1, transport.RepeatModeChangedSubject.Count);

            Detach(window, bar);
            Assert.Equal(0, transport.StateChangedSubject.Count);
            Assert.Equal(0, transport.RepeatModeChangedSubject.Count);

            Attach(window, bar);
            Assert.Equal(1, transport.StateChangedSubject.Count);
            Assert.Equal(1, transport.RepeatModeChangedSubject.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TransportBar_ATransportBoundWhileOut_IsFollowedFromTheReturn()
    {
        var bar = new FrameFlowTransportBar { Transport = new FakeTransport() };
        var window = Show(bar);
        try
        {
            Detach(window, bar);
            var next = new FakeTransport { State = PlaybackState.Playing, RepeatMode = RepeatMode.All };
            bar.Transport = next;

            Assert.Equal(0, next.StateChangedSubject.Count);
            Assert.Equal(0, next.RepeatModeChangedSubject.Count);

            Attach(window, bar);
            Assert.Equal(1, next.StateChangedSubject.Count);
            Assert.Equal(1, next.RepeatModeChangedSubject.Count);
            Assert.True(ButtonNamed(bar, "Pause").IsEnabled);
            Assert.Equal("Loop all", LoopButton(bar).Content);
        }
        finally
        {
            window.Close();
        }
    }

    // The bar subscribes to the repeat mode first, so a transport that refuses only its state
    // leaves the bar holding a subscription unless the refused follow is let go of whole.

    [AvaloniaFact]
    public void TransportBar_ABindRefusedPartWay_LeavesNoSubscriptionBehind()
    {
        var transport = new FakeTransport();
        transport.StateChangedSubject.Dispose();
        var bar = new FrameFlowTransportBar();

        Assert.Throws<ObjectDisposedException>(() => bar.Transport = transport);

        Assert.Equal(0, transport.RepeatModeChangedSubject.Count);
    }

    [AvaloniaFact]
    public void TransportBar_AReattachRefusedPartWay_LeavesNoSubscriptionBehind()
    {
        var transport = new FakeTransport();
        var bar = new FrameFlowTransportBar { Transport = transport };
        var window = Show(bar);
        try
        {
            Detach(window, bar);
            transport.StateChangedSubject.Dispose();

            Attach(window, bar);

            Assert.Equal(0, transport.RepeatModeChangedSubject.Count);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Player view ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void PlayerView_AfterAReattach_PinsTheChromeVisibleOnAPause()
    {
        var transport = new FakeTransport { State = PlaybackState.Playing };
        var view = new FrameFlowPlayerView { Transport = transport };
        var window = Show(view);
        try
        {
            Reattach(window, view);

            // Hidden, as the idle timer leaves it during playback. The fade is a transition, which
            // would put real elapsed time between the write and the value; without it the write
            // lands at once.
            var overlay = Assert.IsType<Border>(view.Chrome.Parent);
            overlay.Transitions = null;
            overlay.Opacity = 0;

            transport.Push(PlaybackState.Paused);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, overlay.Opacity);
            Assert.Equal("Paused", Descendant<FrameFlowStateBadge>(view).Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PlayerView_AReattach_WithATransportDisposedWhileOut_DoesNotThrow()
    {
        var transport = new FakeTransport { State = PlaybackState.Paused };
        var view = new FrameFlowPlayerView { Transport = transport };
        var window = Show(view);
        try
        {
            Detach(window, view);
            // The documented swap disposes the old transport before the new one is bound.
            transport.Dispose();

            Attach(window, view);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Paused", Descendant<FrameFlowStateBadge>(view).Text);

            // The next transport bound is followed as usual.
            var next = new FakeTransport { State = PlaybackState.Paused };
            view.Transport = next;
            next.Push(PlaybackState.Playing);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Playing", Descendant<FrameFlowStateBadge>(view).Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly MediaInfo Clip = new(
        "mov,mp4,m4a,3gp,3g2,mj2",
        TimeSpan.FromSeconds(5),
        [new VideoStreamInfo(0, "h264", 1920, 1080, 30.0)],
        [new AudioStreamInfo(1, "aac", 48000, 2)]
    );

    // The theme goes in first: without one the window has no template to present its content
    // through, and the control never joins the visual tree.
    private static Window Show(Control control)
    {
        var window = new Window { Width = 800, Height = 200 };
        window.Styles.Add(new FluentTheme());
        window.Content = control;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(window, TopLevel.GetTopLevel(control));
        return window;
    }

    private static void Detach(Window window, Control control)
    {
        window.Content = null;
        Assert.Null(TopLevel.GetTopLevel(control));
    }

    private static void Attach(Window window, Control control)
    {
        window.Content = control;
        Assert.Same(window, TopLevel.GetTopLevel(control));
    }

    // As a host that swaps a panel's content does: out and back in one go.
    private static void Reattach(Window window, Control control)
    {
        Detach(window, control);
        Attach(window, control);
    }

    private static Button ButtonNamed(FrameFlowTransportBar bar, string content) =>
        bar.Children.OfType<Button>().Single(b => b is not ToggleButton && (string?)b.Content == content);

    private static ToggleButton LoopButton(FrameFlowTransportBar bar) =>
        bar.Children.OfType<ToggleButton>().Single();

    private static T Descendant<T>(Control root)
        where T : Control => root.GetLogicalDescendants().OfType<T>().Single();

    /// <summary>
    /// An <see cref="IMediaTransport"/> whose state, repeat mode and media the test drives. It
    /// counts subscribers, and once disposed refuses a subscription, as the player does.
    /// </summary>
    private sealed class FakeTransport : IMediaTransport, IDisposable
    {
        public Subject<PlaybackState> StateChangedSubject { get; } = new();
        public Subject<StateTransition<RepeatMode>> RepeatModeChangedSubject { get; } = new();

        public PlaybackState State { get; set; } = PlaybackState.Paused;
        public RepeatMode RepeatMode { get; set; }
        public MediaInfo? MediaInfo { get; set; }

        public List<RepeatMode> Requested { get; } = [];

        public IObservable<PlaybackState> StateChanged => StateChangedSubject;
        public IObservable<StateTransition<RepeatMode>> RepeatModeChanged => RepeatModeChangedSubject;

        public void Push(PlaybackState state)
        {
            State = state;
            StateChangedSubject.OnNext(state);
        }

        public void ChangeRepeatMode(RepeatMode mode)
        {
            var previous = RepeatMode;
            RepeatMode = mode;
            RepeatModeChangedSubject.OnNext(new StateTransition<RepeatMode>(previous, mode));
        }

        // Adopts the mode and reports it before completing, as the player does.
        public Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default)
        {
            Requested.Add(mode);
            if (mode != RepeatMode)
                ChangeRepeatMode(mode);
            return Task.FromResult(Result.Ok());
        }

        public void Dispose()
        {
            StateChangedSubject.Dispose();
            RepeatModeChangedSubject.Dispose();
        }

        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public bool SupportsVolumeControl => false;
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }

        public IObservable<TimeSpan> PositionTick { get; } = new Subject<TimeSpan>();
        public IObservable<LoopRestarted> LoopRestarted { get; } = new Subject<LoopRestarted>();
        public IObservable<LoopStalled> LoopStalled { get; } = new Subject<LoopStalled>();
        public IObservable<PlaybackError> ErrorOccurred { get; } = new Subject<PlaybackError>();

        public PlaybackDiagnosticsSnapshot GetDiagnostics() => PlaybackDiagnosticsSnapshot.Empty;

        public Task<Result> PlayAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());

        public Task<Result> PauseAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());

        public Task<Result> SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A minimal hot observable, delivering on the thread that pushes. Counts its subscribers, and
    /// once disposed refuses new ones with <see cref="ObjectDisposedException"/>.
    /// </summary>
    private sealed class Subject<T> : IObservable<T>, IDisposable
    {
        private readonly List<IObserver<T>> _observers = [];
        private bool _disposed;

        public int Count => _observers.Count;

        public IDisposable Subscribe(IObserver<T> observer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observers.Add(observer);
            return new Unsubscriber(() => _observers.Remove(observer));
        }

        public void OnNext(T value)
        {
            foreach (var observer in _observers.ToArray())
                observer.OnNext(value);
        }

        public void Dispose()
        {
            _disposed = true;
            _observers.Clear();
        }

        private sealed class Unsubscriber(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
