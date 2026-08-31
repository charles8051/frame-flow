// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;

namespace FrameFlow.Graph;

/// <summary>
/// Per-node pump loop implementations. Each pump:
/// (a) drives the node's body to completion or cancellation,
/// (b) implements the always-refcount ownership protocol (substrate
///     handles AddRef/Dispose around each operator invocation),
/// (c) on exit (success or failure):
///       - if exiting via exception, signals graph cancellation so
///         siblings (esp. upstream sources) stop producing,
///       - async-drains input ports so items still in upstream
///         buffers get disposed,
///       - completes output ports so downstream pumps terminate.
/// </summary>
internal static class NodePumps
{
    // ─────────────────────────────────────────────────────────────
    // Source
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpSourceAsync<TOut>(
        SourceNode<TOut> node,
        CancellationTokenSource graphCts
    )
        where TOut : class, IRefCounted
    {
        var ct = graphCts.Token;
        var outputs = node.Output.Writers;
        bool faulted = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TOut? item;
                try
                {
                    item = await node.Body(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        faulted = true;
                        throw;
                    }
                    continue;
                }

                if (item is null)
                    break; // EOS

                await ForwardAsync(item, outputs, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            if (faulted)
                TryCancel(graphCts);
            foreach (var edge in outputs)
                edge.Writer.TryComplete();
            if (node.Cleanup is not null)
            {
                try { await node.Cleanup().ConfigureAwait(false); }
                catch { /* cleanup is best-effort; don't mask primary fault */ }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1→0..1 Operator
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpOperatorAsync<TIn, TOut>(
        OperatorNode<TIn, TOut> node,
        CancellationTokenSource graphCts
    )
        where TIn : class, IRefCounted
        where TOut : class, IRefCounted
    {
        var ct = graphCts.Token;
        var input = RequireConnected(node.Input);
        var outputs = node.Output.Writers;
        bool faulted = false;

        try
        {
            await foreach (var item in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                TOut? result;
                try
                {
                    result = await node.Body(item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    item.Dispose();
                    throw;
                }
                catch
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        faulted = true;
                        throw;
                    }
                    continue;
                }

                if (!ReferenceEquals(item, result))
                    item.Dispose();

                if (result is null)
                    continue;

                await ForwardAsync(result, outputs, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            // If exiting via exception, signal sibling pumps (esp.
            // upstream) so they stop producing BEFORE we async-drain.
            // Otherwise the drain would wait forever for upstream to
            // complete its writer.
            if (faulted)
                TryCancel(graphCts);
            await DrainUntilCompletedAsync(input).ConfigureAwait(false);
            foreach (var edge in outputs)
                edge.Writer.TryComplete();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1→N MultiOperator
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpMultiOperatorAsync<TIn, TOut>(
        MultiOperatorNode<TIn, TOut> node,
        CancellationTokenSource graphCts
    )
        where TIn : class, IRefCounted
        where TOut : class, IRefCounted
    {
        var ct = graphCts.Token;
        var input = RequireConnected(node.Input);
        var outputs = node.Output.Writers;
        bool faulted = false;

        try
        {
            await foreach (var item in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await foreach (
                        var output in node.Body(item, ct).WithCancellation(ct).ConfigureAwait(false)
                    )
                    {
                        await ForwardAsync(output, outputs, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Dispose();
                    throw;
                }
                catch
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        faulted = true;
                        throw;
                    }
                    continue;
                }

                item.Dispose();
            }
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            if (faulted)
                TryCancel(graphCts);
            await DrainUntilCompletedAsync(input).ConfigureAwait(false);
            foreach (var edge in outputs)
                edge.Writer.TryComplete();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Sink
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpSinkAsync<TIn>(
        SinkNode<TIn> node,
        CancellationTokenSource graphCts
    )
        where TIn : class, IRefCounted
    {
        var ct = graphCts.Token;
        var input = RequireConnected(node.Input);
        bool faulted = false;

        try
        {
            await foreach (var item in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await node.Body(item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    item.Dispose();
                    throw;
                }
                catch
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        faulted = true;
                        throw;
                    }
                    continue;
                }
                item.Dispose();
            }
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            if (faulted)
                TryCancel(graphCts);
            await DrainUntilCompletedAsync(input).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Shared infrastructure
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Distributes one upstream item across all outgoing branches.
    /// Per ADR-0054, each branch either inherits the incoming ref
    /// (the first cloner-less branch), receives a fresh ref via
    /// <c>AddRef</c> (every other cloner-less branch), or receives
    /// an independently-produced item via its
    /// <see cref="OutputEdge{T}.Cloner"/>. When every branch has a
    /// cloner, the incoming ref is disposed once all clones are
    /// produced — no branch inherited it.
    /// </summary>
    private static async ValueTask ForwardAsync<T>(
        T item,
        List<OutputEdge<T>> outputs,
        CancellationToken ct
    )
        where T : class, IRefCounted
    {
        if (outputs.Count == 0)
        {
            item.Dispose();
            return;
        }

        // First cloner-less branch (if any) inherits the incoming ref.
        // All other cloner-less branches AddRef; cloner branches clone.
        // If every branch has a cloner, the incoming ref has no
        // inheritor and is disposed below after the clones land.
        int firstNoCloner = -1;
        for (int i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].Cloner is null)
            {
                firstNoCloner = i;
                break;
            }
        }

        // Materialise per-branch items up front so a cloner that
        // throws doesn't leave partially-produced refs leaking. On
        // failure, no write has happened yet — so dispose every
        // per-branch ref already produced AND the incoming ref, then
        // rethrow. (See ADR-0054 "Cloner throws".)
        var branchItems = new T?[outputs.Count];
        try
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                var edge = outputs[i];
                if (i == firstNoCloner)
                    branchItems[i] = item;
                else if (edge.Cloner is { } clone)
                    branchItems[i] = clone(item);
                else
                    branchItems[i] = (T)item.AddRef();
            }
        }
        catch
        {
            // No branch has been written yet: ForwardAsync still owns the
            // incoming ref plus every per-branch ref it produced. Dispose
            // them all. For AddRef-returns-this types (RefBox) the AddRef'd
            // slots ARE `item`, so disposing each balances its increment;
            // for new-wrapper types (VideoFrameRef) they dispose
            // independently.
            for (int j = 0; j < branchItems.Length; j++)
                branchItems[j]?.Dispose();

            // If the inheriting slot was never assigned, the incoming ref
            // hasn't been released yet. (If it was, the loop above already
            // released it exactly once via that slot.)
            if (firstNoCloner < 0 || branchItems[firstNoCloner] is null)
                item.Dispose();
            throw;
        }

        // In the all-cloner case nothing inherited the incoming ref: every
        // branch holds an independent clone, so the incoming ref is dead
        // weight now. Release it BEFORE the writes — that keeps the write
        // path leak-free too, since a write that throws on cancellation
        // then only has to account for its own branch item (which
        // WriteOrDisposeAsync disposes).
        if (firstNoCloner < 0)
            item.Dispose();

        var writeTasks = new Task[outputs.Count];
        for (int i = 0; i < outputs.Count; i++)
        {
            writeTasks[i] = WriteOrDisposeAsync(outputs[i].Writer, branchItems[i]!, ct).AsTask();
        }

        await Task.WhenAll(writeTasks).ConfigureAwait(false);
    }

    private static async ValueTask WriteOrDisposeAsync<T>(
        ChannelWriter<T> writer,
        T item,
        CancellationToken ct
    )
        where T : class, IRefCounted
    {
        try
        {
            await writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            item.Dispose();
        }
        catch (OperationCanceledException)
        {
            item.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Async-drains a channel reader until upstream signals completion.
    /// Uses <see cref="CancellationToken.None"/> so the drain itself
    /// isn't short-circuited by a cancelled graph; upstream's pump
    /// (cancelled separately) will complete its writer, ending the
    /// drain naturally.
    /// </summary>
    private static async Task DrainUntilCompletedAsync<T>(ChannelReader<T> reader)
        where T : class, IRefCounted
    {
        try
        {
            await foreach (
                var item in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)
            )
            {
                item.Dispose();
            }
        }
        catch
        {
            // Best-effort cleanup. Swallow secondary exceptions so the
            // primary cause surfaces.
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch { /* already disposed */ }
    }

    private static ChannelReader<T> RequireConnected<T>(InputPort<T> port)
        where T : class, IRefCounted =>
        port.Reader
        ?? throw new InvalidOperationException(
            $"Input port '{port.Owner.Id}/{port.Name}' has no upstream edge connected."
        );
}
