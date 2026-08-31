// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Playback;

/// <summary>
/// A 1→1 substrate operator that pauses item flow without cancelling
/// upstream. When the gate is open, items pass through synchronously;
/// when closed, the operator awaits the gate before forwarding,
/// causing the downstream sink pump to idle and the upstream source
/// to backpressure naturally (the bounded edge buffer fills, then the
/// upstream's <see cref="Crossbar.SourceNode{T}"/>
/// body call blocks on the channel write).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The first cut of
/// <see cref="SubstrateSession"/> implemented pause/resume by
/// cancelling the entire graph CTS and rebuilding the graph on
/// resume. That works for in-memory test stubs but native-faults on
/// real FFmpeg decoders — the codec context is left in an unstable
/// state by cancel-mid-decode and the next decode call faults the
/// host. The pausable gate keeps the decoder running (and the demux
/// pump feeding it) but parks the data on the sink-side of the
/// gate; nothing gets cancelled mid-decode.
/// </para>
/// <para>
/// <b>Why not put this in <c>Crossbar</c>.</b> The
/// gate is fully generic over <c>T : class, IRefCounted</c> and would
/// fit in the substrate. It lives here for now because (a) it's
/// only used by the playback controller and (b) keeping playback-
/// specific shapes out of the substrate matches the Phase-0 minimum-
/// primitive-set philosophy until a second consumer shows up.
/// Promotable to the substrate later without a public-surface change.
/// </para>
/// <para>
/// <b>Reusing the existing event.</b> Wraps the existing
/// <c>FrameFlow.Playback.AsyncManualResetEvent</c> (accessible via
/// the <c>InternalsVisibleTo</c> declared in
/// <c>FrameFlow.Playback/AssemblyInfo.cs</c>). No need to duplicate
/// the gate primitive.
/// </para>
/// </remarks>
public sealed class PausableGate<T>
    where T : class, IRefCounted
{
    private readonly AsyncManualResetEvent _gate;

    /// <summary>
    /// Creates a gate in the given initial state.
    /// </summary>
    /// <param name="initiallyOpen">
    /// When <see langword="true"/>, items pass through immediately
    /// until <see cref="Close"/> is called. When <see langword="false"/>,
    /// items block at the gate until <see cref="Open"/> is called.
    /// </param>
    public PausableGate(bool initiallyOpen = true)
    {
        _gate = new AsyncManualResetEvent(initiallyOpen);
    }

    /// <summary>Whether the gate is currently open (items pass through).</summary>
    public bool IsOpen => _gate.IsSet;

    /// <summary>Opens the gate. Any items blocked at the gate proceed.</summary>
    public void Open() => _gate.Set();

    /// <summary>
    /// Closes the gate. Subsequent items block at the gate until
    /// <see cref="Open"/> is called.
    /// </summary>
    public void Close() => _gate.Reset();

    /// <summary>
    /// Builds the substrate operator node that gates items through
    /// this gate. Use one node per stream (i.e. one gate instance per
    /// video / audio stream pair); a single gate instance can be the
    /// source of multiple operator nodes for fan-out patterns.
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    public OperatorNode<T, T> AsOperator(string id) =>
        new(
            id,
            async (item, ct) =>
            {
                // Wait until the gate opens (or cancellation aborts the
                // wait). The substrate ensures the item is disposed by
                // the standard 1→1 ownership protocol — we don't touch
                // refcounts here; the item is just held until the gate
                // releases.
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                return item;
            }
        );
}
