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
    // Sync join (2→0..1, primary-driven)
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpSyncJoinAsync<TPrimary, TSecondary, TOut>(
        SyncJoinNode<TPrimary, TSecondary, TOut> node,
        CancellationTokenSource graphCts
    )
        where TPrimary : class, IRefCounted
        where TSecondary : class, IRefCounted
        where TOut : class, IRefCounted
    {
        var ct = graphCts.Token;
        var primary = RequireConnected(node.Primary);
        var secondary = RequireConnected(node.Secondary);
        var outputs = node.Output.Writers;
        var retained = node.Retained;
        bool faulted = false;

        // The pump's teardown clears the window, so a run starts with an empty one. Cleared here
        // as well because a run that never reached that teardown — a graph abandoned mid-run —
        // must not leave its secondaries to match the next run's primaries.
        retained.Clear();

        // After primary EOS the secondary loop keeps reading, but discards
        // instead of admitting. It must keep reading: a secondary upstream with
        // more pending items than the edge's free capacity blocks in WriteAsync
        // forever if nobody drains it, and Graph.RunAsync would never return.
        // Cancelling graphCts here instead would be wrong the other way — the
        // join sits on one branch, and video EOS is not audio EOS.
        //
        // A finite secondary therefore completes its writer and ends this loop
        // on its own. An unbounded one (a live camera) keeps it alive until the
        // graph token fires, which is the same deal every consumer of an
        // unbounded source gets.
        var primaryDone = false;
        Exception? secondaryFault = null;

        // Cancelled when the primary ends. A secondary held on the lead bound waits on
        // this, because after EOS no primary will advance to release it, and the drain
        // below has to reach the rest of the edge.
        using var primaryEnded = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var secondaryLoop = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (
                        var item in secondary.ReadAllAsync(ct).ConfigureAwait(false)
                    )
                    {
                        if (Volatile.Read(ref primaryDone))
                        {
                            item.Dispose();
                            continue;
                        }

                        try
                        {
                            // The constructor refuses a frame-typed secondary without a lead;
                            // this catches a frame arriving through a broader declared type.
                            node.ThrowIfFrameWithoutLead(item);

                            var (from, to) = node.Keys.SecondaryInterval(item);

                            // Past MaxLead, stop reading the edge until the primary
                            // catches up. The edge fills, and its overflow policy
                            // decides what the producer does.
                            while (retained.TryAdmit(item, from, to, node.MaxLead) is { } room)
                                await room.WaitAsync(primaryEnded.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (primaryEnded.IsCancellationRequested)
                        {
                            // Held when the primary ended or the graph tore down. The
                            // window never took it. After EOS the loop carries on
                            // discarding; on teardown it exits.
                            item.Dispose();
                            if (ct.IsCancellationRequested)
                                throw;
                        }
                        catch (Exception ex)
                        {
                            // The key selector or the frame check threw. The window
                            // never took ownership, so this ref is still ours.
                            item.Dispose();
                            if (node.OnError == FailureResponse.Propagate)
                            {
                                // Cancel at the fault site, the way every other
                                // pump's finally does. Deferring to the primary's
                                // exit means "never" while the primary is live:
                                // nobody reads this edge any more, so its upstream
                                // blocks once the edge fills, and the join emits
                                // every primary unmatched in the meantime.
                                secondaryFault ??= ex;
                                TryCancel(graphCts);
                                return;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Primary ended, or the graph is tearing down.
                }
            },
            CancellationToken.None
        );

        try
        {
            await foreach (var item in primary.ReadAllAsync(ct).ConfigureAwait(false))
            {
                TSecondary? match;
                try
                {
                    var t = node.Keys.PrimaryTime(item);
                    match = retained.AdvanceAndMatch(
                        t,
                        node.MatchPolicy,
                        node.Window,
                        node.MaxStaleness
                    );
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

                TOut? result;
                try
                {
                    result = await node.Body(item, match, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    item.Dispose();
                    match?.Dispose();
                    throw;
                }
                catch
                {
                    item.Dispose();
                    match?.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        faulted = true;
                        throw;
                    }
                    continue;
                }

                // Pass-through applies to either input: a body may return the
                // primary, or the secondary, and the substrate then forwards
                // that same ref instead of releasing it here. Every other input
                // ref is released, including the second of two refs on one
                // instance: when a fork feeds the same item to both inputs, the
                // primary and the match are one object, and only one of its two
                // refs goes downstream (ADR-0080, decision 3).
                bool forwarded = false;
                if (match is not null)
                {
                    if (ReferenceEquals(match, result))
                        forwarded = true;
                    else
                        match.Dispose();
                }
                if (!forwarded && ReferenceEquals(item, result))
                    forwarded = true;
                else
                    item.Dispose();

                if (result is null)
                    continue;

                await ForwardAsync(result, outputs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            faulted = true;
            // When the secondary faulted, this cancellation is its consequence.
            // Surface the cause rather than the symptom; the post-finally
            // rethrow below is unreachable on this path.
            if (secondaryFault is not null && node.OnError == FailureResponse.Propagate)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(secondaryFault)
                    .Throw();
            }
            throw;
        }
        catch
        {
            // A genuine primary fault wins over a secondary one.
            faulted = true;
            throw;
        }
        finally
        {
            if (faulted)
                TryCancel(graphCts);

            // Flip the secondary loop to discard-only, then let it run to the
            // secondary's own EOS (or graph cancellation). Completing the
            // output first would be premature: downstream EOS is signalled
            // below, once nothing else can be emitted.
            Volatile.Write(ref primaryDone, true);
            primaryEnded.Cancel();
            try
            {
                await secondaryLoop.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; the primary's fault (if any) is the cause worth
                // surfacing.
            }

            // The secondary loop exits early on teardown, and a lead bound can leave
            // the edge full when it does. Whatever is still on it is ours to dispose.
            await DrainUntilCompletedAsync(secondary).ConfigureAwait(false);
            await DrainUntilCompletedAsync(primary).ConfigureAwait(false);
            retained.Clear();

            foreach (var edge in outputs)
                edge.Writer.TryComplete();
        }

        // A secondary-side fault has to be surfaced here or not at all: the
        // pump's observable result is the primary loop's, and the primary may
        // have exited through the cancellation the secondary itself requested.
        if (secondaryFault is not null && node.OnError == FailureResponse.Propagate)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(secondaryFault)
                .Throw();
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

        // Who inherits the incoming ref. A chain-built fork names its trunk, because Branch
        // wires the sibling first and the scan below would otherwise hand the ref to a
        // cloner-less sibling. Everything else keeps ADR-0054's rule: the first cloner-less
        // branch inherits, other cloner-less branches AddRef, cloner branches clone, and if
        // every branch clones the incoming ref has no inheritor and is disposed below.
        int firstNoCloner = -1;
        for (int i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].Inherit)
            {
                firstNoCloner = i;
                break;
            }

            if (outputs[i].Cloner is null && firstNoCloner < 0)
                firstNoCloner = i;
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
                {
                    // AddRef returns the same instance (ADR-0080, decision 1), so the branch
                    // takes the item it already holds.
                    item.AddRef();
                    branchItems[i] = item;
                }
            }
        }
        catch
        {
            // No branch has been written yet: ForwardAsync still owns the
            // incoming ref plus every per-branch ref it produced. Dispose
            // them all. An AddRef'd slot IS `item`, so disposing each
            // balances its increment.
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
