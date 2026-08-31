// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FrameFlow.Media;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers are Windows high-resolution waitable timers, so a
/// delay costs what it asks for instead of being rounded up to the system tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists to remove.</b> <see cref="TimeProvider.System"/> routes delays to
/// the platform timer queue, which on Windows is quantized to the ~15.625 ms system tick. A
/// 60 fps frame period is 16.67 ms — just over one quantum — so a sleep for one frame usually
/// costs two. Both master clocks pace by sleeping the remaining time to the next frame
/// (ADR-0057), so that rounding is a hard ceiling on delivered frame rate: measured on this
/// machine, one frame period takes 29.5 ms through the system provider against 16.3 ms through
/// a high-resolution timer, which is ~34 fps versus ~61 fps against the same 60 fps source.
/// Reported as #128 and again as #152.
/// </para>
/// <para>
/// <b>Why the library does this rather than the host.</b> The usual fix is
/// <c>timeBeginPeriod(1)</c>, and ADR-0057 deferred it to the playback host on the grounds
/// that it belongs there rather than in a sink. That reasoning is about
/// <c>timeBeginPeriod</c> specifically: it raises the tick rate for the <i>whole process</i>
/// — arguably the whole system — which is a policy call a library has no business making on
/// its consumer's behalf. <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> carries no such
/// consequence. It is per-timer: this object's own timers become precise and nothing else in
/// the process changes. The objection does not transfer, so neither does the deferral, and
/// every consumer stops having to know about any of it.
/// </para>
/// <para>
/// <b>Availability.</b> The flag needs Windows 10 1803. <see cref="Preferred"/> is
/// <see cref="TimeProvider.System"/> everywhere it is unavailable, so callers can use it
/// unconditionally.
/// </para>
/// <para>
/// <b>Only timers change.</b> <see cref="TimeProvider.GetTimestamp"/> and
/// <see cref="TimeProvider.GetUtcNow"/> are the inherited defaults, which are the same
/// QPC/system-clock reads <see cref="TimeProvider.System"/> makes. Substituting this provider
/// changes when a delay wakes and nothing else about what a clock reads.
/// </para>
/// <para>
/// <b>Callbacks run on a thread pool wait thread</b>, not on a fresh pool work item, because
/// queueing one would re-introduce the scheduling delay this type exists to remove. A wait
/// thread serves up to 63 handles, so a callback that blocks delays other timers — the same
/// constraint <see cref="System.Threading.Timer"/> documents. FrameFlow's uses are
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> completions, which
/// only complete a task.
/// </para>
/// </remarks>
public sealed class HighResolutionTimeProvider : TimeProvider
{
    // Probed once. Creating a timer with the flag is the only honest test for it: the flag
    // fails with ERROR_INVALID_PARAMETER on Windows before 1803 rather than being silently
    // ignored, and a version check would not cover a container or emulation layer that
    // reports 1803+ without implementing it.
    private static readonly HighResolutionTimeProvider? Supported = Probe();

    private HighResolutionTimeProvider() { }

    /// <summary>Whether high-resolution timers are available on this machine.</summary>
    public static bool IsSupported => Supported is not null;

    /// <summary>
    /// The high-resolution provider where the platform has one, otherwise
    /// <see cref="TimeProvider.System"/>. Safe to use unconditionally, including on Linux and
    /// macOS, where the system provider's timers are not quantized this way to begin with.
    /// </summary>
    public static TimeProvider Preferred => Supported ?? System;

    /// <inheritdoc/>
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        ArgumentNullException.ThrowIfNull(callback);

