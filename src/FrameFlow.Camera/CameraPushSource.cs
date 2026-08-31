// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// A live camera capture running as a graph source. A background pump drains
/// <see cref="CameraSession.CaptureAsync"/> into a bounded
/// <see cref="CameraFramePushBridge"/> (capacity-1 <c>DropOldest</c> by default
/// = LatestOnly, so the camera is never blocked by a slow graph), exposed as a
/// <c>SourceNode&lt;VideoFrameRef&gt;</c> via <see cref="Source"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>push</b>-style counterpart to
/// <see cref="CameraSourceAdapters.AsVideoFrameSourceNode{T}"/> (pull). Push
/// decouples the camera's cadence from the graph's: stale frames are dropped at
/// the bridge rather than backpressuring capture. Because it owns a background
/// pump it is <see cref="IAsyncDisposable"/> — dispose it after the graph
/// completes to await pump teardown.
/// </para>
/// <para>
/// It does <b>not</b> own the <see cref="CameraSession"/>: whoever opened the
/// session (directly, or via a device-session host) remains responsible for
/// disposing it. Cancelling the token supplied at creation tears down the pump,
/// which disposes the bridge and EOS-signals the source node so
/// <c>graph.RunAsync</c> returns.
/// </para>
/// </remarks>
public sealed class CameraPushSource : IAsyncDisposable
{
    private readonly Task _pumpTask;

    private CameraPushSource(Task pumpTask, SourceNode<VideoFrameRef> source)
    {
        _pumpTask = pumpTask;
        Source = source;
    }

    /// <summary>The graph source node fed by the camera capture pump.</summary>
    public SourceNode<VideoFrameRef> Source { get; }

    internal static CameraPushSource Start(
        CameraSession session,
        CancellationToken ct,
        ILoggerFactory? loggerFactory,
        int capacity,
        string id
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        ILoggerFactory lf = loggerFactory ?? NullLoggerFactory.Instance;

        // Capacity-1 DropOldest = LatestOnly: a slow graph never blocks the
        // camera and never sees stale frames.
        var bridge = new CameraFramePushBridge(
            capacity,
            BoundedChannelFullMode.DropOldest
        );
        var pump = new CameraSessionPushPump(
            session,
            bridge,
            lf.CreateLogger<CameraSessionPushPump>()
        );
        Task pumpTask = Task.Run(() => pump.RunAsync(ct), ct);
        SourceNode<VideoFrameRef> source = bridge.AsVideoFrameSourceNode(id);
        return new CameraPushSource(pumpTask, source);
    }

    /// <summary>
    /// Awaits the capture pump (already signalled to stop via the token supplied
    /// at creation). Does not dispose the session. The pump disposes the bridge.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch
        {
            // The pump logs its own faults; teardown must not throw.
        }
    }
}

/// <summary>
/// Extensions that expose a <see cref="CameraSession"/> as a push-driven graph
/// source.
/// </summary>
public static class CameraSessionSourceExtensions
{
    /// <summary>
    /// Starts a background pump that streams this session's frames into a graph
    /// as a <c>SourceNode&lt;VideoFrameRef&gt;</c> with LatestOnly semantics
    /// (capacity-<paramref name="capacity"/> <c>DropOldest</c>). The returned
    /// <see cref="CameraPushSource"/> owns the pump; dispose it after
    /// <c>graph.RunAsync</c> completes. Cancelling <paramref name="ct"/> tears
    /// down the pump and EOS-signals the graph. The session's lifetime remains
    /// the caller's responsibility.
    /// </summary>
    public static CameraPushSource AsPushVideoFrameSource(
        this CameraSession session,
        CancellationToken ct,
        ILoggerFactory? loggerFactory = null,
        int capacity = 1,
        string id = "camera-source"
    ) => CameraPushSource.Start(session, ct, loggerFactory, capacity, id);
}
