// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Play / Pause / Stop / Loop button row bound to an
/// <see cref="IMediaPlayer"/>. Buttons enable/disable based on the
/// player's current state; clicking dispatches the corresponding
/// async call on the player.
/// </summary>
/// <remarks>
/// <para>
/// Does NOT include an Open button — file opening is a host concern
/// (needs <see cref="global::Avalonia.Platform.Storage.IStorageProvider"/>
/// from a parent Window). <see cref="FrameFlowPlayerView"/> wraps an
/// Open button + drag-drop alongside this transport bar.
/// </para>
/// <para>
/// Errors from <c>PlayAsync</c> / <c>PauseAsync</c> / <c>SeekAsync</c>
/// / <c>SetRepeatModeAsync</c> are caught and silently swallowed —
/// consumers wanting full error visibility should observe
/// <see cref="IMediaPlayer.StateChanged"/> directly (the controller
/// transitions to Error on fatal failures regardless).
/// </para>
/// </remarks>
public sealed class FrameFlowTransportBar : StackPanel
{
    /// <summary>The player to control.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowTransportBar, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    /// <summary>
    /// Initial state of the Loop toggle. Consumers who want loop-by-default
    /// (e.g. demo apps with short clips) set this to <see langword="true"/>.
    /// The transport bar reflects subsequent user clicks regardless.
    /// </summary>
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

    private IDisposable? _stateSubscription;

    public FrameFlowTransportBar()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;

        _playButton = NewButton("Play", OnPlayClick);
        _pauseButton = NewButton("Pause", OnPauseClick);
        _stopButton = NewButton("Stop", OnStopClick);

        _loopButton = new ToggleButton
        {
            Content = "Loop",
            Margin = new Thickness(4),
            IsEnabled = false,
        };
        _loopButton.Click += OnLoopClick;

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
        if (change.Property == MediaPlayerProperty)
            OnMediaPlayerChanged(change.GetNewValue<IMediaPlayer?>());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stateSubscription?.Dispose();
        _stateSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnMediaPlayerChanged(IMediaPlayer? player)
    {
        _stateSubscription?.Dispose();
        _stateSubscription = null;

        if (player is null)
        {
            UpdateButtonsForState(null);
            _loopButton.IsEnabled = false;
            return;
        }

        _loopButton.IsEnabled = true;
        _loopButton.IsChecked = LoopByDefault;
        // Apply the initial loop preference to the freshly-bound player.
        if (LoopByDefault)
            FireAndForget(() => player.SetRepeatModeAsync(RepeatMode.One));

        UpdateButtonsForState(player.State);
        _stateSubscription = player
            .StateChanged.ObserveOnUiThread()
            .Subscribe(s => UpdateButtonsForState(s));
    }

    private void UpdateButtonsForState(PlaybackState? state)
    {
        _playButton.IsEnabled = state is PlaybackState.Paused or PlaybackState.Ended;
        _pauseButton.IsEnabled = state is PlaybackState.Playing;
        _stopButton.IsEnabled = state is PlaybackState.Playing or PlaybackState.Paused;
    }

    private async void OnPlayClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MediaPlayer is { } p)
            try
            {
                await p.PlayAsync();
            }
            catch { }
    }

    private async void OnPauseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MediaPlayer is { } p)
            try
            {
                await p.PauseAsync();
            }
            catch { }
    }

    private async void OnStopClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MediaPlayer is { } p)
            try
            {
                await p.PauseAsync();
                await p.SeekAsync(TimeSpan.Zero);
            }
            catch { }
    }

    private async void OnLoopClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MediaPlayer is not { } p)
            return;
        var mode = _loopButton.IsChecked == true ? RepeatMode.One : RepeatMode.Off;
        try
        {
            await p.SetRepeatModeAsync(mode);
        }
        catch { }
    }

    private static async void FireAndForget(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch { }
    }
}
