// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Admits one holder at a time to the video processor path (#560). <see cref="Process"/> is the
/// lease every <see cref="CompositionInteropVideoView"/> in the process shares, so two views can
/// never issue concurrent <c>VideoProcessorBlt</c>s (ADR-0063).
/// <para>
/// The GPU runs a blit after the call that submitted it returns, so a converter that is dropped
/// can still have blits in flight. Its owner opens a drain with <see cref="BeginDrain"/> before
/// releasing, and <see cref="MayBlit"/> refuses every holder until the drain ends.
/// </para>
/// </summary>
internal sealed class SuperResolutionLease
{
    private object? _holder;
    private int _draining;

    /// <summary>The lease shared by every view in the process.</summary>
    public static SuperResolutionLease Process { get; } = new();

    /// <summary>Whether <paramref name="owner"/> holds the lease, or could take it now.</summary>
    public bool IsAvailableTo(object owner)
    {
        var holder = Volatile.Read(ref _holder);
        return holder is null || ReferenceEquals(holder, owner);
    }

    /// <summary>Takes the lease for <paramref name="owner"/>, or confirms it already holds it.</summary>
    public bool TryAcquire(object owner)
    {
        var holder = Interlocked.CompareExchange(ref _holder, owner, null);
        return holder is null || ReferenceEquals(holder, owner);
    }

    /// <summary>Releases the lease if <paramref name="owner"/> holds it. Idempotent.</summary>
    public void Release(object owner) => Interlocked.CompareExchange(ref _holder, null, owner);

    /// <summary>
    /// Whether <paramref name="owner"/> may submit a blit now: it holds the lease and no dropped
    /// converter's blits are still draining.
    /// </summary>
    public bool MayBlit(object owner) =>
        ReferenceEquals(Volatile.Read(ref _holder), owner) && Volatile.Read(ref _draining) == 0;

    /// <summary>
    /// Holds every holder's blits back until the returned drain is ended, once the blits it stands
    /// for have completed.
    /// </summary>
    public Drain BeginDrain()
    {
        Interlocked.Increment(ref _draining);
        return new Drain(this);
    }

    /// <summary>One dropped converter's blits, holding the lease's blits back until it ends.</summary>
    internal sealed class Drain(SuperResolutionLease lease)
    {
        private int _ended;

        /// <summary>Ends the drain. Ending it twice ends it once.</summary>
        public void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                Interlocked.Decrement(ref lease._draining);
        }
    }
}
