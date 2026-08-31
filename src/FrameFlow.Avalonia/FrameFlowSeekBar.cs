// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Interactive timeline scrubber: shows current playback position on
/// a horizontal slider, click or drag to seek. Bound to an
/// <see cref="IMediaPlayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Smooth thumb motion.</b> The control samples
/// <see cref="IMediaPlayer.Position"/> on a ~30 Hz dispatcher tick and
/// updates the slider's <see cref="RangeBase.Value"/>.
/// <see cref="IMediaPlayer.Position"/> is backed by a continuous
/// wall-clock (the playback clock computes it from elapsed time on
/// every read), so sampling at render cadence makes the thumb glide on
/// real elapsed time — no extrapolation, no drift correction. (The old
/// 4 Hz poll is what made the thumb visibly step.)
/// </para>
/// <para>
/// <b>Feedback-loop avoidance.</b> To distinguish those programmatic
/// <see cref="RangeBase.Value"/> updates from a user dragging the thumb,
/// the control suppresses seeks during its own writes
/// (<see cref="_suppressUserSeek"/>) and skips the refresh tick while
/// the user is interacting (<see cref="_userIsInteracting"/>) — the user
/// always wins.
/// </para>
/// <para>
/// <b>Scrub coalescing.</b> A drag raises <see cref="RangeBase.Value"/>
/// dozens of times per second. Firing
/// <see cref="IMediaPlayer.SeekAsync"/> on each one floods the playback
/// engine — every request triggers a full demux-seek + decoder-flush +
/// decode-forward cycle, which is what makes scrubbing stutter. Instead
/// the control routes user targets through a
/// <see cref="ScrubSeekDispatcher"/>, which keeps at most <b>one seek in
/// flight</b> and coalesces: while a seek runs, newer drag targets
/// overwrite a single pending slot, and only the latest is issued when
/// the current one finishes. This self-paces to the engine's real seek
/// throughput and guarantees the final target (including the one latched
/// at pointer-release) is the one that lands — so release always commits
/// the exact drop position. While a scrub seek is resolving the thumb is
/// held at the dispatcher's last-requested target rather than the
/// engine's not-yet-updated position, so it never snaps back then
/// forward.
/// </para>
/// <para>
/// <b>Click-to-seek</b> works out of the box because Avalonia's
/// <see cref="Slider"/> already handles click-on-track as a Value
/// jump; we just need the user-vs-programmatic discrimination above.
/// </para>
/// </remarks>
public sealed class FrameFlowSeekBar : Slider
{
    // Avalonia's Fluent theme registers Slider's ControlTheme keyed
    // on `typeof(Slider)` — exact-type match. A derived class like
    // FrameFlowSeekBar gets no template by default, so the slider
    // renders as nothing (invisible track + invisible thumb) even
    // though layout reserves space for it. Without this line the
    // seek bar is structurally present but visually gone.
    // StyleKeyOverride is the Avalonia 11 idiom for "treat me as a
    // Slider for theming purposes."
    protected override Type StyleKeyOverride => typeof(Slider);

    // Render-cadence sample interval for the thumb. ~30 Hz reads as
    // continuous motion for a slowly-travelling thumb while keeping
    // wake-ups modest; the underlying Position is continuous, so this is
    // purely a display-smoothness knob. A no-op when the value is
    // unchanged (paused / stopped), since Slider skips invalidation when
    // Value doesn't actually move.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>The player whose position the bar reflects and controls.</summary>
    public static readonly StyledProperty<IMediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<FrameFlowSeekBar, IMediaPlayer?>(nameof(MediaPlayer));

    /// <inheritdoc cref="MediaPlayerProperty"/>
    public IMediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private DispatcherTimer? _refreshTimer;
    private bool _suppressUserSeek;
    private bool _userIsInteracting;
    private TimeSpan _lastKnownDuration = TimeSpan.Zero;

    // Coalescing scrub-seek dispatcher, rebuilt per bound player and null when
    // no player is bound. See ScrubSeekDispatcher for the one-in-flight,
    // latest-target-wins policy that keeps a drag from flooding the engine.
    private ScrubSeekDispatcher? _scrub;

