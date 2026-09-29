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
/// <b>Fault propagation.</b> If any pump faults, an internal linked
/// cancellation token is signalled so the remaining pumps terminate
/// promptly, and <see cref="RunAsync"/> throws a
/// <see cref="GraphFaultException"/> naming the node whose fault was
/// recorded first. Each pump's <c>finally</c> drains its input ports and
/// disposes any leftover items so refcounts stay balanced. An
/// <see cref="OperationCanceledException"/> from a node body is a fault
/// like any other unless the graph was cancelled (#489).
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

    // What each node has discarded, at the node's index in _nodes. Kept for the graph's life, so a
    // count covers every run (#501).
    private readonly List<DiscardTally> _discards = new();

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
        {
            _nodes.Add(node);
            _discards.Add(new DiscardTally(node.Id));
        }
        return node;
    }

    /// <summary>
    /// Every node of this graph that has discarded an input under
    /// <see cref="FailureResponse.Discard"/>, in the order the nodes were added, with how many and
    /// what the last one threw (#501).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot, safe to read from any thread while the graph runs. Counts cover every run of
    /// this graph and never reset. A node that has discarded nothing is not listed.
    /// </para>
    /// <para>
    /// The same discards are counted process-wide as <c>frameflow.graph.discards</c> on the
    /// <c>FrameFlow.Graph</c> meter, tagged <c>node</c> with the node's id.
    /// </para>
    /// </remarks>
    public IReadOnlyList<NodeDiscards> Discards
    {
        get
        {
            var discards = new List<NodeDiscards>();
            foreach (var tally in _discards)
            {
                if (tally.Snapshot() is { } snapshot)
                    discards.Add(snapshot);
            }
            return discards;
        }
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

        RequireUnconnected(to);
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

    /// <summary>Throws when <paramref name="to"/> already has its one upstream edge.</summary>
    internal static void RequireUnconnected<T>(InputPort<T> to)
        where T : class, IRefCounted
    {
        if (to.IsConnected)
        {
            throw new InvalidOperationException(
                $"Input port '{to.Owner.Id}/{to.Name}' is already connected. "
                    + "Each input port accepts exactly one upstream edge."
            );
        }
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
    /// The first node, walking from <paramref name="source"/>, that one of
    /// <paramref name="emitted"/> can reach and that does not accept it, or <see langword="null"/>
    /// when every node on the way accepts what can reach it (#435).
    /// </summary>
    /// <remarks>
    /// Each node passes on the domains that arrived unless it declares an output domain
    /// (<see cref="FrameDomainRule"/>), as a download to system memory does. A node that declares
    /// nothing accepts either domain. A builder that knows its source's domains before the graph
    /// runs uses this to refuse a graph at build; <see cref="RunAsync"/> refuses one whose source
    /// declares its domains.
    /// </remarks>
    public FrameDomainMismatch? FrameDomainMismatchFor<T>(OutputPort<T> source, FrameMemoryDomains emitted)
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(source);
        return FrameDomainChecks.For(source, emitted, _edges);
    }

    /// <summary>
    /// As <see cref="FrameDomainMismatchFor{T}(OutputPort{T}, FrameMemoryDomains)"/>, with a node
    /// that declares nothing taking <paramref name="undeclared"/>.
    /// </summary>
    /// <remarks>
    /// With <see cref="FrameDomainRule.CpuOnly"/>, a null result means every node that GPU frames
    /// reach has said it takes them. A builder that decides whether to hand the graph GPU frames
    /// asks that, rather than whether any node would refuse them (#294).
    /// </remarks>
    public FrameDomainMismatch? FrameDomainMismatchFor<T>(
        OutputPort<T> source,
        FrameMemoryDomains emitted,
        FrameDomainRule undeclared)
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(undeclared);
        return FrameDomainChecks.For(source, emitted, _edges, undeclared);
    }

    /// <summary>
    /// Runs the graph to completion. Returns when every node's pump
    /// loop has terminated (EOS propagated, cancellation requested,
    /// or a pump failed).
    /// </summary>
    /// <exception cref="GraphFaultException">
    /// One or more nodes faulted. It names the node whose fault was recorded first and holds that
    /// fault as its inner exception, and every node's fault in <see cref="GraphFaultException.Faults"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled and no node faulted.</exception>
    /// <exception cref="InvalidOperationException">
    /// The graph is already running, or it was refused before any node ran because it is wired
    /// wrongly, two nodes share an id, or a node cannot take a memory domain its source emits.
    /// </exception>
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

        // A source that says which memory domains its frames can be in has each of them checked
        // against the nodes it can reach, so a node that cannot read one is named here rather
        // than failing on the first such frame (#435).
        foreach (var node in _nodes)
        {
            if (node is IDomainSource source
                && source.EmittedDomains is { } emitted
                && FrameDomainChecks.For(source.DomainOutput, emitted, _edges) is { } mismatch)
            {
                throw new InvalidOperationException(mismatch.Message);
            }
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

        using var run = new GraphRun(_nodes, _discards, ct);

        var tasks = new List<Task>(_nodes.Count);
        foreach (var node in _nodes)
        {
            tasks.Add(PumpAsync((IPumpableNode)node, run));
        }

        // A pump that faults records the fault in the run, which cancels it, and every other pump
        // ends on that cancellation. The run is cancelled for no other reason than the caller's
        // token: no pump cancels on a clean exit, because one branch reaching EOS says nothing
        // about the others, so a pump that has nothing left to do drains its inputs instead of
        // tearing the graph down.
        //
        // So the pumps end Canceled either because the caller cancelled, and that cancellation
        // propagates unwrapped, or because one faulted, and the faults are what surface. A pump's
        // task alone cannot say which: a body that throws its own OperationCanceledException ends
        // its pump Canceled too (#489). The run can.
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.HasFaulted)
        {
            // The pumps stopped because one faulted; surfaced below.
        }

        // Wrapped so the exception says which node threw, and whether others did too (#499).
        if (run.Faults is { Count: > 0 } faults)
            throw new GraphFaultException(faults);
    }

    /// <summary>
    /// Runs <paramref name="node"/>'s pump, recording what the pump itself did not: a fault
    /// thrown before its loop, such as an input with no edge.
    /// </summary>
    /// <remarks>
    /// Faults end the returned task normally, since the run holds them. The run's cancellation
    /// ends it Canceled.
    /// </remarks>
    private static async Task PumpAsync(IPumpableNode node, GraphRun run)
    {
        try
        {
            await node.RunPumpAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex) when (!FaultRules.IsCancellation(ex, run.Token.IsCancellationRequested))
        {
            run.Fault(node, ex);
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
