// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using FrameFlow.Avalonia.Core;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Play / Pause / Stop / Loop button row bound to an
/// <see cref="IMediaTransport"/>. Buttons enable/disable based on the
/// transport's current state; clicking dispatches the corresponding
/// async call on the transport.
/// </summary>
/// <remarks>
/// <para>
/// Does NOT include an Open button — file opening is a host concern
/// (needs <see cref="global::Avalonia.Platform.Storage.IStorageProvider"/>
/// from a parent Window). <see cref="FrameFlowPlayerView"/> wraps an
/// Open button + drag-drop alongside this transport bar.
/// </para>
/// <para>
/// The Loop toggle shows the transport's <see cref="IMediaTransport.RepeatMode"/> and follows
/// <see cref="IMediaTransport.RepeatModeChanged"/>. A click steps the mode from Off to One to All
/// and back to Off. One and All both show checked, labelled "Loop one" and "Loop all".
/// </para>
/// <para>
/// The bar stops following its transport when it leaves the visual tree. When it comes back, it
/// shows what the transport reports then and follows it again. A transport set while the bar is
/// out is followed from its return.
/// </para>
/// <para>
/// A refused <c>PlayAsync</c> / <c>PauseAsync</c> / <c>SeekAsync</c> /
/// <c>SetRepeatModeAsync</c> comes back as a <see cref="Result"/> and is
/// logged through Avalonia's logger by <c>TransportCommand</c> (ADR-0069).
/// The bar has no error affordance of its own: the buttons follow
/// <see cref="IMediaTransport.StateChanged"/> and the Loop toggle follows
/// <see cref="IMediaTransport.RepeatModeChanged"/>, so a refusal leaves them
/// where the transport says they belong. A consumer that wants to hear about
/// failures observes <see cref="IMediaTransport.ErrorOccurred"/>, which carries
/// failures during playback and any failure that puts the player into
/// <see cref="PlaybackState.Error"/>, a Play clicked here included.
/// </para>
/// </remarks>
public sealed class FrameFlowTransportBar : StackPanel
{
    /// <summary>The transport to control.</summary>
    public static readonly StyledProperty<IMediaTransport?> TransportProperty =
        AvaloniaProperty.Register<FrameFlowTransportBar, IMediaTransport?>(nameof(Transport));

    /// <inheritdoc cref="TransportProperty"/>
    public IMediaTransport? Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    /// <summary>
    /// Whether binding a transport that is not repeating sets it to
    /// <see cref="RepeatMode.One"/>. Consumers who want loop-by-default
    /// (e.g. demo apps with short clips) set this to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Read when <see cref="Transport"/> is set, and not again when the bar leaves
    /// the visual tree and comes back. A transport already in
    /// <see cref="RepeatMode.One"/> or <see cref="RepeatMode.All"/> keeps its
    /// mode. The Loop toggle shows the transport's mode either way.
    /// </remarks>
    public static readonly StyledProperty<bool> LoopByDefaultProperty =
        AvaloniaProperty.Register<FrameFlowTransportBar, bool>(nameof(LoopByDefault));

    /// <inheritdoc cref="LoopByDefaultProperty"/>
    public bool LoopByDefault
    {
        get => GetValue(LoopByDefaultProperty);
        set => SetValue(LoopByDefaultProperty, value);
    }

    private readonly Button _playButton;
    private readonly Button _pauseButton;
    private readonly Button _stopButton;
    private readonly ToggleButton _loopButton;

    private readonly TransportFollower _follower;

    public FrameFlowTransportBar()
    {
        _follower = new TransportFollower(FollowTransport);

        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;

        _playButton = NewButton("Play", OnPlayClick);
        _pauseButton = NewButton("Pause", OnPauseClick);
        _stopButton = NewButton("Stop", OnStopClick);

        _loopButton = new ToggleButton { Margin = new Thickness(4), IsEnabled = false };
        _loopButton.Click += OnLoopClick;
        ShowRepeatMode(RepeatMode.Off);

        Children.Add(_playButton);
        Children.Add(_pauseButton);
        Children.Add(_stopButton);
        Children.Add(_loopButton);

        UpdateButtonsForState(null);
    }

