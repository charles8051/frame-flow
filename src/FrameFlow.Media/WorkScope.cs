// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// Holds the work one run of an owner starts until that work ends (ADR-0086, decision 1). The
/// bookkeeping is the pure <see cref="WorkLedger"/>; this shell starts the work, cancels it and
/// waits for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Start.</b> <see cref="TryStart"/> runs the work on the thread pool under the scope's own
/// cancellation, linked to the token the work also needs, and holds it until it ends, however it
/// ends. Only its end lets it go. Once the scope is stopped, it starts nothing.
/// </para>
/// <para>
/// <b>Stop.</b> <see cref="StopAsync"/> cancels the work, waits for every piece of it to end, and
/// returns the faults it ended with that no stop has returned yet: every fault but a cancellation
/// the work was asked for, each once. The caller's token abandons only the caller's wait. The work
/// stays held and cancelled, and the next stop waits for it and returns its faults.
/// </para>
/// <para>
/// Work must not await its own scope's <see cref="StopAsync"/>: the stop waits for the work, which
/// would be waiting for the stop, and neither would end until that wait is abandoned.
/// </para>
/// <para>
/// <b>One run.</b> A scope serves one run. An owner gives each run a fresh scope, keeps every older
/// one that still holds work, and disposes by stopping them all.
/// </para>
/// <para>
/// <b>Threading.</b> Every member is safe to call from any thread.
/// </para>
/// </remarks>
internal sealed class WorkScope
{
    private readonly object _gate = new();

    // Cancelled by the first stop, and never disposed: it has no timer, and the link to it for a
    // piece of work is removed when that work ends, which leaves nothing registered on it.
    private readonly CancellationTokenSource _stop = new();

    // The task each piece of held work runs as, by the number the ledger gave it. Kept in step with
    // the ledger under _gate: added as the work starts, removed as it ends.
    private readonly Dictionary<long, Task> _held = [];
    private WorkLedger _ledger = WorkLedger.Empty;

    // The first stop's cancellation, which every stop waits for. Null until the first stop.
    private Task? _cancelled;

    /// <summary>The names of the work still held, in the order it started.</summary>
    /// <remarks>What a dispose that has waited too long names (ADR-0086, decision 3.3).</remarks>
    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_gate)
                return _ledger.Pending;
        }
    }

    /// <summary>Whether the scope has been stopped, and so starts nothing more.</summary>
    public bool IsStopped
    {
        get
        {
            lock (_gate)
                return _ledger.Stopped;
        }
    }

    /// <summary>
    /// Raised once the scope has let a piece of work go, with the name it was started under, on the
    /// thread the work ended on. For tests: the signal that the end has been recorded.
    /// </summary>
    internal Action<string>? WorkEnded { get; set; }

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool and holds it until it ends, unless the
    /// scope has been stopped.
    /// </summary>
    /// <param name="name">What the work is, as <see cref="Pending"/> and a fault name it.</param>
    /// <param name="work">The work, given the token that cancels it.</param>
    /// <param name="cancellationToken">
    /// A token the work also needs. The work's token is cancelled by this one or by the stop,
    /// whichever comes first.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the work started; <see langword="false"/> if the scope is stopped,
    /// in which case <paramref name="work"/> is not called.
    /// </returns>
    public bool TryStart(
        string name,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            var (next, started) = _ledger.Start(name);
            if (started is not { } number)
                return false;
            _ledger = next;

            // HoldAsync owns the linked source from here and disposes it once the work has ended.
            // CA2000 cannot follow it into the task.
#pragma warning disable CA2000
            var linked = cancellationToken.CanBeCanceled
                ? LinkedCancellation.Create(_stop.Token, cancellationToken)
                : null;
#pragma warning restore CA2000
            var token = linked?.Token ?? _stop.Token;

            // With no token of its own, so the wrapper always runs and always reaches the end that
            // lets the work go, even for work whose token is cancelled before it starts. Its end
            // needs _gate, so it cannot be recorded before the task is added here.
            _held.Add(
                number,
                Task.Run(() => HoldAsync(number, name, work, token, linked), CancellationToken.None)
            );
            return true;
        }
    }

    /// <summary>
    /// Cancels the work, waits for every piece of it to end, and returns the faults it ended with.
    /// </summary>
    /// <param name="cancellationToken">
    /// Abandons this caller's wait, not the stop: the work stays cancelled and held, and the next
    /// call waits for it.
    /// </param>
    /// <returns>
    /// The faults the scope's work has ended with that no completed stop has returned, in the order
    /// it ended: all but a cancellation the work was asked for. A call whose wait was abandoned
    /// returns nothing, so the next one returns them.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work had ended.
    /// </exception>
    public async Task<IReadOnlyList<WorkFault>> StopAsync(CancellationToken cancellationToken = default)
    {
        Task[] waits;
        lock (_gate)
        {
            (_ledger, var cancel) = _ledger.Stop();
            if (cancel)
                _cancelled = CancelAsync();
            waits = [_cancelled!, .. _held.Values];
        }

        // Nothing starts once the scope is stopped, so this is every piece of work there will be.
        // None of these faults: each records what its work ended with in the ledger.
        await Task.WhenAll(waits).WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            (_ledger, var faults) = _ledger.TakeFaults();
            return faults;
        }
    }

    /// <summary>Runs one piece of work to its end, then lets it go.</summary>
    private async Task HoldAsync(
        long number,
        string name,
        Func<CancellationToken, Task> work,
        CancellationToken token,
        LinkedCancellation? linked
    )
    {
        Exception? error = null;
        try
        {
            await work(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        lock (_gate)
        {
            _ledger = _ledger.End(number, error, token.IsCancellationRequested);
            _held.Remove(number);
        }

        // Cancelled already, or never to be: the work that used it has ended.
        linked?.Dispose();
        WorkEnded?.Invoke(name);
    }

    /// <summary>
    /// Cancels the work asynchronously, so its cancellation callbacks, and whatever they run, run on
    /// the thread pool, not under <c>_gate</c> or on the stopping caller's thread. A callback that
    /// throws is kept as a fault.
    /// </summary>
    private async Task CancelAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _ledger = _ledger.Fault("the stop's cancellation", ex);
        }
    }
}
