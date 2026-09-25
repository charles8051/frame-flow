// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Periphery.Camera;

namespace FrameFlow.Camera.Internal;

/// <summary>
/// The camera source's guard (ADR-0081 decision 5, phase 1): the count of camera leases held
/// downstream, and the hand-off that copies a frame instead once the graph holds its limit.
/// </summary>
internal sealed partial class CameraLeaseGate
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly string _source;
    private CameraLeaseState _state;
    private long _framesCopied;
    private int _shortfallReported;

    public CameraLeaseGate(int limit, ILogger logger, string source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(source);
        _state = CameraLeaseState.Initial(limit);
        _logger = logger;
        _source = source;
    }

    /// <summary>The most leases the graph may hold.</summary>
    public int Limit
    {
        get
        {
            lock (_gate)
                return _state.Limit;
        }
    }

    /// <summary>Leases handed to the graph and not yet released.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
                return _state.Outstanding;
        }
    }

    /// <summary>Frames handed to the graph as copies.</summary>
    public long FramesCopied => Interlocked.Read(ref _framesCopied);

    /// <summary>
    /// Takes the graph's camera budget before a run (ADR-0081, decision 4). A budget larger than
    /// the session's <c>BufferCount</c> leaves the graph is logged once. Frames past the limit
    /// are copied by <see cref="HandOff"/> whatever the budget, so capture keeps its buffers
    /// either way.
    /// </summary>
    public void ApplyBudget(FrameBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        int limit = Limit;
        if (!CameraLeaseBudget.Exceeds(budget.Frames, limit))
            return;
        if (Interlocked.Exchange(ref _shortfallReported, 1) != 0)
            return;

        if (budget.Frames is { } frames)
            LogShortfall(_logger, _source, frames, limit, frames - limit);
        else
            LogUnbounded(_logger, _source, budget.UnboundedHolder!, limit);
    }

    /// <summary>
    /// Takes the caller's reference on <paramref name="frame"/> and returns what the graph gets:
    /// the lease itself while the graph holds fewer than the limit, otherwise a copy in CPU
    /// memory, with the lease released before this returns.
    /// </summary>
    public IVideoFrame HandOff(ICameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        CameraHandoff handoff;
        bool report;
        int limit;
        lock (_gate)
        {
            (_state, handoff, report) = CameraLeaseBudget.Next(_state);
            limit = _state.Limit;
        }

        if (handoff == CameraHandoff.Lease)
            return new CameraVideoFrame(frame, Released);

        Interlocked.Increment(ref _framesCopied);
        if (report)
            LogCopying(_logger, _source, limit);

        using var lease = new CameraVideoFrame(frame);
        return lease.CloneCpu();
    }

    private void Released()
    {
        lock (_gate)
            _state = CameraLeaseBudget.Released(_state);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Camera source {Source}: the graph holds {Limit} camera leases, its share of the "
            + "session's BufferCount, so frames past that are copied into CPU memory. Raise "
            + "CameraSessionOptions.BufferCount to keep them zero-copy."
    )]
    private static partial void LogCopying(ILogger logger, string source, int limit);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Camera source {Source}: the graph can hold {Frames} camera frames, more than the "
            + "{Limit} the session's BufferCount leaves it, so frames past {Limit} are copied into "
            + "CPU memory. Raise CameraSessionOptions.BufferCount by {Shortfall} to keep them all "
            + "zero-copy."
    )]
    private static partial void LogShortfall(
        ILogger logger,
        string source,
        int frames,
        int limit,
        int shortfall
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Camera source {Source}: '{Holder}' declares no bound on the frames it holds, so "
            + "frames past {Limit} are copied into CPU memory. Declare its Holding to size the "
            + "session for the graph."
    )]
    private static partial void LogUnbounded(ILogger logger, string source, string holder, int limit);
}
