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
/// <see cref="IMediaTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Smooth thumb motion.</b> The control samples
/// <see cref="IMediaTransport.Position"/> on a ~30 Hz dispatcher tick and
/// updates the slider's <see cref="RangeBase.Value"/>.
/// <see cref="IMediaTransport.Position"/> is backed by a continuous
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
/// <see cref="IMediaTransport.SeekAsync"/> on each one floods the playback
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
/// <para>
/// <b>In and out of the visual tree</b> (ADR-0085, decision 1). The tick
/// runs while the bar is in the visual tree with a transport bound. The bar
/// seeks its transport while it is bound to it: from when the transport is
/// set, or from the bar's return to the tree when it is set while the bar is
/// out. Leaving the tree, or replacing the transport, ends the binding: the
/// tick stops, a scrub target still queued is dropped rather than sent, and
/// the result of a seek already sent is not reported. The return starts the
/// tick and the seeking again with the same transport, and arms the refusal
/// report again. A <see cref="RangeBase.Value"/> written while the bar is out
/// of the tree after leaving it seeks nothing.
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

    /// <summary>The transport whose position the bar reflects and controls.</summary>
    public static readonly StyledProperty<IMediaTransport?> TransportProperty =
        AvaloniaProperty.Register<FrameFlowSeekBar, IMediaTransport?>(nameof(Transport));

    /// <inheritdoc cref="TransportProperty"/>
    public IMediaTransport? Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    private bool _suppressUserSeek;
    private bool _userIsInteracting;
    private TimeSpan _lastKnownDuration = TimeSpan.Zero;

    // Follows the bound transport while the bar is bound to it, and runs the refresh tick while
    // the bar is in the visual tree with a transport bound (ADR-0085, decision 1).
    private readonly TransportFollower _follower;

    // Coalescing scrub-seek dispatcher, built when a transport is bound and
    // dropped when the binding ends, so null while no transport is bound. See
    // ScrubSeekDispatcher for the one-in-flight, latest-target-wins policy that
    // keeps a drag from flooding the engine.
    private ScrubSeekDispatcher? _scrub;

    // Latches on the first refused seek and clears on the next successful one,
    // so a source that refuses seeking says so once per run of failures rather
    // than once per scrub tick. UI thread only: the dispatcher's pump resumes
    // on the Avalonia context.
    private bool _seekRefusalReported;

    /// <summary>How many refused seeks the bar has reported. For tests.</summary>
    internal int SeekRefusalsReported { get; private set; }

    /// <summary>Whether the refresh tick is running. For tests.</summary>
    internal bool IsRefreshing => _follower.IsRefreshing;

    public FrameFlowSeekBar()
    {
        _follower = new TransportFollower(FollowTransport, refresh: (RefreshInterval, RefreshFromTransport));

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
        _follower.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _follower.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TransportProperty)
            OnTransportChanged(change.GetNewValue<IMediaTransport?>());
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
        // No bound transport, or out of the tree after leaving it → no dispatcher → no seek target.
        _scrub?.Request(TimeSpan.FromSeconds(newValue));
    }

    private void OnTransportChanged(IMediaTransport? transport)
    {
        // Ends the old binding, which drops its scrub dispatcher, and binds the new transport,
        // which builds one, or binds nothing.
        _follower.Assign(transport);

        if (transport is null)
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
        RefreshFromTransport();
    }

    // On every bind: when a transport is assigned while the bar is bound or before it first joins
    // the tree, and on every return to the tree. Built here and not when the transport is assigned:
    // one built while the bar is out of the tree would belong to no binding, and its results would
    // be stale from the return on.
    //
    // A dispatcher sends a target only while the follow it was built for is current. Its pump can
    // outlive the binding with a target queued behind a seek in flight, and the transport may be
    // the same one the next binding seeks: sent after a detach, that target would move a transport
    // the bar no longer follows, and sent after the return it could land after a newer target from
    // the next binding's dispatcher, which would break one-in-flight, latest-wins across the trip.
    // So a target queued under a binding that has ended is dropped, and only a seek already sent
    // can still complete.
    //
    // A scrub reports its Result through a latch rather than on every seek. A scrub emits seeks
    // continuously, so reporting each refusal would turn one gesture into a burst of identical
    // warnings; reporting none leaves a source that has a duration but refuses seeking
    // indistinguishable from one that works. The latch gives the first refusal and then stays quiet
    // until a seek succeeds, which re-arms it for the next thing that goes wrong.
    //
    // The latch belongs to one binding, not to the control, and each bind arms it again. A late
    // result from an earlier binding's seek could log against a transport that is no longer bound,
    // eat this binding's first refusal, or clear a latch it does not own, so each result is
    // committed only while the follow it was built for is current (ADR-0085, decision 5).
    private void FollowTransport(IMediaTransport transport, TransportFollower.Follow follow)
    {
        _seekRefusalReported = false;
        _scrub = new ScrubSeekDispatcher(async t =>
        {
            if (!follow.IsCurrent)
                return;

            var result = await transport.SeekAsync(t).ConfigureAwait(true);
            if (!follow.IsCurrent)
                return;

            if (result.IsSuccess)
            {
                _seekRefusalReported = false;
                return;
            }

            if (_seekRefusalReported)
                return;
            _seekRefusalReported = true;
            SeekRefusalsReported++;
            TransportCommand.Report(this, nameof(IMediaTransport.SeekAsync), result);
        });
        follow.OnEnd(() => _scrub = null);
    }

    /// <summary>
    /// Render-cadence sample: keep <see cref="RangeBase.Maximum"/> in sync
    /// with the media duration and glide <see cref="RangeBase.Value"/> to
    /// the live position. Skipped entirely while the user owns the thumb.
    /// </summary>
    private void RefreshFromTransport()
    {
        var transport = Transport;
        if (transport is null)
            return;
        // Skip programmatic updates while the user is mid-drag — would
        // fight the user's input.
        if (_userIsInteracting)
            return;

        var duration = transport.Duration;
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
        var position = _scrub is { IsSeeking: true } scrub ? scrub.LastRequested : transport.Position;
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
