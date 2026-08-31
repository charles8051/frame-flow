// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia;

/// <summary>
/// Drop-in player control bundling <see cref="FrameFlowVideoView"/>
/// and <see cref="FrameFlowPlayerChrome"/> into the standard
/// overlay-on-video YouTube/Netflix layout: video fills the surface,
/// chrome sits at the bottom over a transparent-to-dark gradient,
/// auto-hides on pointer idle, fades back in on pointer movement.
/// Bind <see cref="MediaPlayer"/>; handle <see cref="FileOpenRequested"/>
/// to wire your build pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition.</b> This control is a thin wrapper: it sets up
/// the Grid + gradient Border + hover machinery and forwards
/// properties. The actual playback controls live in
/// <see cref="FrameFlowPlayerChrome"/>; the actual video rendering
/// lives in <see cref="FrameFlowVideoView"/>. Drop down to either
/// individually for custom layouts (e.g. the multicast example
/// embeds the chrome in its own row below multiple video panes).
/// </para>
/// <para>
/// <b>Player ownership.</b> The control NEVER builds an
/// <see cref="IMediaPlayer"/> itself — that's the consumer's job
/// via <c>FrameFlowPlayer.Open(path).BuildAsync()</c>. When the user
/// clicks Open (chrome's button) or drops a file (this view's
/// drag-drop), <see cref="FileOpenRequested"/> fires with the chosen
/// path; the consumer disposes any prior <see cref="MediaPlayer"/>,
/// builds a new one, and assigns it.
/// </para>
/// <para>
/// <b>Display-only path.</b> If you only need video rendering with
/// no controls, use <see cref="FrameFlowVideoView"/> directly.
/// </para>
/// </remarks>
public sealed class FrameFlowPlayerView : UserControl
{
    /// <summary>The player to display + control.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowPlayerView, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    /// <summary>Initial state of the Loop toggle. See
    /// <see cref="FrameFlowTransportBar.LoopByDefault"/>.</summary>
    public static readonly StyledProperty<bool> LoopByDefaultProperty =
        AvaloniaProperty.Register<FrameFlowPlayerView, bool>(nameof(LoopByDefault));

    /// <inheritdoc cref="LoopByDefaultProperty"/>
    public bool LoopByDefault
    {
        get => GetValue(LoopByDefaultProperty);
        set => SetValue(LoopByDefaultProperty, value);
    }

    /// <summary>
    /// Fires when the user clicks the Open button (and selects a
    /// file) or drops a file onto the control. Consumer handles
    /// teardown of the prior <see cref="MediaPlayer"/> and assigns
    /// the new one.
    /// </summary>
    public event EventHandler<FileOpenRequestedEventArgs>? FileOpenRequested;

    private IVideoSurface _surface;
    private Grid? _root;
    private readonly FrameFlowPlayerChrome _chrome;
    private Border? _chromeOverlay;