    private static Button NewButton(
        string content,
        EventHandler<global::Avalonia.Interactivity.RoutedEventArgs> click
    )
    {
        var b = new Button { Content = content, Margin = new Thickness(4), IsEnabled = false };
        b.Click += click;
        return b;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TransportProperty)
            OnTransportChanged(change.GetNewValue<IMediaTransport?>());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Back in the tree with the transport it left with: follow it again from what it reports
        // now. LoopByDefault is not asked for again, because it is a request made on bind.
        _follower.Resume(Transport);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _follower.Suspend();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTransportChanged(IMediaTransport? transport)
    {
        _follower.Rebind(transport);

        if (transport is null)
        {
            UpdateButtonsForState(null);
            ShowRepeatMode(RepeatMode.Off);
            _loopButton.IsEnabled = false;
            return;
        }

        _loopButton.IsEnabled = true;
        if (LoopToggle.OnBind(transport.RepeatMode, LoopByDefault) is { } requested)
            TransportCommand.FireAndForget(
                this,
                nameof(IMediaTransport.SetRepeatModeAsync),
                () => transport.SetRepeatModeAsync(requested)
            );
    }

    // Following the transport, on bind and on every return to the tree. The follow drops a change
    // posted before it stopped, so one from a transport this bar has since let go of does not show.
    private void FollowTransport(IMediaTransport transport, TransportFollower.Follow follow)
    {
        follow.On(transport.RepeatModeChanged, t => ShowRepeatMode(t.Current));
        follow.On(transport.StateChanged, s => UpdateButtonsForState(s));
        ShowRepeatMode(transport.RepeatMode);
        UpdateButtonsForState(transport.State);
    }

    private void UpdateButtonsForState(PlaybackState? state)
    {
        _playButton.IsEnabled = state is PlaybackState.Paused or PlaybackState.Ended;
        _pauseButton.IsEnabled = state is PlaybackState.Playing;
        _stopButton.IsEnabled = state is PlaybackState.Playing or PlaybackState.Paused;
    }

    private void ShowRepeatMode(RepeatMode mode)
    {
        var face = LoopToggle.Face(mode);
        _loopButton.IsChecked = face.IsChecked;
        _loopButton.Content = face.Label;
    }

    private void OnPlayClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Transport is { } p)
            TransportCommand.FireAndForget(this, nameof(IMediaTransport.PlayAsync), () => p.PlayAsync());
    }

    private void OnPauseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Transport is { } p)
            TransportCommand.FireAndForget(this, nameof(IMediaTransport.PauseAsync), () => p.PauseAsync());
    }

    private void OnStopClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Transport is not { } p)
            return;

        // Stop is a pause followed by a rewind. If the pause is refused there
        // is nothing to rewind to, so its Result is what the caller hears
        // about. Under the old exception model the throw skipped the seek
        // implicitly; this says so.
        TransportCommand.FireAndForget(
            this,
            "Stop",
            async () =>
            {
                var paused = await p.PauseAsync().ConfigureAwait(true);
                return paused.IsSuccess
                    ? await p.SeekAsync(TimeSpan.Zero).ConfigureAwait(true)
                    : paused;
            }
        );
    }

    private void OnLoopClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Transport is not { } p)
            return;

        // The click has already flipped the toggle. Put it back to the mode the transport
        // reports: the toggle moves when RepeatModeChanged says the mode moved, so a refused
        // command leaves it on the mode still in force, and there is nothing to roll back.
        // A second click that lands before the first request is applied reads the same mode and
        // asks for the same next one, so it is absorbed rather than stepping twice.
        var current = p.RepeatMode;
        ShowRepeatMode(current);
        var next = LoopToggle.Next(current);
        TransportCommand.FireAndForget(
            this,
            nameof(IMediaTransport.SetRepeatModeAsync),
            () => p.SetRepeatModeAsync(next)
        );
    }
}
