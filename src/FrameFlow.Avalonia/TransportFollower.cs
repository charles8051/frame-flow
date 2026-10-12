// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Keeps a chrome control following its bound <see cref="IMediaTransport"/>: the subscriptions
/// that track the transport start when it is bound, stop when the control leaves the visual tree,
/// and start again when the control comes back (#643). A transport bound while the control is out
/// of the tree is followed from its return.
/// </summary>
/// <remarks>
/// <para>
/// The control says what following a transport means in one delegate, which subscribes through
/// the <see cref="Follow"/> it is handed and then reads the transport's current values.
/// Subscribing first means a change that lands between the two still arrives, after the read. The
/// delegate runs on every bind and on every return to the visual tree, so it does only what
/// tracking the transport needs. A request the control makes of the transport when it is bound,
/// such as the transport bar's <c>LoopByDefault</c>, stays with the bind.
/// </para>
/// <para>
/// A control bound before it first joins the tree follows from the bind, as it always has.
/// </para>
/// <para>
/// Notifications are posted to the UI thread, so one raised before a follow stops can be
/// delivered after it, from a transport the control has let go of or over values read on the
/// control's return. A <see cref="Follow"/> drops whatever it delivers once it has stopped. UI
/// thread only: the stop and the delivery both run there, which is what makes that check sound.
/// </para>
/// </remarks>
/// <param name="start">Subscribes to the transport through the <see cref="Follow"/>, then reads
/// its current values.</param>
internal sealed class TransportFollower(Action<IMediaTransport, TransportFollower.Follow> start)
{
    private Follow? _live;

    // Between Suspend and Resume: the control is out of the visual tree.
    private bool _suspended;

    /// <summary>
    /// Follows <paramref name="transport"/> in place of whatever was followed, or nothing when it
    /// is <see langword="null"/>. Call when the control's transport changes. While the control is
    /// out of the tree this only lets go of the old transport; <see cref="Resume"/> takes up the
    /// new one.
    /// </summary>
    public void Rebind(IMediaTransport? transport)
    {
        End();
        if (transport is not null && !_suspended)
            Start(transport);
    }

    /// <summary>
    /// Follows <paramref name="transport"/> again when nothing is followed. Call when the control
    /// joins the visual tree, with its bound transport.
    /// </summary>
    /// <remarks>
    /// A transport disposed while the control was out of the tree refuses the subscription with
    /// <see cref="ObjectDisposedException"/>. That is not the control's to throw from its attach:
    /// it follows nothing, and shows what it showed, until a transport is bound.
    /// </remarks>
    public void Resume(IMediaTransport? transport)
    {
        _suspended = false;
        if (_live is not null || transport is null)
            return;
        try
        {
            Start(transport);
        }
        catch (ObjectDisposedException)
        {
            // Start has already let go of whatever it subscribed before the refusal.
        }
    }

    /// <summary>
    /// Stops following until <see cref="Resume"/>. Call when the control leaves the visual tree.
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        End();
    }

    private void End()
    {
        var live = _live;
        _live = null;
        live?.Dispose();
    }

    /// <summary>
    /// Runs the control's delegate on a new <see cref="Follow"/>. If the delegate throws, the
    /// follow is stopped, with whatever it had subscribed, so <c>_live</c> only ever holds one
    /// that started.
    /// </summary>
    private void Start(IMediaTransport transport)
    {
        var follow = new Follow();
        _live = follow;
        try
        {
            start(transport, follow);
        }
        catch
        {
            End();
            throw;
        }
    }

    /// <summary>One spell of following a transport: its subscriptions, and whether it has stopped.</summary>
    internal sealed class Follow : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        private bool _stopped;

        /// <summary>
        /// Delivers <paramref name="source"/> to <paramref name="onNext"/> on the UI thread until
        /// this follow stops. A notification posted before the stop and delivered after it is
        /// dropped.
        /// </summary>
        public void On<T>(IObservable<T> source, Action<T> onNext) =>
            _subscriptions.Add(
                source
                    .ObserveOnUiThread()
                    .Subscribe(value =>
                    {
                        if (!_stopped)
                            onNext(value);
                    })
            );

        public void Dispose()
        {
            _stopped = true;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
