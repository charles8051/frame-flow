// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Display-only one-line summary of an
/// <see cref="IMediaPlayer"/>'s loaded media —
/// codec, resolution, frame rate, audio sample rate / channels, and
/// container. Refreshes whenever the player's state changes (which
/// covers the Initializing → Paused / Playing transition where
/// <see cref="IMediaPlayer.MediaInfo"/> first becomes available).
/// </summary>
public sealed class FrameFlowStreamSummary : TextBlock
{
    /// <summary>The player whose loaded media to summarise.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowStreamSummary, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private IDisposable? _stateSubscription;

    public FrameFlowStreamSummary()
    {
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 11;
        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        Foreground = new SolidColorBrush(Color.Parse("#888888"));
        TextTrimming = TextTrimming.CharacterEllipsis;
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
            Text = string.Empty;
            return;
        }

        Refresh(player);
        _stateSubscription = player.StateChanged.ObserveOnUiThread().Subscribe(_ => Refresh(player));
    }

    private void Refresh(IMediaPlayer player)
    {
        // MediaInfo throws if the player hasn't finished loading.
        // Catch and clear — the next state transition will retry.
        MediaInfo info;
        try
        {
            info = player.MediaInfo;
        }
        catch
        {
            Text = string.Empty;
            return;
        }

        if (info.VideoStreams.Count > 0)
        {
            var v = info.VideoStreams[0];
            Text =
                $"{v.CodecName}  {v.Width}x{v.Height}  {v.FrameRate:F2} fps  ·  "
                + $"{info.ContainerName}";
        }
        else if (info.AudioStreams.Count > 0)
        {
            var a = info.AudioStreams[0];
            Text =
                $"{a.CodecName}  {a.SampleRate} Hz  {a.Channels} ch  ·  "
                + $"{info.ContainerName}";
        }
        else
        {
            Text = string.Empty;
        }
    }
}
