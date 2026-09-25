// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;

namespace FrameFlow.Graph;

/// <summary>
/// Graph builder + runner. Collects nodes and wires their ports
/// together with edges, then drives the graph via <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheduling.</b> One <see cref="Task"/> per node; each task runs
/// the node's pump loop (read from inputs, invoke operator, write to
/// outputs). All inter-node buffering is via <see cref="Channel{T}"/>.
/// </para>
/// <para>
/// <b>Termination.</b> Sources complete their output ports when their
/// producer returns null (EOS). Downstream pumps observe channel
/// completion and complete their own outputs. The graph run finishes
/// when all node tasks have terminated.
/// </para>
/// <para>
/// <b>Fault propagation.</b> If any pump throws, an internal linked
/// cancellation token is signalled so the remaining pumps terminate
/// promptly. Each pump's <c>finally</c> drains its input ports and
/// disposes any leftover items so refcounts stay balanced.
/// </para>
/// <para>
/// <b>Dispatch.</b> Every node implements <see cref="IPumpableNode"/>;
/// <see cref="RunAsync"/> calls <c>RunPumpAsync</c> on each node
/// directly (virtual dispatch). No reflection.
/// </para>
/// </remarks>
public sealed class Graph
{
    private readonly List<INode> _nodes = new();
    private readonly List<Action> _wireUps = new();

    // What each wire-up wires, as a value. The closures say how an edge is built; this says what
    // shape the graph has, which is what GraphTopology's rules are about.
    private readonly List<EdgeSpec> _edges = new();

    // Output ports a chain has forked with Branch. The next edge such a port takes that is not
    // itself a branch is the trunk, and inherits the incoming ref.


    // Per-edge reset actions, run at the top of every RunAsync BEFORE the
    // wire-ups. They clear the prior run's edge state (output-port writers +
    // input-port reader) so a graph instance is re-runnable: without this, a
    // second RunAsync would APPEND a second writer to each output port (and a
    // source would then fan a frame into a now-orphaned channel), corrupting the
    // topology. Re-running a finished graph is how RepeatMode.One loops cheaply —
    // see SubstrateSession.RewindToStartAsync.
    private readonly List<Action> _resets = new();

    // Caller-registered actions, run at the top of every RunAsync before the edge resets. This is
    // where state that outlives a run is dropped: a graph is re-runnable, and a loop rewinds its
    // item and runs the same graph again, so an operator's closure survives the loop while the
    // timestamps it sees go back to zero (#217).
    private readonly List<Action> _beforeRun = new();

    // Set while a run is in flight. A second run would reset state and rewire ports under the
    // first one's pumps.
    private int _running;

    /// <summary>
    /// Registers an action to run before each run of this graph, including the first, ahead of
    /// the edge resets and the wire-ups.
    /// </summary>
    /// <param name="beforeRun">
    /// What to drop. A body that remembers anything across items — the last timestamp it saw, a
    /// window of frames, a running total — clears it here, as does a caller holding such state
    /// outside the graph entirely.
    /// </param>
    /// <remarks>
    /// <para>
    /// A graph is re-runnable, and a loop rewinds its item and runs the same graph again rather
    /// than building a new one, so nothing else tells an operator that the run it is about to see
    /// starts over. A seek and an item change build a new graph, where the question does not
    /// arise; a loop does not.
    /// </para>
    /// <para>
    /// It runs on the thread that starts the graph, before any pump, so it needs no lock against
    /// the bodies. It must not throw: an action that throws fails the run before it starts. A
    /// chain reaches its graph through <see cref="GraphChain{T}.Graph"/>, which is how a
    /// configurator registers one.
    /// </para>
    /// </remarks>
    public Graph BeforeEachRun(Action beforeRun)
    {
        ArgumentNullException.ThrowIfNull(beforeRun);
        _beforeRun.Add(beforeRun);
        return this;
    }

