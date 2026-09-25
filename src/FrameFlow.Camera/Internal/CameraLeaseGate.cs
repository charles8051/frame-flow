// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
}