        // Unreachable in practice — Probe returns null off Windows, so no instance exists to
        // call this on. Stated anyway because this is a public override that the platform
        // annotation on the timer cannot otherwise be checked against.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "High-resolution waitable timers are a Windows facility."
            );

        return new HighResolutionTimer(callback, state, dueTime, period);
    }

    private static HighResolutionTimeProvider? Probe()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var probe = Interop.CreateWaitableTimerExW(
                IntPtr.Zero,
                IntPtr.Zero,
                Interop.CreateWaitableTimerHighResolution,
                Interop.TimerAllAccess
            );
            return probe.IsInvalid ? null : new HighResolutionTimeProvider();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class HighResolutionTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly SafeWaitHandle _timer;

        // The same handle, in the shape ThreadPool.RegisterWaitForSingleObject accepts. Held
        // so teardown can release it: it is what keeps the OS handle alive between the
        // registration and its unregister. Null only if construction failed before creating
        // it, which is a path that ends in a throw.
        private readonly WaitableTimerHandle? _waitable;

        // Serializes Change against Dispose so a timer is never armed after its handle is
        // released. Read outside it only through _disposed, which is volatile for that reason.
        private readonly Lock _gate = new();
        private volatile bool _disposed;
        private RegisteredWaitHandle? _registration;

        internal HighResolutionTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            ValidateInterval(dueTime, nameof(dueTime));
            ValidateInterval(period, nameof(period));

            _callback = callback;
            _state = state;
            _timer = Interop.CreateWaitableTimerExW(
                IntPtr.Zero,
                IntPtr.Zero,
                Interop.CreateWaitableTimerHighResolution,
                Interop.TimerAllAccess
            );
            if (_timer.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Everything past this point can throw while holding native resources, and a
            // constructor that throws never hands the caller anything to dispose. Registration
            // can fail on thread-pool or handle exhaustion and SetWaitableTimer can fail
            // transiently — exactly the conditions where leaking a handle and a wait
            // registration per attempt compounds the exhaustion that caused it. Releasing them
            // here is the only chance anything gets.
            try
            {
                // Registered before the timer is armed. A due time of zero signals
                // immediately, so arming first would drop that first fire on the floor.
                _waitable = new WaitableTimerHandle(_timer);
                _registration = ThreadPool.RegisterWaitForSingleObject(
                    _waitable,
                    static (state, _) => ((HighResolutionTimer)state!).OnSignalled(),
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: false
                );

                Arm(dueTime, period);
            }
            catch
            {
                // Ordered as teardown is: stop dispatching, then release. Setting _disposed
                // first is what makes a signal already in flight to the wait thread a no-op.
                _disposed = true;
                _registration?.Unregister(null);
                _registration = null;
                ReleaseHandles();
                throw;
            }
        }

        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateInterval(dueTime, nameof(dueTime));
            ValidateInterval(period, nameof(period));

            lock (_gate)
            {
                if (_disposed)
                    return false;
                Arm(dueTime, period);
                return true;
            }
        }

        private void Arm(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                if (!Interop.CancelWaitableTimer(_timer))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return;
            }

            // A negative due time is relative to now in 100 ns units, which is exactly what a
            // TimeSpan tick is — so the requested interval reaches the kernel undivided, and
            // sub-millisecond delays stay sub-millisecond.
            long due = -dueTime.Ticks;

            // The kernel's repeat interval is milliseconds, matching what ITimer's own callers
            // can express. Zero means one-shot, which is what every FrameFlow use asks for:
            // the pacing loops recompute the next wait from the live clock each time rather
            // than running on a fixed cadence.
            int periodMs =
                period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero
                    ? 0
                    : Math.Max(1, (int)Math.Min(int.MaxValue, (long)period.TotalMilliseconds));

            if (!Interop.SetWaitableTimer(_timer, in due, periodMs, IntPtr.Zero, IntPtr.Zero, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void OnSignalled()
        {
            // Disposal can race a signal already on its way to the wait thread, so this check
            // is what keeps a callback from reaching a disposed timer's consumer. It does not
            // need to guard the handle: teardown does not release the handle until the wait
            // registration is gone.
            if (_disposed)
                return;
            _callback(_state);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            var registration = TearDown();
            if (registration is null)
                return;

            // Unregister before releasing, always. Once this returns, the thread pool is no
            // longer waiting on the handle, so closing it cannot pull the handle out from
            // under a live wait. Callbacks already queued may still be running, but they do
            // not touch the handle — only the consumer's callback, which _disposed has
            // already shut off.
            registration.Unregister(null);
            ReleaseHandles();
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            var registration = TearDown();
            if (registration is null)
                return default;

            // DisposeAsync promises no callback is running once it completes, and
            // Unregister(null) cannot promise that — it returns while callbacks are still in
            // flight. Passing an event makes the unregister signal it when they have drained.
            var drained = new ManualResetEvent(false);
            if (!registration.Unregister(drained))
            {
                drained.Dispose();
                ReleaseHandles();
                return default;
            }

            return new ValueTask(AwaitDrainAsync(drained));
        }

        private async Task AwaitDrainAsync(ManualResetEvent drained)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var waiter = ThreadPool.RegisterWaitForSingleObject(
                drained,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                completion,
                Timeout.Infinite,
                executeOnlyOnce: true
            );
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                waiter.Unregister(null);
                drained.Dispose();

                // Only now: the unregister has completed and every callback has returned.
                ReleaseHandles();
            }
        }

        /// <summary>
        /// Closes the kernel handle. Only ever called after the wait registration is gone, so
        /// the thread pool is never waiting on a handle this closes. Idempotent.
        /// </summary>
        private void ReleaseHandles()
        {
            _waitable?.Dispose();
            _timer.Dispose();
        }

        /// <summary>
        /// Disarms and marks disposed, handing the registration to the caller. Null when
        /// disposal already happened, so exactly one caller ever gets it.
        /// </summary>
        /// <remarks>
        /// Deliberately does not close the handle. The caller unregisters the wait first and
        /// releases afterwards, so the ordering never depends on the thread pool's reference
        /// on the <see cref="SafeWaitHandle"/> outliving this. The handle still has to be
        /// released promptly rather than left to a finalizer — at 60 timers a second that
        /// would be a real leak — which is what makes it the caller's job rather than nobody's.
        /// </remarks>
        private RegisteredWaitHandle? TearDown()
        {
            lock (_gate)
            {
                if (_disposed)
                    return null;
                _disposed = true;

                // Disarm first: a cancelled timer never signals again, which is what makes
                // the racing signal in OnSignalled a narrow window rather than an open one.
                Interop.CancelWaitableTimer(_timer);

                var registration = _registration;
                _registration = null;
                return registration;
            }
        }

        /// <summary>
        /// The <see cref="ITimer"/> range: any interval representable in milliseconds as a
        /// UInt32, or <see cref="Timeout.InfiniteTimeSpan"/>.
        /// </summary>
        private static void ValidateInterval(TimeSpan value, string name)
        {
            if (value == Timeout.InfiniteTimeSpan)
                return;

            var ms = value.TotalMilliseconds;
            if (ms < 0 || ms > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(name, value, "Interval is out of range.");
        }

        /// <summary>
        /// Presents the timer handle as something <see cref="ThreadPool"/> can wait on. The
        /// timer is created auto-reset, so each signal satisfies exactly one wait and a
        /// periodic timer delivers one callback per period.
        /// </summary>
        private sealed class WaitableTimerHandle : WaitHandle
        {
            internal WaitableTimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }
    }

    private static class Interop
    {
        /// <summary>
        /// Windows 10 1803 and later. Without it the timer is an ordinary one quantized to the
        /// system tick, which is the behaviour being fixed.
        /// </summary>
        internal const uint CreateWaitableTimerHighResolution = 0x00000002;

        internal const uint TimerAllAccess = 0x1F0003;

        [DllImport(
            "kernel32.dll",
            SetLastError = true,
            EntryPoint = "CreateWaitableTimerExW",
            CharSet = CharSet.Unicode
        )]
        internal static extern SafeWaitHandle CreateWaitableTimerExW(
            IntPtr lpTimerAttributes,
            IntPtr lpTimerName,
            uint dwFlags,
            uint dwDesiredAccess
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimer(
            SafeWaitHandle hTimer,
            in long lpDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            [MarshalAs(UnmanagedType.Bool)] bool fResume
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelWaitableTimer(SafeWaitHandle hTimer);
    }
}
