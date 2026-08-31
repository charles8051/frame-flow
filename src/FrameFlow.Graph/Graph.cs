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

    // Per-edge reset actions, run at the top of every RunAsync BEFORE the
    // wire-ups. They clear the prior run's edge state (output-port writers +
    // input-port reader) so a graph instance is re-runnable: without this, a
    // second RunAsync would APPEND a second writer to each output port (and a
    // source would then fan a frame into a now-orphaned channel), corrupting the
    // topology. Re-running a finished graph is how RepeatMode.One loops cheaply —
    // see SubstrateSession.RewindToStartAsync.
    private readonly List<Action> _resets = new();

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
    /// the graph if not already present. This overload uses the
    /// substrate's default fan-out semantics — siblings get a fresh
    /// ref via <c>AddRef</c>. For one-shot frame types whose
    /// <c>AddRef</c> throws, use the
    /// <see cref="Connect{T}(OutputPort{T}, InputPort{T}, EdgeConfig{T})"/>
    /// overload with an explicit cloner (ADR-0054).
    /// </summary>
    public Graph Connect<T>(
        OutputPort<T> from,
        InputPort<T> to,
        EdgeOptions? options = null
    )
        where T : class, IRefCounted =>
        Connect(from, to, new EdgeConfig<T>(options ?? EdgeOptions.Default, Cloner: null));

    /// <summary>
    /// Connects an output port to an input port using a typed
    /// <see cref="EdgeConfig{T}"/> that may carry a per-branch cloner
    /// (per ADR-0054). When the config's cloner is non-<see langword="null"/>,
    /// fan-out invokes the cloner instead of <c>AddRef</c> for this
    /// specific branch — required for one-shot frame types (e.g.
    /// converter outputs) and useful when a branch wants an
    /// independent deep copy regardless. Sibling branches without a
    /// cloner continue to use <c>AddRef</c>.
    /// </summary>
    public Graph Connect<T>(
        OutputPort<T> from,
        InputPort<T> to,
        EdgeConfig<T> config
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

        var opts = config.Options ?? EdgeOptions.Default;
        var cloner = config.Cloner;
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
            from.Writers.Add(new OutputEdge<T>(channel.Writer, cloner));
            to.Reader = channel.Reader;
        });
        return this;
    }

    /// <summary>
    /// Runs the graph to completion. Returns when every node's pump
    /// loop has terminated (EOS propagated, cancellation requested,
    /// or a pump failed).
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // Reset any edge state left over from a previous run, then (re)wire fresh
        // channels. Resets run before wire-ups so a fan-out output port's writers
        // are cleared once and rebuilt, never accumulated across runs. On the first
        // run the resets act on empty ports (no-op). This is what makes a graph
        // instance re-runnable for the cheap RepeatMode.One loop rewind.
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
        // finally cancels graphCts if it exits via exception (and the
        // join pump cancels even on clean exit, to stop upstream
        // sources from producing into a dead consumer). Sibling pumps
        // observe cancellation and exit cleanly.
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
