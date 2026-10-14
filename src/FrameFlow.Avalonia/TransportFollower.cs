// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using FrameFlow.Avalonia.Core;
using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// The shell over <see cref="SurfaceLifecycle"/> for a chrome control that follows an
/// <see cref="IMediaTransport"/> (ADR-0085, decision 1). The transport is a target the control
/// borrows: it follows the transport while bound, and runs its refresh timer, if it has one, while
/// it presents, which for a chrome control is while it is in the visual tree with a transport
/// bound. A control never disposes its transport, so the follower never sends the core a dispose.
/// </summary>
/// <remarks>
/// <para>
/// A transport bound before the control first joins the tree is followed from the bind, as it
/// always has been, and the first attach only starts the timer. Leaving the tree stops following
/// and stops the timer, and the control keeps its transport. Coming back follows it again, from
/// what it reports then, and starts the timer (#643). A transport bound while the control is out of
/// the tree is taken up at once and followed from the return.
/// </para>
/// <para>
/// The control says what following a transport means in one delegate, which subscribes through
/// the <see cref="Follow"/> it is handed and then reads the transport's current values.
/// Subscribing first means a change that lands between the two still arrives, after the read. The
/// delegate runs on every bind, so on every return to the tree, and does only what tracking the
/// transport needs. A request the control makes of a transport it is given, such as the transport
/// bar's <c>LoopByDefault</c>, goes in the delegate the follower runs when it takes the transport
/// up, once per transport assigned and never on a return.
/// </para>
/// <para>
/// Notifications are posted to the UI thread, so one raised before a follow ends can be delivered
/// after it, from a transport the control has let go of or over values read on the control's
/// return. A <see cref="Follow"/> delivers only while the binding number it was bound under is
/// current, and every bind and unbind moves the number (ADR-0085, decision 5), and only until it
/// is ended, which a delegate that throws partway does at once. UI thread only: the transitions
/// and the delivery both run there, which is what makes that comparison sound.
/// </para>
/// </remarks>
internal sealed class TransportFollower
{
    private readonly Action<IMediaTransport, Follow>? _follow;
    private readonly Action<IMediaTransport>? _taken;
    private readonly (TimeSpan Interval, Action Tick)? _refresh;

    // Where the control is in its life, and its binding number. UI thread only.
    private SurfaceLifecycle _lifecycle = SurfaceLifecycle.Initial;

    // The transport the lifecycle's target says the control holds.
    private IMediaTransport? _transport;

    private Follow? _live;
    private DispatcherTimer? _refreshTimer;

    /// <summary>Creates the follower for one control.</summary>
    /// <param name="follow">
    /// Subscribes to the transport through the <see cref="Follow"/>, then reads its current values.
    /// Runs on every bind. <see langword="null"/> for a control that only polls.
    /// </param>
    /// <param name="taken">
    /// Runs once when the control takes up a transport it is assigned, before the bind, and not on
    /// a return to the tree.
    /// </param>
    /// <param name="refresh">
    /// The control's refresh timer: its interval and what each tick does. It runs while the control
    /// presents. <see langword="null"/> for a control with none.
    /// </param>
    public TransportFollower(
        Action<IMediaTransport, Follow>? follow = null,
        Action<IMediaTransport>? taken = null,
        (TimeSpan Interval, Action Tick)? refresh = null
    )
    {
        _follow = follow;
        _taken = taken;
        _refresh = refresh;
    }

    /// <summary>Whether the refresh timer is running. For tests.</summary>
    internal bool IsRefreshing => _refreshTimer is { IsEnabled: true };

    /// <summary>
    /// Follows <paramref name="transport"/> in place of whatever was followed, or nothing when it
    /// is <see langword="null"/>. Call when the control's transport changes. While the control is
    /// out of the tree this takes the transport up and lets go of the old one; the return binds it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The transport refused a subscription the follow made. The control follows nothing until it
    /// is given another transport or leaves the tree and comes back.
    /// </exception>
    public void Assign(IMediaTransport? transport)
    {
        if (ReferenceEquals(_transport, transport))
            return;

        Complete(Apply(_lifecycle.Assign(transport is not null), transport));
    }

    /// <summary>Call when the control joins the visual tree.</summary>
    /// <remarks>
    /// A transport disposed while the control was out of the tree refuses the subscription with
    /// <see cref="ObjectDisposedException"/>. That is not the control's to throw from its attach:
    /// it follows nothing, and shows what it showed, until a transport is bound.
    /// </remarks>
    public void Attach()
    {
        var faults = Apply(_lifecycle.Attach());
        faults?.RemoveAll(fault => fault is ObjectDisposedException);
        Complete(faults);
    }

