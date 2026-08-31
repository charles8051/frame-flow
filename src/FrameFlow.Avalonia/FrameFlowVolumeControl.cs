// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Composite volume widget: mute toggle (speaker glyph) + slider +
/// percentage label. Bound to an <see cref="IMediaPlayer"/>'s
/// <see cref="IMediaPlayer.Volume"/> / <see cref="IMediaPlayer.Muted"/>
/// properties. Settings persist across re-binds because they live on
/// the underlying audio sink, not on the control.
/// </summary>
/// <remarks>
/// <para>
/// The glyph picks among 🔇 / 🔈 / 🔉 / 🔊 based on the
/// effective level: muted shows 🔇, otherwise the level bucket
/// (off / quiet / medium / loud). Matches the AvaloniaPlayer example.
/// </para>
/// <para>
/// The slider is disabled until <see cref="MediaPlayer"/> is non-null
/// so users can't fling the slider before a player exists.
/// </para>
/// </remarks>
public sealed class FrameFlowVolumeControl : StackPanel
{
    /// <summary>The player whose volume/mute to control.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowVolumeControl, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private readonly ToggleButton _muteButton;
    private readonly Slider _slider;
    private readonly TextBlock _label;
    private bool _suppressSliderEvent;

    public FrameFlowVolumeControl()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        _muteButton = new ToggleButton
        {
            Content = "🔊",
            Width = 36,
            Margin = new Thickness(4, 0),
            IsEnabled = false,
        };
        _muteButton.Click += OnMuteClick;

        _slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 1,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            IsEnabled = false,
            TickFrequency = 0.05,
        };
        _slider.ValueChanged += OnSliderChanged;

        _label = new TextBlock
        {
            Text = "100%",
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(4, 0),
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
        };

        Children.Add(_muteButton);
        Children.Add(_slider);
        Children.Add(_label);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaPlayerProperty)
            OnMediaPlayerChanged(change.GetNewValue<IMediaPlayer?>());
    }

    private void OnMediaPlayerChanged(IMediaPlayer? player)
    {
        // Enabled only when a player is attached and its audio sink has a gain
        // stage. Writing volume to a player without one is a documented no-op,
        // so a live-looking slider that changes nothing is worse than a
        // disabled one.
        var canControl = player is not null && player.SupportsVolumeControl;
        _muteButton.IsEnabled = canControl;
        _slider.IsEnabled = canControl;

        // Seed the display unconditionally, including when disabled. Rebinding
        // from a gain-capable player to a gainless one would otherwise leave
        // the previous player's slider position and mute glyph on screen,
        // describing a player that is no longer attached.
        SeedFrom(player);
    }

    /// <summary>
    /// Pushes <paramref name="player"/>'s volume and mute state into the
    /// widgets, or resets to unity and unmuted when it is <see langword="null"/>.
    /// </summary>
    private void SeedFrom(IMediaPlayer? player)
    {
        // Volume and mute persist across player lifetimes via the audio-sink
        // singleton, so an existing setting should be reflected immediately.
        _muteButton.IsChecked = player?.Muted ?? false;
        _suppressSliderEvent = true;
        try
        {
            _slider.Value = player?.Volume ?? 1.0;
        }
        finally
        {
            _suppressSliderEvent = false;
        }
        UpdateUi();
    }

    private void OnSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvent)
            return;
        var player = MediaPlayer;
        if (player is null)
            return;
        try
        {
            player.Volume = (float)e.NewValue;
            UpdateUi();
        }
        catch
        {
            // Sink may refuse out-of-range writes; let it complain via
            // its own logger and don't crash the UI.
        }
    }

    private void OnMuteClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        var player = MediaPlayer;
        if (player is null)
            return;
        try
        {
            player.Muted = _muteButton.IsChecked == true;
            UpdateUi();
        }
        catch
        {
            // See note above.
        }
    }

    /// <summary>
    /// Re-reads <see cref="IMediaPlayer.Volume"/> and
    /// <see cref="IMediaPlayer.Muted"/> and refreshes the slider /
    /// mute toggle / glyph / label. Call after mutating the player's
    /// volume or mute from outside the control (e.g. keyboard
    /// shortcuts on a parent view) so the visual state matches.
    /// </summary>
    public void RefreshFromPlayer() => SeedFrom(MediaPlayer);

    private void UpdateUi()
    {
        var player = MediaPlayer;
        if (player is null)
            return;

        var pct = (int)Math.Round(player.Volume * 100);
        _label.Text = player.Muted ? "mute" : $"{pct}%";
        _label.Foreground = new SolidColorBrush(
            Color.Parse(player.Muted ? "#d07a7a" : "#888888")
        );
        _muteButton.Content = player.Muted
            ? "🔇"
            : player.Volume switch
            {
                <= 0.001f => "🔈",
                < 0.34f => "🔈",
                < 0.67f => "🔉",
                _ => "🔊",
            };
    }
}
