// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Display-only badge showing the current playback state of an
/// <see cref="IMediaPlayer"/> with a color-coded text palette
/// (green = Playing, amber = Paused, red = Error, neutral grey
/// for everything else).
/// </summary>
/// <remarks>
/// <para>
/// Bind the <see cref="MediaPlayer"/> styled property; the badge
/// subscribes to <see cref="IMediaPlayer.StateChanged"/>, marshals
/// onto the UI thread via <see cref="AvaloniaObservableExtensions.ObserveOnUiThread"/>,
/// and seeds itself from the current state on assignment. Swap players
/// (or set to <see langword="null"/>) at any time — the badge disposes
/// the prior subscription and re-binds.
/// </para>
/// <para>
/// This is intentionally a leaf control: text + color, nothing else.
/// Compose alongside other status widgets in your own status strip,
/// or rely on the canonical layout in <see cref="FrameFlowPlayerView"/>.
/// </para>
/// </remarks>
public sealed class FrameFlowStateBadge : TextBlock
{
    /// <summary>
    /// The <see cref="IMediaPlayer"/> whose state the badge displays.
    /// Settable via XAML binding; can be re-assigned or cleared at runtime.
    /// </summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowStateBadge, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private IDisposable? _stateSubscription;

    public FrameFlowStateBadge()
    {
        // Sensible defaults that match the AvaloniaPlayer example's
        // status strip — consumers can override via XAML.
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 11;
        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        Text = "Idle";
        Foreground = MakeBrush(IdleColor);
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
            ApplyState(PlaybackState.Idle);
            return;
        }

        // Seed from current state — the observable only fires on
        // *changes*, so without this the badge would lag until the
        // first transition after binding.
        ApplyState(player.State);
        _stateSubscription = player.StateChanged.ObserveOnUiThread().Subscribe(ApplyState);
    }

    private void ApplyState(PlaybackState state)
    {
        Text = state.ToString();
        Foreground = MakeBrush(
            state switch
            {
                PlaybackState.Playing => PlayingColor,
                PlaybackState.Paused => PausedColor,
                PlaybackState.Error => ErrorColor,
                _ => IdleColor,
            }
        );
    }

    // Palette matches the AvaloniaPlayer example's existing scheme so
    // the migration is a visual no-op.
    private const string PlayingColor = "#7ad07a";
    private const string PausedColor = "#d0c07a";
    private const string ErrorColor = "#d07a7a";
    private const string IdleColor = "#888888";

    private static IBrush MakeBrush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
