// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Coalescing, single-in-flight dispatcher for scrub seeks. Separates the
/// "which target to seek to" decision — a tiny state machine of an in-flight
/// flag plus one pending slot — from the seek IO, which is injected as an
/// async delegate. Keeps at most one seek outstanding: rapid
/// <see cref="Request"/> calls during a drag overwrite the pending slot, so
/// only the latest target is issued when the current seek finishes, and the
/// final target is always the one that lands (commit-on-release).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A slider drag raises value changes dozens of times
/// per second. Firing a seek on each one floods the playback engine — every
/// request triggers a full demux-seek + decoder-flush + decode-forward cycle,
/// which is what makes scrubbing stutter. Coalescing collapses the burst to a
/// rate the engine can actually sustain while still honouring the user's final
/// drop position.
/// </para>
/// <para>
/// <b>State / IO / timing split.</b> This type owns only state (the flag and
/// pending slot) and delegates IO to the injected seek function. It carries no
/// timer and no clock — the cadence is whatever the caller's value changes and
/// the engine's seek latency produce. That keeps it deterministically testable
/// with a fake seek delegate whose completion the test drives.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe: every member must be touched from a
/// single context (for a UI seek bar, the UI thread). The seek delegate is
/// awaited without <c>ConfigureAwait(false)</c>, so its continuations resume on
/// the caller's synchronization context and the dispatcher's state never leaves
/// that thread. A fault from one seek is swallowed so the pump keeps draining
/// later targets — fatal seek failures surface through the player's own state
/// machine, not here.
/// </para>
/// </remarks>
public sealed class ScrubSeekDispatcher
{
    private readonly Func<TimeSpan, Task> _seek;
    private bool _inFlight;
    private TimeSpan? _pending;

    /// <param name="seek">
    /// Performs the actual seek to the given target. Awaited one at a time;
    /// its continuation must resume on the caller's context (do not apply
    /// <c>ConfigureAwait(false)</c> inside it if the caller relies on
    /// single-threaded access, as the seek bar does).
    /// </param>
    public ScrubSeekDispatcher(Func<TimeSpan, Task> seek)
    {
        ArgumentNullException.ThrowIfNull(seek);
        _seek = seek;
    }

    /// <summary>
    /// The most recently requested target. While <see cref="IsSeeking"/> is
    /// true this is where the thumb should be pinned — the engine has not
    /// necessarily reached it yet, so reading the live position instead would
    /// snap the thumb backwards and then forwards.
    /// </summary>
    public TimeSpan LastRequested { get; private set; }

    /// <summary>True while a seek is outstanding (including a queued follow-up).</summary>
    public bool IsSeeking => _inFlight;

    /// <summary>
    /// Records a new seek target. If a seek is already running the target is
    /// stashed and supersedes any earlier un-issued target; otherwise the pump
    /// starts immediately.
    /// </summary>
    public void Request(TimeSpan target)
    {
        LastRequested = target;
        _pending = target;
        if (_inFlight)
            return; // the running pump will pick up the latest pending target
        _ = PumpAsync();
    }

    /// <summary>
    /// Drains the pending slot one seek at a time until no newer target
    /// remains. Never faults: a throwing seek is caught per-iteration so the
    /// loop continues, and the in-flight flag is always cleared on exit.
    /// </summary>
    private async Task PumpAsync()
    {
        _inFlight = true;
        try
        {
            while (_pending is { } target)
            {
                _pending = null;
                try
                {
                    await _seek(target);
                }
                catch
                {
                    // Swallow so a single failed seek doesn't abandon the
                    // targets queued behind it. Fatal failures route the
                    // player to its Error state independently of this pump.
                }
            }
        }
        finally
        {
            _inFlight = false;
        }
    }
}