    public FrameFlowPlayerView()
    {
        Focusable = true;

        _surface = new FrameFlowVideoView();
        _chrome = new FrameFlowPlayerChrome
        {
            HasOpenButton = true,
            EnableKeyboardShortcuts = true,
        };
        _chrome.FileOpenRequested += OnChromeFileOpenRequested;
        // Pointer over the inner seek bar pins the chrome visible too —
        // the chrome panel's PointerEntered/Exited handles the
        // transport row, but the seek bar margin can extend outside
        // that handler's region depending on theme.
        _chrome.SeekBar.PointerEntered += (_, _) => _pointerOverChrome = true;
        _chrome.SeekBar.PointerExited += (_, _) => _pointerOverChrome = false;

        Content = BuildLayout();

        // Drag-drop on the whole player area (declared on the control,
        // handlers on this).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private Control BuildLayout()
    {
        // ┌─────────────────────────────────────────────────────────────┐
        // │                                                             │
        // │              FrameFlowVideoView (fills surface)             │
        // │                                                             │
        // │ ┌──── FrameFlowPlayerChrome over gradient scrim ──────────┐ │
        // │ │ status strip / seek bar / transport row                 │ │
        // │ └─────────────────────────────────────────────────────────┘ │
        // └─────────────────────────────────────────────────────────────┘
        _root = new Grid();
        _root.Children.Add(_surface.Control);

        // Bottom-aligned overlay with transparent → dark gradient so
        // chrome text reads against any video content. 24 px padding-
        // top gives the gradient room to fade in before the status
        // strip starts (YouTube/Netflix scrim look).
        _chromeOverlay = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 24, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(180, 0, 0, 0), 0.5),
                    new GradientStop(Color.FromArgb(230, 0, 0, 0), 1),
                },
            },
            Child = _chrome,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(250),
                },
            },
        };
        _chromeOverlay.PointerEntered += (_, _) => _pointerOverChrome = true;
        _chromeOverlay.PointerExited += (_, _) => _pointerOverChrome = false;
        _root.Children.Add(_chromeOverlay);

        return _root;
    }

    /// <summary>
    /// The video surface this player hosts. Defaults to a CPU
    /// <see cref="FrameFlowVideoView"/>; assign a different
    /// <see cref="IVideoSurface"/> (e.g. the Windows zero-copy presenter
    /// <c>FrameFlow.Avalonia.Windows.CompositionInteropVideoView</c>) before
    /// first playback to swap the rendering path while keeping the transport chrome.
    /// </summary>
    public IVideoSurface VideoSurface
    {
        get => _surface;
        set
        {
            if (value is null || ReferenceEquals(_surface, value))
                return;
            _root?.Children.Remove(_surface.Control);
            _surface = value;
            // Insert at 0 so the surface sits behind the chrome overlay (added last).
            _root?.Children.Insert(0, value.Control);
        }
    }

    /// <summary>
    /// Wires the logger and returns the hosted surface's <see cref="IVideoSink"/> to hand to
    /// <c>MediaPlayer.CreateAsync</c>. Pair with
    /// <see cref="IVideoSurface.PrefersHardwareFrames"/> on <see cref="VideoSurface"/> for the
    /// <c>yieldHardwareFrames</c> flag.
    /// </summary>
    public IVideoSink AttachSink(ILoggerFactory loggerFactory) => _surface.AttachSink(loggerFactory);

    /// <summary>
    /// The underlying <see cref="FrameFlowPlayerChrome"/> — exposed
    /// for callers who need to toggle <see cref="FrameFlowPlayerChrome.HasOpenButton"/>
    /// or <see cref="FrameFlowPlayerChrome.EnableKeyboardShortcuts"/>
    /// beyond what this wrapper offers.
    /// </summary>
    public FrameFlowPlayerChrome Chrome => _chrome;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaPlayerProperty)
        {
            var p = change.GetNewValue<IMediaPlayer?>();
            _chrome.MediaPlayer = p;

            // Wire the hover-to-reveal state subscription so the
            // chrome pins visible on Paused/Idle/Error and fades on
            // Playing-with-idle-pointer.
            _chromeStateSubscription?.Dispose();
            _chromeStateSubscription = null;
            if (p is not null)
            {
                OnTrackedStateChanged(p.State);
                _chromeStateSubscription = p
                    .StateChanged.ObserveOnUiThread()
                    .Subscribe(OnTrackedStateChanged);
            }
            else
            {
                _chromeHideTimer?.Stop();
                ShowChrome();
            }
        }
        else if (change.Property == LoopByDefaultProperty)
        {
            _chrome.LoopByDefault = change.GetNewValue<bool>();
        }
    }

    // ── File open: chrome button + drag-drop ────────────────────────────

    private void OnChromeFileOpenRequested(object? sender, FileOpenRequestedEventArgs e) =>
        FileOpenRequested?.Invoke(this, e);

    // Avalonia 11.4 introduces a new DataTransfer / DataFormat.File
    // API and deprecates the existing DragEventArgs.Data + DataFormats.Files
    // surface. We're on 11.3.x where the new API isn't fully fleshed out
    // yet, so we stick with the old surface and pin the suppression
    // here for easy removal when we move to 11.4+.
