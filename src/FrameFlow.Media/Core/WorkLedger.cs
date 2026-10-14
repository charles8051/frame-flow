// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Media.Core;

/// <summary>A fault a piece of held work ended with, and the name it was started under.</summary>
/// <param name="Work">The name the work was started under.</param>
/// <param name="Error">What the work ended with.</param>
internal readonly record struct WorkFault(string Work, Exception Error);

/// <summary>
/// The bookkeeping of a <see cref="WorkScope"/> (ADR-0086, decision 1): which work the scope holds,
/// whether it has been stopped, and the faults its work has ended with. Pure: a ledger and an event
/// in, a ledger and a verdict out.
/// </summary>
/// <remarks>
/// <para>
/// Work is held from its start until it ends, and only its end lets it go. A stop cancels the work
/// and holds it still, so the stop can wait for it. Once stopped, the ledger starts nothing new.
/// </para>
/// <para>
/// A cancellation the work was asked for is not a fault: one the work ends with while the token it
/// was given is cancelled, by the stop or by the token it was started with. Anything else it ends
/// with is a fault, an <see cref="OperationCanceledException"/> nobody asked for included, as for a
/// graph's node (#489).
/// </para>
/// <para>
/// Each fault is returned once, by the first stop that completes after it (decision 3.4): an owner
/// that stops at a seek and again at its dispose does not raise one fault twice.
/// </para>
/// </remarks>
/// <param name="Stopped">Whether the scope has been stopped.</param>
/// <param name="LastWork">The number given to the work started last, from 1; 0 before the first.</param>
/// <param name="Held">The work held, by number, with the name each was started under.</param>
/// <param name="Faults">The faults the work has ended with, in the order it ended.</param>
/// <param name="Returned">How many of <paramref name="Faults"/>, from the first, a stop has returned.</param>
internal sealed record WorkLedger(
    bool Stopped,
    long LastWork,
    ImmutableSortedDictionary<long, string> Held,
    ImmutableList<WorkFault> Faults,
    int Returned
)
{
    /// <summary>A scope just made: holding nothing, not stopped.</summary>
    public static WorkLedger Empty { get; } =
        new(
            Stopped: false,
            LastWork: 0,
            ImmutableSortedDictionary<long, string>.Empty,
            ImmutableList<WorkFault>.Empty,
            Returned: 0
        );

    /// <summary>Whether no work is held.</summary>
    public bool IsIdle => Held.IsEmpty;

    /// <summary>The names of the work held, in the order it started.</summary>
    public IReadOnlyList<string> Pending => [.. Held.Values];

    /// <summary>
    /// Work named <paramref name="name"/> asks to start. It is held under the next number, or
    /// refused, with a null number, once the scope is stopped.
    /// </summary>
    public (WorkLedger Next, long? Work) Start(string name)
    {
        if (Stopped)
            return (this, null);

        var work = LastWork + 1;
        return (this with { LastWork = work, Held = Held.Add(work, name) }, work);
    }

    /// <summary>
    /// The scope is stopped. The first stop cancels the work; a later one finds it cancelled
    /// already. Either way the work stays held until it ends.
    /// </summary>
    public (WorkLedger Next, bool Cancel) Stop() =>
        Stopped ? (this, false) : (this with { Stopped = true }, true);

    /// <summary>
    /// The work numbered <paramref name="work"/> ended, with <paramref name="error"/> or none. It is
    /// let go, and what it ended with is kept as a fault unless it was a cancellation it was asked
    /// for. Work the ledger does not hold changes nothing.
    /// </summary>
    /// <param name="work">The number the work was started under.</param>
    /// <param name="error">What the work threw, or <see langword="null"/> when it completed.</param>
    /// <param name="cancellationRequested">Whether the token the work was given was cancelled when it ended.</param>
    public WorkLedger End(long work, Exception? error, bool cancellationRequested)
    {
        if (!Held.TryGetValue(work, out var name))
            return this;

        var next = this with { Held = Held.Remove(work) };
        return FaultOf(error, cancellationRequested) is { } fault
            ? next with { Faults = Faults.Add(new WorkFault(name, fault)) }
            : next;
    }

    /// <summary>
    /// Something the scope did on the work's behalf, such as cancelling it, failed with
    /// <paramref name="error"/>, which is kept as a fault under <paramref name="what"/>.
    /// </summary>
    public WorkLedger Fault(string what, Exception error) =>
        this with { Faults = Faults.Add(new WorkFault(what, error)) };

    /// <summary>
    /// A stop has completed: it returns the faults no stop has returned yet, and they are not
    /// returned again.
    /// </summary>
    public (WorkLedger Next, IReadOnlyList<WorkFault> Faults) TakeFaults() =>
        (this with { Returned = Faults.Count }, [.. Faults.Skip(Returned)]);

    /// <summary>
    /// The fault in what a piece of work ended with: none for a completion or a cancellation it was
    /// asked for, and what it threw otherwise.
    /// </summary>
    public static Exception? FaultOf(Exception? error, bool cancellationRequested) =>
        error is OperationCanceledException && cancellationRequested ? null : error;
}