    /// <summary>Call when the control leaves the visual tree.</summary>
    public void Detach() => Complete(Apply(_lifecycle.Detach()));

    /// <summary>
    /// Moves to the state the core chose and does the work it named, in the core's order: stop
    /// following, stop the timer, let go of the transport, take up the assigned one, follow it,
    /// start the timer. UI thread. Every step runs even when one before it threw, because the core
    /// has moved on and will not ask for that work again, and what they threw is returned.
    /// </summary>
    private List<Exception>? Apply(SurfaceTransition transition, IMediaTransport? assigned = null)
    {
        Debug.Assert(!transition.Refused, "A chrome control is never disposed, so the core refuses nothing.");

        _lifecycle = transition.Next;
        var work = transition.Work;
        List<Exception>? faults = null;

        if (work.HasFlag(SurfaceWork.Unbind))
            Run(EndFollow, ref faults);
        if (work.HasFlag(SurfaceWork.StopPresenting))
            StopRefresh();
        if (work.HasFlag(SurfaceWork.ForgetTarget))
            _transport = null;
        if (work.HasFlag(SurfaceWork.Borrow))
        {
            _transport = assigned!;
            if (_taken is { } taken)
                Run(() => taken(assigned!), ref faults);
        }
        if (work.HasFlag(SurfaceWork.Bind))
            Run(StartFollow, ref faults);
        if (work.HasFlag(SurfaceWork.StartPresenting))
            Run(StartRefresh, ref faults);

        return faults;
    }

    private static void Run(Action step, ref List<Exception>? faults)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            (faults ??= []).Add(ex);
        }
    }

    private static void Complete(List<Exception>? faults)
    {
        switch (faults)
        {
            case null or []:
                break;
            case [var one]:
                ExceptionDispatchInfo.Throw(one);
                break;
            default:
                throw new AggregateException(faults);
        }
    }

    /// <summary>
    /// Runs the control's delegate on a new <see cref="Follow"/>, under the binding number the
    /// transition has just set. If the delegate throws, the follow is ended, with whatever it had
    /// subscribed, so <c>_live</c> only ever holds one that started.
    /// </summary>
    private void StartFollow()
    {
        if (_follow is null)
            return;

        var follow = new Follow(this, _lifecycle.Binding);
        _live = follow;
        try
        {
            _follow(_transport!, follow);
        }
        catch
        {
            EndFollow();
            throw;
        }
    }

    private void EndFollow()
    {
        var live = _live;
        _live = null;
        live?.Dispose();
    }

    private void StartRefresh()
    {
        if (_refresh is not { } refresh)
            return;

        _refreshTimer = new DispatcherTimer(refresh.Interval, DispatcherPriority.Background, (_, _) => refresh.Tick());
        _refreshTimer.Start();
    }

    private void StopRefresh()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    /// <summary>
    /// One binding's following of a transport: its subscriptions, what the control built for it,
    /// and the binding number it was bound under.
    /// </summary>
    internal sealed class Follow : IDisposable
    {
        private readonly TransportFollower _owner;
        private readonly long _binding;
        private readonly List<IDisposable> _held = [];

        internal Follow(TransportFollower owner, long binding)
        {
            _owner = owner;
            _binding = binding;
        }

        /// <summary>
        /// Whether this follow is still the control's: no unbind or bind has moved the binding
        /// number since it was bound, and it has not been ended. A follow whose delegate threw
        /// partway is ended at once while its binding stands, so the number alone would not say
        /// so. Work that commits on the UI thread for this follow, such as a notification or a
        /// seek, checks it there. UI thread.
        /// </summary>
        public bool IsCurrent => _owner._lifecycle.Binding == _binding && ReferenceEquals(_owner._live, this);

        /// <summary>
        /// Delivers <paramref name="source"/> to <paramref name="onNext"/> on the UI thread while
        /// this follow is current. A notification posted before the binding ended and delivered
        /// after it is dropped.
        /// </summary>
        public void On<T>(IObservable<T> source, Action<T> onNext) =>
            _held.Add(
                source
                    .ObserveOnUiThread()
                    .Subscribe(value =>
                    {
                        if (IsCurrent)
                            onNext(value);
                    })
            );

        /// <summary>
        /// Runs <paramref name="end"/> when this follow ends, for what the control built for it
        /// and lets go of with it.
        /// </summary>
        public void OnEnd(Action end) => _held.Add(new Ending(end));

        public void Dispose()
        {
            foreach (var held in _held)
                held.Dispose();
            _held.Clear();
        }

        private sealed class Ending(Action end) : IDisposable
        {
            public void Dispose() => end();
        }
    }
}
