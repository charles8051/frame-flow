// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Standalone player chrome — the controls UI of a media player
/// (status badge, stream summary, position label, seek bar, transport
/// buttons, volume control, optional Open button + keyboard shortcuts)
/// without the video surface. Bind the <see cref="MediaPlayer"/>
/// property and the panel renders + drives playback for whatever
/// <see cref="FrameFlowVideoView"/> (or other view) is showing that
/// player's output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own control.</b> <see cref="FrameFlowPlayerView"/>
/// composes a <see cref="FrameFlowVideoView"/> and a chrome panel into
/// a single overlay-on-video drop-in. Some consumers don't want that
/// layout — multi-pane multicast viewers, custom-skinned hosts,
/// fullscreen variants with chrome positioned outside the video
/// rectangle. Splitting the chrome out lets those hosts use the same
/// well-tested controls (seek bar, transport, volume, shortcuts)
/// without having to either reimplement them or accept the bundled
/// player's overlay layout.
/// </para>
/// <para>
/// <b>What this control owns and does not own.</b> It owns: forwarding
/// <see cref="MediaPlayer"/> to every sub-control, the file picker
/// behind the optional Open button, and the TopLevel keyboard
/// shortcut handler (Space / M / ←/→). It does NOT own: hover-to-
/// reveal opacity transitions, drag-drop, the gradient background a
/// player view might want when overlaying video. Those decorations
/// live in <see cref="FrameFlowPlayerView"/> (the wrapper) or are the
/// consumer's responsibility in custom hosts.
/// </para>
/// </remarks>
public sealed class FrameFlowPlayerChrome : UserControl
{
    /// <summary>The player to display + control.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowPlayerChrome, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    /// <summary>Initial state of the Loop toggle. See
    /// <see cref="FrameFlowTransportBar.LoopByDefault"/>.</summary>
    public static readonly StyledProperty<bool> LoopByDefaultProperty =
        AvaloniaProperty.Register<FrameFlowPlayerChrome, bool>(nameof(LoopByDefault));

    /// <inheritdoc cref="LoopByDefaultProperty"/>
    public bool LoopByDefault
    {
        get => GetValue(LoopByDefaultProperty);
        set => SetValue(LoopByDefaultProperty, value);
    }

    /// <summary>
    /// Whether to show the built-in Open button (left of the transport
    /// row). Default <c>true</c>. Set <c>false</c> in hosts that
    /// already publish their own file picker (e.g. the multicast
    /// example, which has Open + Loop in its own top bar).
    /// </summary>
    public static readonly StyledProperty<bool> HasOpenButtonProperty =
        AvaloniaProperty.Register<FrameFlowPlayerChrome, bool>(
            nameof(HasOpenButton),
            defaultValue: true
        );

    /// <inheritdoc cref="HasOpenButtonProperty"/>
    public bool HasOpenButton
    {
        get => GetValue(HasOpenButtonProperty);
        set => SetValue(HasOpenButtonProperty, value);
    }

    /// <summary>
    /// Whether to install the TopLevel keyboard shortcut handler
    /// (Space = Play/Pause, M = Mute, ←/→ = ±5 s seek). Default
    /// <c>true</c>. Set <c>false</c> when the host wants exclusive
    /// keyboard control (e.g. a multi-player layout where each
    /// chrome instance would otherwise fight for the same keys).
    /// Changing the value after the control is attached has no
    /// effect until the next detach/attach cycle — read once at
    /// attach time.
    /// </summary>
    public static readonly StyledProperty<bool> EnableKeyboardShortcutsProperty =
        AvaloniaProperty.Register<FrameFlowPlayerChrome, bool>(
            nameof(EnableKeyboardShortcuts),
            defaultValue: true
        );

    /// <inheritdoc cref="EnableKeyboardShortcutsProperty"/>
    public bool EnableKeyboardShortcuts
    {
        get => GetValue(EnableKeyboardShortcutsProperty);
        set => SetValue(EnableKeyboardShortcutsProperty, value);
    }

    /// <summary>
    /// Fires when the user clicks the built-in Open button (and
    /// picks a file). The consumer handles teardown of the prior
    /// <see cref="MediaPlayer"/> and assigns the new one.
    /// </summary>
    public event EventHandler<FileOpenRequestedEventArgs>? FileOpenRequested;

    private readonly FrameFlowStateBadge _stateBadge;
    private readonly FrameFlowStreamSummary _streamSummary;
    private readonly FrameFlowPositionLabel _positionLabel;
    private readonly FrameFlowSeekBar _seekBar;
    private readonly FrameFlowTransportBar _transportBar;
    private readonly FrameFlowVolumeControl _volumeControl;
    private readonly Button _openButton;

    private TopLevel? _keyHostTopLevel;

    public FrameFlowPlayerChrome()
    {
        _stateBadge = new FrameFlowStateBadge();
        _streamSummary = new FrameFlowStreamSummary { Margin = new Thickness(16, 0) };
        _positionLabel = new FrameFlowPositionLabel();
        _seekBar = new FrameFlowSeekBar { Margin = new Thickness(10, 0) };
        _transportBar = new FrameFlowTransportBar();
        _volumeControl = new FrameFlowVolumeControl();
        _openButton = new Button { Content = "Open", Margin = new Thickness(4) };
        _openButton.Click += OnOpenClick;

        Content = BuildLayout();
    }

