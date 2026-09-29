// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Display-only one-line summary of an
/// <see cref="IMediaTransport"/>'s loaded media —
/// codec, resolution, frame rate, audio sample rate / channels, and
/// container. Refreshes whenever the transport's state changes (which
/// covers the Initializing → Paused / Playing transition where
/// <see cref="IMediaTransport.MediaInfo"/> first becomes available).
/// </summary>
public sealed class FrameFlowStreamSummary : TextBlock
{
    /// <summary>The transport whose loaded media to summarise.</summary>
    public static readonly StyledProperty<IMediaTransport?> TransportProperty =
        AvaloniaProperty.Register<FrameFlowStreamSummary, IMediaTransport?>(nameof(Transport));

    /// <inheritdoc cref="TransportProperty"/>
    public IMediaTransport? Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
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
        if (change.Property == TransportProperty)
            OnTransportChanged(change.GetNewValue<IMediaTransport?>());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stateSubscription?.Dispose();
        _stateSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTransportChanged(IMediaTransport? transport)
    {
        _stateSubscription?.Dispose();
        _stateSubscription = null;

        if (transport is null)
        {
            Text = string.Empty;
            return;
        }

        Refresh(transport);
        _stateSubscription = transport.StateChanged.ObserveOnUiThread().Subscribe(_ => Refresh(transport));
    }

    private void Refresh(IMediaTransport transport)
    {
        // Null until the current item finishes loading. Clear and wait; the next state
        // transition retries.
        if (transport.MediaInfo is not { } info)
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