    /// <summary>Adds a node to the graph (idempotent).</summary>
    public T Add<T>(T node) where T : INode
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.Contains(node))
            _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Connects an output port to an input port via an edge with the
    /// given options. Both ports' owners are automatically added to
    /// the graph if not already present. An output port with more than
    /// one edge fans out, and every branch shares the item by
    /// <c>AddRef</c> (ADR-0080).
    /// </summary>
    public Graph Connect<T>(
        OutputPort<T> from,
        InputPort<T> to,
        EdgeOptions? options = null
    )
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        Add(from.Owner);
        Add(to.Owner);

        if (to.IsConnected)
        {
            throw new InvalidOperationException(
                $"Input port '{to.Owner.Id}/{to.Name}' is already connected. "
                    + "Each input port accepts exactly one upstream edge."
            );
        }
        to.IsConnected = true;

        var opts = options ?? EdgeOptions.Default;
        _edges.Add(
            new EdgeSpec(
                from,
                to,
                Blocks: opts.Overflow == Overflow.Block,
                Capacity: Math.Max(1, opts.Capacity)
            )
        );
        // Reset clears the prior run's edge state so RunAsync can be called again.
        // For a fan-out output port (multiple edges share one `from`), each edge
        // registers a Clear(); they all run before any wire-up Add(), so clearing
        // repeatedly is harmless and the writers are rebuilt from scratch each run.
        _resets.Add(() =>
        {
            from.Writers.Clear();
            to.Reader = null;
        });
        _wireUps.Add(() =>
        {
            var channel = CreateChannel<T>(opts);
            from.Writers.Add(new OutputEdge<T>(channel.Writer));
            to.Reader = channel.Reader;
        });
        return this;
    }

    /// <summary>
    /// The most items of <paramref name="source"/>'s output this graph, as wired so far, can
    /// hold at once, or the node that leaves it unbounded (ADR-0081, decision 3).
    /// </summary>
    /// <remarks>
    /// It counts the item the source's pump is writing, each edge's capacity and what each node
    /// declares (<see cref="FrameHolding"/>), up to the first storage boundary on each path. A
    /// fixed-pool source sizes its pool from it.
    /// </remarks>
    public FrameBudget FrameBudgetFor<T>(OutputPort<T> source)
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(source);
        return FrameBudgets.For(source, _edges);
    }

    /// <summary>
    /// Runs the graph to completion. Returns when every node's pump
    /// loop has terminated (EOS propagated, cancellation requested,
    /// or a pump failed).
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // One run at a time. A second would drop the state the first is using and rewire the
        // ports under its pumps.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException(
                "This graph is already running. Await the run in flight before starting another."
            );
        }

        try
        {
            await RunOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // Drop what the caller keeps between runs, reset any edge state left over from a previous
        // run, then (re)wire fresh channels. Resets run before wire-ups so a fan-out output port's
        // writers are cleared once and rebuilt, never accumulated across runs. On the first run
        // they act on empty ports (no-op). This is what makes a graph instance re-runnable for the
        // loop's in-place rewind, and the registered actions are what tell an operator that the
        // run it is about to see starts over.
        // The topology is checked before anything is reset or wired, so a malformed graph fails
        // the run it was started for rather than hanging in a pump that waits for an item no
        // edge can deliver.
        var errors = GraphTopology.Validate(_nodes, _edges);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "This graph is not wired correctly:" + Environment.NewLine + "  "
                    + string.Join(Environment.NewLine + "  ", errors)
            );
        }

        // Each fixed-pool source learns what this graph can hold of its frames before anything
        // runs, and may refuse the run (ADR-0081, decision 4).
        foreach (var node in _nodes)
        {
            if (node is IBudgetedSource { WantsBudget: true } budgeted)
                budgeted.ApplyBudget(FrameBudgets.For(budgeted.BudgetedOutput, _edges));
        }

        foreach (var beforeRun in _beforeRun)
            beforeRun();

        foreach (var reset in _resets)
            reset();

        foreach (var wire in _wireUps)
            wire();

        using var graphCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = new List<Task>(_nodes.Count);
        foreach (var node in _nodes)
        {
            tasks.Add(((IPumpableNode)node).RunPumpAsync(graphCts));
        }

        // Pumps handle fault-propagation internally: each pump's
        // finally cancels graphCts if it exits via exception. Sibling
        // pumps observe cancellation and exit cleanly. No pump cancels
        // on a clean exit — one branch reaching EOS says nothing about
        // the others, so a pump that has nothing left to do drains its
        // inputs instead of tearing the graph down.
        //
        // The OCE-suppression below distinguishes "caller cancelled
        // the graph" (legitimate, propagate) from "a pump triggered
        // internal cleanup cancellation" (normal end-of-graph,
        // swallow). Real pump exceptions still surface — they're
        // Faulted, not Canceled, and surface before any OCE in the
        // WhenAll aggregation.
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Internal cleanup cancellation; normal end-of-graph.
            // Surface any pump exception if one exists.
            foreach (var t in tasks)
            {
                if (t.IsFaulted)
                {
                    // Re-throw the first faulted pump's exception.
                    await t.ConfigureAwait(false);
                }
            }
            // No pump faulted — all cancellation was from internal cleanup.
        }
    }

    private static Channel<T> CreateChannel<T>(EdgeOptions opts)
        where T : class, IRefCounted
    {
        var capacity = Math.Max(1, opts.Capacity);
        var fullMode = opts.Overflow switch
        {
            Overflow.Block => BoundedChannelFullMode.Wait,
            Overflow.DropIncoming => BoundedChannelFullMode.DropWrite,
            Overflow.DropOldest => BoundedChannelFullMode.DropOldest,
            _ => throw new ArgumentOutOfRangeException(nameof(opts), opts.Overflow, null),
        };
        return Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = fullMode,
                SingleReader = true,
                SingleWriter = true,
            },
            itemDropped: dropped => dropped.Dispose()
        );
    }
}
