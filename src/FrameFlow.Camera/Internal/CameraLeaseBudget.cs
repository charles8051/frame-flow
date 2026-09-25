// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Camera.Internal;

/// <summary>What the camera source hands the graph for one frame.</summary>
internal enum CameraHandoff
{
    /// <summary>The camera's lease itself, zero-copy.</summary>
    Lease,

    /// <summary>A copy in CPU memory; the lease goes back to the pool at once.</summary>
    Copy,
}

/// <summary>
/// The camera source's count: leases it has handed to the graph that are still held, the most
/// the graph may hold, and whether copying has been reported.
/// </summary>
/// <param name="Outstanding">Leases handed to the graph and not yet released.</param>
/// <param name="Limit">The most leases the graph may hold.</param>
/// <param name="CopyReported">Whether the first copy has been logged.</param>
internal readonly record struct CameraLeaseState(int Outstanding, int Limit, bool CopyReported)
{
    /// <summary>A source that has handed nothing out.</summary>
    public static CameraLeaseState Initial(int limit) => new(0, limit, false);
}

/// <summary>
/// The camera source's guard policy (ADR-0081 decision 5, phase 1), as total functions over
/// <see cref="CameraLeaseState"/>. <see cref="CameraLeaseGate"/> is its shell.
/// </summary>
/// <remarks>
/// A camera session's pool is <c>BufferCount + QueueDepth + 1</c> buffers, and a consumer that
/// holds more than <c>BufferCount</c> leases starves capture, because an active lease is never
/// revoked. So past its limit the source copies the frame into CPU memory and returns the lease:
/// one copy instead of a lost frame.
/// </remarks>
internal static class CameraLeaseBudget
{
    /// <summary>
    /// The leases the graph may hold: the session's <c>BufferCount</c> less the frames the
    /// source's own bridge holds on the way to the graph. Zero when the bridge takes them all,
    /// and then every frame is copied.
    /// </summary>
    /// <remarks>
    /// The pump's frame is briefly one more lease while it replaces the bridge's. It has just
    /// left the session's queue, so the pool's spare still covers the next capture.
    /// </remarks>
    public static int LimitFor(int bufferCount, int bridgeCapacity) =>
        Math.Max(0, bufferCount - bridgeCapacity);

    /// <summary>
    /// Whether the graph's camera budget exceeds the limit: the graph can hold more camera frames
    /// than the session's <c>BufferCount</c> leaves it, or holds without bound (ADR-0081, decision
    /// 4). Frames past the limit are then copied, one at a time, by <see cref="Next"/>.
    /// </summary>
    public static bool Exceeds(int? budgetFrames, int limit) =>
        budgetFrames is not { } frames || frames > limit;

    /// <summary>
    /// Hands the next frame over: as its lease while the graph holds fewer than the limit,
    /// otherwise as a copy. The first copy is reported.
    /// </summary>
    public static (CameraLeaseState State, CameraHandoff Handoff, bool ReportCopy) Next(
        CameraLeaseState state
    ) =>
        state.Outstanding < state.Limit
            ? (state with { Outstanding = state.Outstanding + 1 }, CameraHandoff.Lease, false)
            : (state with { CopyReported = true }, CameraHandoff.Copy, !state.CopyReported);

    /// <summary>The graph released a lease it was handed.</summary>
    public static CameraLeaseState Released(CameraLeaseState state) =>
        state with { Outstanding = Math.Max(0, state.Outstanding - 1) };
}
