// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A cancellation linked to other tokens, like
/// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken[])"/>, whose dispose
/// never waits for a link callback that another thread is running (#785).
/// </summary>
/// <remarks>
/// <para>
/// <b>The wait this removes.</b> Disposing a linked <see cref="CancellationTokenSource"/>
/// unregisters it from its parents, and an unregistration waits for the callback if another thread
/// is running it at that moment. That is the case on every stop of a wait that resumes on another
/// thread. The thread that cancels runs the link callbacks one inside the other, down to the wait's
/// own. A wait whose continuation is queued rather than inlined, such as a
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, resumes on a pool thread
/// while the canceller is still inside that chain, and the first thing it does on its way out is
/// dispose its linked source. The runtime waits for the callback with a <see cref="SpinWait"/>,
/// which falls back to <c>Thread.Sleep(1)</c>. On Windows that sleep lasts until the next system
/// timer tick, up to 15.6 ms. A paused pacer's dispose lost that tick on many of its stops.
/// </para>
/// <para>
/// <b>How.</b> Each link is a registration of its own, and <see cref="Dispose"/> removes them with
/// <see cref="CancellationTokenRegistration.Unregister"/>, which does not wait. The source is
/// disposed only when every link was removed before it ran. A link that ran or is running may
/// still be cancelling the source on another thread, so the source is left to the collector then.
/// It has no timer and no wait handle, so it holds nothing the collector cannot reclaim.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Token"/> is safe from any thread. <see cref="Dispose"/> is for the
/// owner, once its use of the token has ended, as with a linked source.
/// </para>
/// </remarks>
internal sealed class LinkedCancellation : IDisposable
{
    private static readonly Action<object?> CancelSource = static state =>
        ((CancellationTokenSource)state!).Cancel();

    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration[] _links;
    private bool _disposed;

    private LinkedCancellation(ReadOnlySpan<CancellationToken> tokens)
    {
        int count = 0;
        foreach (var token in tokens)
        {
            if (token.CanBeCanceled)
                count++;
        }

        // Only tokens that can cancel get a link: a default registration cannot be unregistered,
        // and Unregister reports it as one that ran.
        _links = new CancellationTokenRegistration[count];
        int next = 0;
        foreach (var token in tokens)
        {
            if (token.CanBeCanceled)
                _links[next++] = token.UnsafeRegister(CancelSource, _source);
        }
    }

    /// <summary>Links a new cancellation to <paramref name="tokens"/>.</summary>
    /// <returns>A cancellation whose token is cancelled once any of <paramref name="tokens"/> is.</returns>
    public static LinkedCancellation Create(params ReadOnlySpan<CancellationToken> tokens) => new(tokens);

    /// <summary>The token, cancelled once any linked token is.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Whether any linked token has cancelled this one.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>Removes the links without waiting for one that another thread is running.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        bool removedAll = true;
        foreach (ref var link in _links.AsSpan())
            removedAll &= link.Unregister();
        if (removedAll)
            _source.Dispose();
    }
}
