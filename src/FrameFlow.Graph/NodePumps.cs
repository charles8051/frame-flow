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
///       - if exiting on a fault, records it in the <see cref="GraphRun"/>,
///         which cancels the run so siblings (esp. upstream sources) stop
///         producing,
///       - async-drains input ports so items still in upstream
///         buffers get disposed,
///       - completes output ports so downstream pumps terminate.
/// </summary>
/// <remarks>
/// An exception from a body is the run's cancellation only when the run's token is cancelled
/// (<see cref="FaultRules.IsCancellation"/>). Any other one, an
/// <see cref="OperationCanceledException"/> included, takes the node's
/// <see cref="FailureResponse"/> (#489).
/// </remarks>
internal static class NodePumps
{
    // ─────────────────────────────────────────────────────────────
    // Source
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpSourceAsync<TOut>(SourceNode<TOut> node, GraphRun run)
        where TOut : class, IRefCounted
    {
        var ct = run.Token;
        var outputs = node.Output.Writers;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TOut? item;
                try
                {
                    item = await node.Body(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        run.Fault(node, ex);
                        throw;
                    }
                    continue;
                }

                if (item is null)
                    break; // EOS

                await ForwardAsync(item, outputs, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            run.Ended(node, ex);
            throw;
        }
        finally
        {
            foreach (var edge in outputs)
                edge.Writer.TryComplete();
            await RunCleanupAsync(node.Cleanup).ConfigureAwait(false);
        }
    }

