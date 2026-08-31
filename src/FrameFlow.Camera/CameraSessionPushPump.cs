// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// Drains a <see cref="CameraSession"/>'s capture stream and re-publishes each
/// frame via push to a <see cref="CameraFramePushBridge"/>, which exposes them
/// to a graph as a <c>SourceNode&lt;VideoFrameRef&gt;</c>. Internal plumbing
/// behind <see cref="CameraSessionSourceExtensions.AsPushVideoFrameSource"/>;
/// real consumers (e.g. a router whose subscriber callback hands borrowed
/// frames to <see cref="CameraFramePushBridge.Push"/>) drive the bridge
/// directly and don't need this pump.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> <see cref="CameraSession.CaptureAsync"/> yields owned
/// frames (one ref per item). <see cref="CameraFramePushBridge.Push"/> AddRefs
/// internally — it does not consume the pump's ref — so the pump disposes the
/// original ref in its <c>finally</c>; the bridge holds the AddRef'd copy the
/// graph consumes. Net residual refcount per pumped frame: zero.
/// </para>
/// <para>
/// <b>EOS signalling.</b> When the pump exits (cancellation, capture ending, or
/// fault) it disposes the bridge, completing the channel so the graph's source
/// node observes end-of-stream and <c>graph.RunAsync</c> can return even if the
/// outer token hasn't fired.
/// </para>
/// </remarks>
internal sealed class CameraSessionPushPump
{
    private readonly CameraSession _session;
    private readonly CameraFramePushBridge _bridge;
    private readonly ILogger _logger;

    public CameraSessionPushPump(
        CameraSession session,
        CameraFramePushBridge bridge,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(logger);
        _session = session;
        _bridge = bridge;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _session.CaptureAsync(ct: ct).WithCancellation(ct))
            {
                try
                {
                    _bridge.Push(frame);
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown — caller cancels ct; CaptureAsync's
            // enumerator surfaces it. Don't escalate.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera push pump faulted.");
            // Propagate the fault THROUGH the source channel so the consuming
            // graph throws out of RunAsync — without this, the finally's
            // Dispose() completes the channel cleanly and the graph drains
            // silently, leaving the host's reconnect loop unaware of the
            // disconnect. See CameraFramePushBridge.Fault.
            _bridge.Fault(ex);
            throw;
        }
        finally
        {
            _bridge.Dispose();
        }
    }
}