#pragma warning disable CS0618 // obsolete
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files))
            return;
        var files = e.Data.GetFiles();
        if (files is null)
            return;
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path is null)
                continue;
            FileOpenRequested?.Invoke(this, new FileOpenRequestedEventArgs(path));
            // First droppable file wins — matches the picker contract.
            break;
        }
    }
#pragma warning restore CS0618

    // ── Hover-to-reveal chrome overlay ──────────────────────────────────
    // The chrome overlay (status strip + seek bar + transport row)
    // auto-hides after a couple of seconds of pointer idle while
    // playback is active, fading back in on any pointer movement
    // (YouTube / Netflix pattern). When the player is paused / idle
    // / errored, the chrome stays visible — those are the states
    // where the user is most likely to want to scrub or hit Play.
    // Pointer-over-chrome pins it visible so it doesn't vanish while
    // you're reaching for the seek thumb or a transport button.
    private static readonly TimeSpan ChromeHideAfter = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _chromeHideTimer;
    private IDisposable? _chromeStateSubscription;
    private bool _pointerOverChrome;
    private TopLevel? _pointerHostTopLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pointerHostTopLevel = TopLevel.GetTopLevel(this);
        _pointerHostTopLevel?.AddHandler(
            PointerMovedEvent,
            OnTopLevelPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true
        );

        _chromeHideTimer = new DispatcherTimer(
            ChromeHideAfter,
            DispatcherPriority.Background,
            OnChromeHideTick
        );
        // Don't auto-start the timer; pointer movement starts it.
        ShowChrome();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pointerHostTopLevel?.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
        _pointerHostTopLevel = null;
        _chromeHideTimer?.Stop();
        _chromeHideTimer = null;
        _chromeStateSubscription?.Dispose();
        _chromeStateSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        // Any movement reveals the chrome and restarts the idle timer.
        ShowChrome();
        if (ShouldAutoHide())
            RestartHideTimer();
    }

    private void OnChromeHideTick(object? sender, EventArgs e)
    {
        _chromeHideTimer?.Stop();
        if (_pointerOverChrome || !ShouldAutoHide())
            return;
        if (_chromeOverlay is not null)
            _chromeOverlay.Opacity = 0;
    }

    private void ShowChrome()
    {
        if (_chromeOverlay is not null)
            _chromeOverlay.Opacity = 1;
    }

    private void RestartHideTimer()
    {
        if (_chromeHideTimer is null)
            return;
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    /// <summary>
    /// True when the player is in a state where the chrome should
    /// fade after a beat. Paused / idle / errored states keep the
    /// chrome pinned so scrubbing remains easy.
    /// </summary>
    private bool ShouldAutoHide()
    {
        var player = MediaPlayer;
        if (player is null)
            return false;
        return player.State == PlaybackState.Playing;
    }

    private void OnTrackedStateChanged(PlaybackState state)
    {
        if (state == PlaybackState.Playing)
            RestartHideTimer();
        else
        {
            // Paused, Ended, Error, Idle, … — pin chrome visible.
            _chromeHideTimer?.Stop();
            ShowChrome();
        }
    }
}

/// <summary>
/// Event payload for
/// <see cref="FrameFlowPlayerView.FileOpenRequested"/> and
/// <see cref="FrameFlowPlayerChrome.FileOpenRequested"/>. The
/// consumer inspects <see cref="FilePath"/>, builds an
/// <see cref="IMediaPlayer"/>, and assigns it to
/// <see cref="FrameFlowPlayerView.MediaPlayer"/>.
/// </summary>
public sealed class FileOpenRequestedEventArgs(string filePath) : EventArgs
{
    /// <summary>Local filesystem path of the file the user opened.</summary>
    public string FilePath { get; } = filePath;
}
