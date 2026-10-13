// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// The hardware decode devices a player keeps across its items: one per backend, created the
/// first time a decoder binds that backend and lent to every decoder after it.
/// </summary>
/// <remarks>
/// <para>
/// Without it each decoder creates its own device, and a player opens a decoder per item, so
/// every item boundary created a device. That was most of the cost of a switch: about 220 ms of
/// a 250 ms switch measured on a discrete GPU, against 25 ms with one device lent through
/// <see cref="VideoDecoderOptions.Device"/>. Unlike a lent device, the cache does not narrow the
/// decoder to one backend: the decoder chooses as it always does, and borrows the cached device
/// for the backend it tries.
/// </para>
/// <para>
/// A device is dropped when an item that decoded on it fails (<see cref="Drop"/>), so one left
/// unusable, by a driver reset for instance, is replaced by the next item rather than failing
/// every item after it. Decoders hold their own references, so dropping or disposing the cache
/// never pulls a device from under a decoder that is using it.
/// </para>
/// </remarks>
internal sealed partial class HardwareDeviceCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<HardwareDecodeBackendKind, HardwareDevice> _devices = [];
    private readonly ILogger _logger;
    private bool _disposed;

    public HardwareDeviceCache(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// A new reference to the device for <paramref name="backend"/>, creating it on first use, for
    /// a decoder to own and release. Zero when the cache is disposed or the device cannot be
    /// created, and the decoder then creates its own, as it would without a cache.
    /// </summary>
    /// <remarks>Taken under the cache's lock, so a concurrent drop or dispose cannot race it.</remarks>
    internal nint Borrow(HardwareDecodeBackendKind backend)
    {
        lock (_gate)
        {
            if (_disposed)
                return nint.Zero;

            if (!_devices.TryGetValue(backend, out var device))
            {
                try
                {
                    device = HardwareDevice.Create(backend);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    LogCreateFailed(_logger, backend.ToString(), ex.Message);
                    return nint.Zero;
                }
                _devices[backend] = device;
                LogCreated(_logger, backend.ToString());
            }

            return device.Borrow();
        }
    }

    /// <summary>
    /// Drops the device for <paramref name="backend"/>, so the next decoder to bind it gets a new
    /// one. Decoders already using it keep their references.
    /// </summary>
    public void Drop(HardwareDecodeBackendKind backend)
    {
        HardwareDevice? dropped;
        lock (_gate)
        {
            if (!_devices.Remove(backend, out dropped))
                return;
        }
        dropped.Dispose();
        LogDropped(_logger, backend.ToString());
    }

    public void Dispose()
    {
        HardwareDevice[] devices;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            devices = [.. _devices.Values];
            _devices.Clear();
        }
        foreach (var device in devices)
            device.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Created a {Backend} device that later items will share.")]
    private static partial void LogCreated(ILogger logger, string backend);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not create a shared {Backend} device: {Reason}. The decoder creates its own.")]
    private static partial void LogCreateFailed(ILogger logger, string backend, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped the shared {Backend} device after an item that used it failed. The next item creates a new one.")]
    private static partial void LogDropped(ILogger logger, string backend);
}
