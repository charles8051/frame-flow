// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;
using FrameFlow.Graph;
using FrameFlow.Media;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// Push-to-pull bridge for camera frames. Consumers receiving frames via
/// callback (e.g. a router's <c>Action&lt;ICameraFrame&gt;</c> subscriber
/// surface) hand them to <see cref="Push"/>; the bridge takes a fresh
/// ref, enqueues the frame in a bounded channel, and exposes the channel
/// reader as a <see cref="SourceNode{TOut}"/> for a graph to consume.
/// Complements <see cref="CameraSourceAdapters"/>, which covers the
/// pull-style case (an <see cref="IAsyncEnumerable{T}"/> source).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> <see cref="Push"/> calls
/// <see cref="ICameraFrame.AddRef"/> on the borrowed frame before
/// enqueue. Disposal of the AddRef'd ref is split by overflow policy:
/// </para>
/// <list type="bullet">
///   <item>
///     Under <see cref="BoundedChannelFullMode.DropOldest"/> /
///     <see cref="BoundedChannelFullMode.DropNewest"/> /
///     <see cref="BoundedChannelFullMode.DropWrite"/>, the channel's
///     <c>itemDropped</c> callback disposes the evicted frame
///     (returning the buffer to Periphery's pool).
///   </item>
///   <item>
///     Under <see cref="BoundedChannelFullMode.Wait"/>, a full channel
///     causes <see cref="Push"/> to return <c>false</c> and dispose the
///     AddRef'd copy itself.
///   </item>
/// </list>
/// <para>
/// Either way, every AddRef'd frame is matched by a Dispose somewhere —
/// either downstream of the source node (the normal path) or at the
/// boundary (the overflow path).
/// </para>
/// <para>
/// <b>Single source.</b> Each bridge backs at most one source node.
/// Calling <see cref="AsSourceNode"/> or
/// <see cref="AsVideoFrameSourceNode"/> a second time throws — two
/// source nodes sharing the same reader would race on items.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="Dispose"/> completes the channel writer
/// and drains any remaining queued frames (disposing each). A source
/// node already running observes EOS via the standard channel-completion
/// signal; the graph terminates naturally.
/// </para>
/// </remarks>
public sealed class CameraFramePushBridge : IDisposable
{
    private readonly Channel<ICameraFrame> _channel;
    private int _sourceClaimed;
    private int _disposed;

    /// <summary>Constructs a push bridge.</summary>
    /// <param name="capacity">
    /// Bounded channel capacity. Defaults to <c>1</c> — combined with
    /// the default <see cref="BoundedChannelFullMode.DropOldest"/>, this
    /// gives <c>LatestOnly</c> semantics: a slow graph never sees stale
    /// frames, and pushers never block.
    /// </param>
    /// <param name="overflowMode">
    /// Behaviour when <see cref="Push"/> is called on a full channel.
    /// Defaults to <see cref="BoundedChannelFullMode.DropOldest"/>.
    /// </param>
    public CameraFramePushBridge(
        int capacity = 1,
        BoundedChannelFullMode overflowMode = BoundedChannelFullMode.DropOldest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<ICameraFrame>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = overflowMode,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: static frame => frame.Dispose());
    }

    /// <summary>
    /// Hands a borrowed camera frame to the bridge. The bridge takes a
    /// fresh ref via <see cref="ICameraFrame.AddRef"/>; the caller's
    /// borrowed reference is unaffected and can be released per the
    /// caller's contract (typically: returned to the router after the
    /// subscriber callback returns).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the frame was enqueued (possibly displacing an
    /// older frame under drop policies); <c>false</c> if a
    /// <see cref="BoundedChannelFullMode.Wait"/> channel rejected the
    /// write (the bridge has already disposed the AddRef'd copy in
    /// that case) or the bridge has been disposed.
    /// </returns>
    public bool Push(ICameraFrame borrowed)
    {
        ArgumentNullException.ThrowIfNull(borrowed);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var retained = borrowed.AddRef();

        if (_channel.Writer.TryWrite(retained))
        {
            return true;
        }

        // Wait-mode rejection, or the channel was completed between our
        // disposed check and TryWrite. Release the AddRef'd copy.
        retained.Dispose();
        return false;
    }

    /// <summary>
    /// Exposes the bridge's frames as a
    /// <see cref="SourceNode{CameraFrameAdapter}"/>. Use this when
    /// downstream operators consume <see cref="CameraFrameAdapter"/>
    /// directly (camera-specific surface visible).
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    /// <exception cref="InvalidOperationException">
    /// A source node has already been created from this bridge.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The bridge has been disposed.
    /// </exception>
    public SourceNode<CameraFrameAdapter> AsSourceNode(string id = "camera-push-source")
    {
        ClaimSource();
        return _channel.Reader.ReadAllAsync().AsSourceNode(id);
    }

    /// <summary>
    /// Exposes the bridge's frames as a
    /// <see cref="SourceNode{VideoFrameRef}"/>, matching the shape of
    /// FrameFlow's decoded-video pipelines. Use this when downstream
    /// operators expect <see cref="VideoFrameRef"/> (the standard shape
    /// for <c>VideoOperators</c>, YOLO operators, video sinks).
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    /// <exception cref="InvalidOperationException">
    /// A source node has already been created from this bridge.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The bridge has been disposed.
    /// </exception>
    public SourceNode<VideoFrameRef> AsVideoFrameSourceNode(string id = "camera-push-source")
    {
        ClaimSource();
        return _channel.Reader.ReadAllAsync().AsVideoFrameSourceNode(id);
    }

    /// <summary>
    /// Marks the channel as faulted with the supplied exception. A source node
    /// reader awaiting frames sees the exception (wrapped by the channel), so
    /// the graph throws out of <c>RunAsync</c> and the host's reconnect logic
    /// can react. Use this — rather than <see cref="Dispose"/> alone — when a
    /// pump or upstream surface fails: <c>Dispose</c> alone completes the
    /// writer normally, which the graph treats as a clean EOS (the failure is
    /// silently lost). Idempotent: subsequent calls (and a later
    /// <see cref="Dispose"/>) are no-ops on an already-faulted channel.
    /// </summary>
    /// <param name="error">The exception to surface to readers.</param>
    public void Fault(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _channel.Writer.TryComplete(error);
    }

    /// <summary>
    /// Completes the writer and drains any frames still in the channel
    /// (disposing each). A source node already running observes EOS via
    /// the standard channel-completion signal and terminates its pump.
    /// Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        // Drain anything left so AddRef'd frames don't leak when no
        // source node ever consumed them. TryRead is thread-safe alongside
        // a running source-node consumer; whichever path observes the
        // item disposes it exactly once.
        while (_channel.Reader.TryRead(out var leftover))
        {
            leftover.Dispose();
        }
    }

    private void ClaimSource()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(CameraFramePushBridge));
        }
        if (Interlocked.Exchange(ref _sourceClaimed, 1) != 0)
        {
            throw new InvalidOperationException(
                "A source node has already been created from this bridge. "
                + "Each bridge backs at most one source node.");
        }
    }
}
