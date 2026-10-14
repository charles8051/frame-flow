// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// The claim on one sink (ADR-0085, decision 6): who holds it, a non-blocking try-claim, and the
/// sink's place in the one claim order. A sink composes one and forwards
/// <see cref="IClaimableSink"/> to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Every member is safe to call from any thread. A try-claim never waits for
/// a claim to be released: it takes the sink or reports who holds it.
/// </para>
/// <para>
/// <b>Leases.</b> <see cref="TryClaim"/> returns a lease that releases the claim it took when it is
/// disposed. Disposing it again does nothing, and a lease disposed after its claim has ended does
/// nothing to a later claim.
/// </para>
/// <para>
/// <b>Disposal.</b> The sink calls <see cref="Close"/> from its own <c>DisposeAsync</c>. From then
/// on <see cref="TryClaim"/> throws <see cref="ObjectDisposedException"/>. A claim that held the
/// sink when it was closed stands until its lease is released.
/// </para>
/// </remarks>
public sealed class SinkClaimGate
{
    // The last claim order given out. They come from one sequence so that they are unique in the
    // process, which is what lets every claim on several sinks take them in one order.
    private static long s_lastClaimOrder;

    private readonly object _lock = new();
    private readonly Type _owner;
    private readonly Action? _released;
    private SinkClaimState _state = SinkClaimState.Open;

    /// <summary>Creates the gate for a sink of type <paramref name="owner"/>, which nobody holds.</summary>
    /// <param name="owner">
    /// The sink's type. <see cref="TryClaim"/> names it in the <see cref="ObjectDisposedException"/>
    /// it throws after <see cref="Close"/>.
    /// </param>
    public SinkClaimGate(Type owner)
        : this(owner, released: null) { }

    /// <summary>
    /// Creates the gate, with <paramref name="released"/> run each time a claim is released.
    /// </summary>
    /// <param name="owner">The sink's type, as for the public constructor.</param>
    /// <param name="released">
    /// Runs once for each claim released, on the thread that released it, before the gate takes
    /// another claim: a claim tried while it runs fails and names the claimant being released, so
    /// nothing the next claimant sends can reach the sink before it returns. A view's sink lets go
    /// of the frame it kept for a player that is gone here (ADR-0085, decision 2). The claim is
    /// released even if it throws, and the exception reaches the lease's caller.
    /// <para>
    /// That thread can be any thread, the UI thread included: a player's <c>DisposeAsync</c>
    /// releases its claims on whatever thread it resumed on, and an app may await it on the UI
    /// thread. So the hook does its work inline and never blocks on another thread, which could be
    /// waiting for the UI thread.
    /// </para>
    /// </param>
    internal SinkClaimGate(Type owner, Action? released)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _released = released;
        ClaimOrder = Interlocked.Increment(ref s_lastClaimOrder);
    }

    /// <summary>
    /// The sink's place in the claim order, unique in the process: gates made later have larger
    /// values. A sink reports it as <see cref="IClaimableSink.ClaimOrder"/>.
    /// </summary>
    public long ClaimOrder { get; }

    /// <summary>
    /// Who holds the sink, as their claim described them, or <see langword="null"/> when nobody
    /// does.
    /// </summary>
    public string? Claimant
    {
        get
        {
            lock (_lock)
                return _state.Claimant;
        }
    }

    /// <summary>Tries to claim the sink for <paramref name="claimant"/>, without waiting.</summary>
    /// <param name="claimant">Who claims it, as an error message names them.</param>
    /// <returns>
    /// The lease, or <see langword="null"/> when another claimant holds the sink.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="claimant"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException"><see cref="Close"/> has been called.</exception>
    public IDisposable? TryClaim(string claimant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);

        SinkClaimState next;
        SinkClaimOutcome outcome;
        lock (_lock)
        {
            (next, outcome) = _state.TryClaim(claimant);
            _state = next;
        }

        return outcome switch
        {
            SinkClaimOutcome.Taken => new Lease(this, next.Claim),
            SinkClaimOutcome.Held => null,
            _ => throw new ObjectDisposedException(_owner.FullName),
        };
    }

    /// <summary>
    /// Refuses claims from now on: <see cref="TryClaim"/> throws
    /// <see cref="ObjectDisposedException"/>. The sink calls it from its dispose.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this call closed the gate; <see langword="false"/> if it was
    /// already closed, in which case the call did nothing. As with
    /// <see cref="LatestWinsFrameSlot.Close"/>, a sink can use it to do its dispose-once work once.
    /// </returns>
    public bool Close()
    {
        lock (_lock)
        {
            if (_state.Closed)
                return false;
            _state = _state.Close();
            return true;
        }
    }

    // Runs once per lease, from its first Dispose.
    private void Release(long claim)
    {
        bool holds;
        lock (_lock)
            holds = _state.Holds(claim);
        if (!holds)
            return;

        // The hook runs while the claim still stands, so no new claim can be taken until it
        // returns, and it cannot let go of anything the next claimant sent.
        try
        {
            _released?.Invoke();
        }
        finally
        {
            lock (_lock)
                _state = _state.Release(claim);
        }
    }

    /// <summary>The claim one try-claim took. Releases it once, on the first dispose.</summary>
    private sealed class Lease(SinkClaimGate gate, long claim) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release(claim);
        }
    }
}