    // A node's cleanup is best-effort: a throw from it must not mask the fault that ended the
    // pump, or turn a clean end into a failed run.
    private static async ValueTask RunCleanupAsync(Func<ValueTask>? cleanup)
    {
        if (cleanup is null)
            return;
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch
        {
            // Swallowed; see above.
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1→0..1 Operator
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpOperatorAsync<TIn, TOut>(
        OperatorNode<TIn, TOut> node,
        GraphRun run
    )
        where TIn : class, IRefCounted
        where TOut : class, IRefCounted
    {
        var ct = run.Token;
        var input = RequireConnected(node.Input);
        var outputs = node.Output.Writers;

        try
        {
            await foreach (var item in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                TOut? result;
                try
                {
                    result = await node.Body(item, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                {
                    item.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        run.Fault(node, ex);
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
        catch (Exception ex)
        {
            // A fault stops sibling pumps (esp. upstream) BEFORE the
            // async-drain below. Otherwise the drain would wait forever for
            // upstream to complete its writer.
            run.Ended(node, ex);
            throw;
        }
        finally
        {
            await DrainUntilCompletedAsync(input).ConfigureAwait(false);
            foreach (var edge in outputs)
                edge.Writer.TryComplete();
            await RunCleanupAsync(node.Cleanup).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1→N MultiOperator
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpMultiOperatorAsync<TIn, TOut>(
        MultiOperatorNode<TIn, TOut> node,
        GraphRun run
    )
        where TIn : class, IRefCounted
        where TOut : class, IRefCounted
    {
        var ct = run.Token;
        var input = RequireConnected(node.Input);
        var outputs = node.Output.Writers;

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
                catch (Exception ex) when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                {
                    item.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        run.Fault(node, ex);
                        throw;
                    }
                    continue;
                }

                item.Dispose();
            }
        }
        catch (Exception ex)
        {
            run.Ended(node, ex);
            throw;
        }
        finally
        {
            await DrainUntilCompletedAsync(input).ConfigureAwait(false);
            foreach (var edge in outputs)
                edge.Writer.TryComplete();
            await RunCleanupAsync(node.Cleanup).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Sink
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpSinkAsync<TIn>(SinkNode<TIn> node, GraphRun run)
        where TIn : class, IRefCounted
    {
        var ct = run.Token;
        var input = RequireConnected(node.Input);

        try
        {
            await foreach (var item in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await node.Body(item, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                {
                    item.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        run.Fault(node, ex);
                        throw;
                    }
                    continue;
                }
                item.Dispose();
            }
        }
        catch (Exception ex)
        {
            run.Ended(node, ex);
            throw;
        }
        finally
        {
            await DrainUntilCompletedAsync(input).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Sync join (2→0..1, primary-driven)
    // ─────────────────────────────────────────────────────────────

    public static async Task PumpSyncJoinAsync<TPrimary, TSecondary, TOut>(
        SyncJoinNode<TPrimary, TSecondary, TOut> node,
        GraphRun run
    )
        where TPrimary : class, IRefCounted
        where TSecondary : class, IRefCounted
        where TOut : class, IRefCounted
    {
        var ct = run.Token;
        var primary = RequireConnected(node.Primary);
        var secondary = RequireConnected(node.Secondary);
        var outputs = node.Output.Writers;
        var retained = node.Retained;

        // The pump's teardown clears the window, so a run starts with an empty one. Cleared here
        // as well because a run that never reached that teardown — a graph abandoned mid-run —
        // must not leave its secondaries to match the next run's primaries.
        retained.Clear();

        // After primary EOS the secondary loop keeps reading, but discards
        // instead of admitting. It must keep reading: a secondary upstream with
        // more pending items than the edge's free capacity blocks in WriteAsync
        // forever if nobody drains it, and Graph.RunAsync would never return.
        // Cancelling the run here instead would be wrong the other way — the
        // join sits on one branch, and video EOS is not audio EOS.
        //
        // A finite secondary therefore completes its writer and ends this loop
        // on its own. An unbounded one (a live camera) keeps it alive until the
        // graph token fires, which is the same deal every consumer of an
        // unbounded source gets.
        var primaryDone = false;

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

                            // Past MaxLead, or at MaxRetained with every entry still a
                            // candidate, stop reading the edge until the primary catches
                            // up. The edge fills, and its overflow policy decides what the
                            // producer does.
                            while (
                                retained.TryAdmit(
                                    item,
                                    from,
                                    to,
                                    node.Window,
                                    node.MaxLead,
                                    node.MaxRetained,
                                    node.MatchPolicy,
                                    node.MaxStaleness
                                ) is { } room
                            )
                                await room.WaitAsync(primaryEnded.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException held)
                            when (held.CancellationToken == primaryEnded.Token)
                        {
                            // Held when the primary ended or the graph tore down. The
                            // window never took it. After EOS the loop carries on
                            // discarding; on teardown it exits. Matched on the wait's own
                            // token, so a key selector's own cancellation is not taken
                            // for this one.
                            item.Dispose();
                            if (ct.IsCancellationRequested)
                                throw;
                        }
                        catch (Exception ex)
                            when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                        {
                            item.Dispose();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // The key selector or the frame check threw. The window
                            // never took ownership, so this ref is still ours.
                            item.Dispose();
                            if (node.OnError == FailureResponse.Propagate)
                            {
                                // Record, and so cancel, at the fault site. Deferring
                                // to the primary's exit means "never" while the
                                // primary is live: nobody reads this edge any more,
                                // so its upstream blocks once the edge fills, and the
                                // join emits every primary unmatched in the meantime.
                                run.Fault(node, ex);
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The graph tearing down, or a fault outside the key selector, which
                    // is the join's whatever its failure response.
                    run.Ended(node, ex);
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
                catch (Exception ex) when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                {
                    item.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    item.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        run.Fault(node, ex);
                        throw;
                    }
                    continue;
                }

                TOut? result;
                try
                {
                    result = await node.Body(item, match, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (FaultRules.IsCancellation(ex, ct.IsCancellationRequested))
                {
                    item.Dispose();
                    match?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    item.Dispose();
                    match?.Dispose();
                    if (node.OnError == FailureResponse.Propagate)
                    {
                        run.Fault(node, ex);
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
        catch (Exception ex)
        {
            // A cancellation, including the one a secondary fault requested, which the run
            // already holds; or a fault of the primary side, recorded before the teardown below.
            run.Ended(node, ex);
            throw;
        }
        finally
        {
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
                // Best-effort. The secondary loop records its own fault in the run, which is
                // where the graph surfaces it from.
            }

            // The secondary loop exits early on teardown, and a lead bound can leave
            // the edge full when it does. Whatever is still on it is ours to dispose.
            await DrainUntilCompletedAsync(secondary).ConfigureAwait(false);
            await DrainUntilCompletedAsync(primary).ConfigureAwait(false);
            retained.Clear();

            foreach (var edge in outputs)
                edge.Writer.TryComplete();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Shared infrastructure
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Distributes one upstream item across all outgoing branches. The first branch takes the
    /// incoming reference and every other branch an <c>AddRef</c> of it, which returns the same
    /// instance (ADR-0080), so every branch holds the one item.
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

        // Take every branch's reference before any write. A branch that is written first and
        // read at once releases its reference, which must not be the last one while a later
        // branch still has to be written. If an AddRef throws, nothing has been written: release
        // the references taken so far and the incoming one, and rethrow.
        int taken = 0;
        try
        {
            for (; taken < outputs.Count - 1; taken++)
                item.AddRef();
        }
        catch
        {
            for (int i = 0; i <= taken; i++)
                item.Dispose();
            throw;
        }

        if (outputs.Count == 1)
        {
            await WriteOrDisposeAsync(outputs[0].Writer, item, ct).ConfigureAwait(false);
            return;
        }

        var writeTasks = new Task[outputs.Count];
        for (int i = 0; i < outputs.Count; i++)
            writeTasks[i] = WriteOrDisposeAsync(outputs[i].Writer, item, ct).AsTask();

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

    private static ChannelReader<T> RequireConnected<T>(InputPort<T> port)
        where T : class, IRefCounted =>
        port.Reader
        ?? throw new InvalidOperationException(
            $"Input port '{port.Owner.Id}/{port.Name}' has no upstream edge connected."
        );
}
