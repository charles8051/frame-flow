// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Display-only label showing
/// <c>{Position} / {Duration}</c> for an <see cref="IMediaPlayer"/>,
/// updated on a quarter-second dispatcher tick (cheap; matches the
/// AvaloniaPlayer example's existing cadence).
/// </summary>
/// <remarks>
/// <para>
/// Position polling uses a <see cref="DispatcherTimer"/> rather than
/// <see cref="IMediaPlayer.PositionChanged"/> because that observable
/// can fire many times per second during playback — for a
/// human-readable label, 4 Hz is plenty and avoids redundant UI work.
/// </para>
/// </remarks>
public sealed class FrameFlowPositionLabel : TextBlock
{
    /// <summary>The player whose position to display.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowPositionLabel, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private DispatcherTimer? _refreshTimer;

    public FrameFlowPositionLabel()
    {
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 11;
        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        Foreground = new SolidColorBrush(Color.Parse("#888888"));
        Text = "--:--.--- / --:--.---";
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => Refresh()
        );
        _refreshTimer.Start();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaPlayerProperty)
            Refresh();
    }

    private void Refresh()
    {
        var player = MediaPlayer;
        if (player is null)
        {
            Text = "--:--.--- / --:--.---";
            return;
        }
        var duration = player.Duration;
        var position = player.Position;
        Text = $"{Format(position)} / {Format(duration)}";
    }

    private static string Format(TimeSpan ts) =>
        ts >= TimeSpan.FromHours(1) ? ts.ToString(@"hh\:mm\:ss\.fff") : ts.ToString(@"mm\:ss\.fff");
}