    /// <summary>Exposes the inner seek bar for hosts that need to
    /// reference it (e.g. for hover-to-reveal pointer tracking).</summary>
    public FrameFlowSeekBar SeekBar => _seekBar;

    private Control BuildLayout()
    {
        // ┌──────────────────────────────────────────────────────────┐
        // │ [state]  [stream summary]              [position 0:30] │  status
        // │ [══════════════════●═══════════════════════════════════]│  seek bar
        // │ [Open] [▶][⏸][⏹][↻]              [🔊 ▬▬●▬▬ 80%]      │  transport
        // └──────────────────────────────────────────────────────────┘
        var statusGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(12, 8, 12, 0),
        };
        Grid.SetColumn(_stateBadge, 0);
        Grid.SetColumn(_streamSummary, 1);
        Grid.SetColumn(_positionLabel, 2);
        statusGrid.Children.Add(_stateBadge);
        statusGrid.Children.Add(_streamSummary);
        statusGrid.Children.Add(_positionLabel);

        var transportGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 4, 8, 8),
        };
        var leftStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftStack.Children.Add(_openButton);
        leftStack.Children.Add(_transportBar);
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(_volumeControl, 2);
        transportGrid.Children.Add(leftStack);
        transportGrid.Children.Add(_volumeControl);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(statusGrid);
        stack.Children.Add(_seekBar);
        stack.Children.Add(transportGrid);
        return stack;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaPlayerProperty)
        {
            var p = change.GetNewValue<IMediaPlayer?>();
            _stateBadge.MediaPlayer = p;
            _streamSummary.MediaPlayer = p;
            _positionLabel.MediaPlayer = p;
            _seekBar.MediaPlayer = p;
            _transportBar.MediaPlayer = p;
            _volumeControl.MediaPlayer = p;
        }
        else if (change.Property == LoopByDefaultProperty)
        {
            _transportBar.LoopByDefault = change.GetNewValue<bool>();
        }
        else if (change.Property == HasOpenButtonProperty)
        {
            _openButton.IsVisible = change.GetNewValue<bool>();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!EnableKeyboardShortcuts)
            return;
        _keyHostTopLevel = TopLevel.GetTopLevel(this);
        // handledEventsToo:true so we still see Space/M even if a
        // focused button has set e.Handled = true on its own KeyDown
        // (Avalonia buttons accept Space as a click trigger by default).
        _keyHostTopLevel?.AddHandler(
            KeyDownEvent,
            OnTopLevelKeyDown,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHostTopLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _keyHostTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ── File picker ─────────────────────────────────────────────────────

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open Media File",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Media Files")
                    {
                        Patterns =
                        [
                            "*.mp4",
                            "*.mkv",
                            "*.webm",
                            "*.avi",
                            "*.mov",
                            "*.m4a",
                            "*.mp3",
                            "*.ogg",
                            "*.flac",
                            "*.wav",
                        ],
                    },
                    new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                ],
            }
        );

        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;
        FileOpenRequested?.Invoke(this, new FileOpenRequestedEventArgs(path));
    }

    // ── Keyboard shortcuts ──────────────────────────────────────────────

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        var player = MediaPlayer;
        if (player is null)
            return;

        // Don't steal keys destined for a focused text input — the
        // host might have a log textbox or filename field where the
        // user is typing.
        if (e.Source is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Space:
                TogglePlayPause(player);
                e.Handled = true;
                break;
            case Key.M:
                // Ignore the shortcut when the sink has no gain stage; the
                // write would be a no-op and the glyph would flip to a mute
                // state the audio never entered.
                if (player.SupportsVolumeControl)
                {
                    player.Muted = !player.Muted;
                    // Muted has no observable today; nudge the volume
                    // glyph to re-read so the icon stays in sync.
                    _volumeControl.RefreshFromPlayer();
                }
                e.Handled = true;
                break;
            case Key.Left:
                FireAndForget(() => player.SeekAsync(SeekBy(player, -5)));
                e.Handled = true;
                break;
            case Key.Right:
                FireAndForget(() => player.SeekAsync(SeekBy(player, +5)));
                e.Handled = true;
                break;
        }
    }

    private static void TogglePlayPause(IMediaPlayer player)
    {
        FireAndForget(async () =>
        {
            if (player.State == PlaybackState.Playing)
                await player.PauseAsync();
            else
                await player.PlayAsync();
        });
    }

    private static TimeSpan SeekBy(IMediaPlayer player, double seconds)
    {
        var target = player.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero)
            target = TimeSpan.Zero;
        var duration = player.Duration;
        // Clamp shy of duration — landing exactly on Ended drains
        // the audio pre-buffer for ~800 ms while the video pump
        // finds nothing past EOF to render, producing a silent
        // stall. 500 ms margin keeps playback alive after a near-end
        // seek. (Related: the playback clock keeps ticking after
        // Ended — tracked in docs/DEFERRED_WORK.md.)
        var ceiling = duration - TimeSpan.FromMilliseconds(500);
        if (ceiling < TimeSpan.Zero)
            ceiling = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && target > ceiling)
            target = ceiling;
        return target;
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
