// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What the pumps of one run of a graph share: the token that stops them all, and the fault that
/// ended the run.
/// </summary>
/// <remarks>
/// <para>
/// A pump reports its fault here rather than through its task. A pump is an <c>async</c> method,
/// and one that ends by throwing an <see cref="OperationCanceledException"/> ends Canceled, not
/// Faulted, so a body's own <see cref="TaskCanceledException"/> would otherwise read as the run
/// being cancelled (#489).
/// </para>
/// <para>
/// Recording a fault cancels the run, so the other pumps stop producing before the faulting one
/// drains its inputs. A pump records a fault in the catch that caught it, so the first recorded
/// is the first a pump caught. Two pumps that fault at once are recorded in whichever order they
/// reach <see cref="Fault"/>, which need not be the order their bodies threw.
/// </para>
/// </remarks>
internal sealed class GraphRun : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private Exception? _firstFault;

    /// <summary>A run that is also cancelled when <paramref name="callerToken"/> is.</summary>
    public GraphRun(CancellationToken callerToken) =>
        _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

    /// <summary>Cancelled when the caller cancels or a pump faults.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The first fault recorded in this run, or <see langword="null"/> when none was.</summary>
    public Exception? FirstFault => Volatile.Read(ref _firstFault);

    /// <summary>Records <paramref name="ex"/> as the fault of <paramref name="node"/> and stops the run.</summary>
    /// <remarks>
    /// For a caller that has already decided <paramref name="ex"/> is a fault, such as a body's
    /// catch site under <see cref="FailureResponse.Propagate"/>. Recording the same exception
    /// twice records it once.
    /// </remarks>
    public void Fault(INode node, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(ex);
        Interlocked.CompareExchange(ref _firstFault, ex, null);
        Cancel();
    }

    /// <summary>
    /// Records <paramref name="ex"/>, which ended <paramref name="node"/>'s pump, as its fault and
    /// stops the run, unless it is the run's cancellation.
    /// </summary>
    public void Ended(INode node, Exception ex)
    {
        if (!FaultRules.IsCancellation(ex, Token.IsCancellationRequested))
            Fault(node, ex);
    }

    private void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Disposed once the run is over, when nothing is left to stop, or a callback
            // registered on the token threw. Neither may replace the fault being recorded.
        }
    }

    public void Dispose() => _cts.Dispose();
}