    public FrameFlowSeekBar()
    {
        Minimum = 0;
        Maximum = 1;
        Value = 0;
        IsEnabled = false;
        SmallChange = 1; // 1 second
        LargeChange = 10; // 10 seconds (PageUp/Down)
        // Fluent's default Slider track is thin and easy to overlook
        // on a dark video player chrome. Force enough height so the
        // bar always reads as an interactive timeline regardless of
        // theme defaults, and Stretch horizontally so it fills the
        // available row.
        //
        // Height must be ≥ ~30 px to fully contain the Fluent thumb
        // glyph — at 24 px the bottom half of the thumb circle is
        // clipped by the control's layout slot. Spec'd at 40 px to
        // give the thumb full breathing room and make the bar an
        // easier mouse target.
        Height = 40;
        MinHeight = 40;
        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer = new DispatcherTimer(
            RefreshInterval,
            DispatcherPriority.Background,
            (_, _) => RefreshFromPlayer()
        );
        _refreshTimer.Start();
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
            OnMediaPlayerChanged(change.GetNewValue<IMediaPlayer?>());
        else if (change.Property == ValueProperty)
            OnValueChangedHandler(change.GetNewValue<double>());
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _userIsInteracting = true;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Defer flipping the flag back so any final ValueChanged from
        // the click-to-seek lands as user-initiated, not programmatic.
        Dispatcher.UIThread.Post(() => _userIsInteracting = false, DispatcherPriority.Background);
    }

    private void OnValueChangedHandler(double newValue)
    {
        // Programmatic update (refresh tick / duration coercion) — ignore.
        if (_suppressUserSeek)
            return;
        // No bound player → no dispatcher → no seek target.
        _scrub?.Request(TimeSpan.FromSeconds(newValue));
    }

    private void OnMediaPlayerChanged(IMediaPlayer? player)
    {
        // Rebind the coalescing dispatcher to the new player (or drop it). Any
        // pump still draining against the previous player finishes harmlessly
        // — its seeks target the old, now-disposing player and are swallowed.
        _scrub = player is null ? null : new ScrubSeekDispatcher(t => player.SeekAsync(t));

        if (player is null)
        {
            IsEnabled = false;
            _suppressUserSeek = true;
            try
            {
                Maximum = 1;
                Value = 0;
            }
            finally
            {
                _suppressUserSeek = false;
            }
            _lastKnownDuration = TimeSpan.Zero;
            return;
        }
        IsEnabled = true;
        _lastKnownDuration = TimeSpan.Zero; // force Maximum re-sync on next sample
        RefreshFromPlayer();
    }

    /// <summary>
    /// Render-cadence sample: keep <see cref="RangeBase.Maximum"/> in sync
    /// with the media duration and glide <see cref="RangeBase.Value"/> to
    /// the live position. Skipped entirely while the user owns the thumb.
    /// </summary>
    private void RefreshFromPlayer()
    {
        var player = MediaPlayer;
        if (player is null)
            return;
        // Skip programmatic updates while the user is mid-drag — would
        // fight the user's input.
        if (_userIsInteracting)
            return;

        var duration = player.Duration;
        if (duration != _lastKnownDuration && duration > TimeSpan.Zero)
        {
            _suppressUserSeek = true;
            try
            {
                Maximum = duration.TotalSeconds;
            }
            finally
            {
                _suppressUserSeek = false;
            }
            _lastKnownDuration = duration;
        }

        // While a scrub seek is resolving, pin the thumb to the user's
        // requested target. Reading the live position here would show the
        // pre-seek value until the engine catches up, snapping the thumb
        // back and then forward — visible jitter on every scrub.
        var position = _scrub is { IsSeeking: true } scrub ? scrub.LastRequested : player.Position;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        _suppressUserSeek = true;
        try
        {
            Value = position.TotalSeconds;
        }
        finally
        {
            _suppressUserSeek = false;
        }
    }
}
