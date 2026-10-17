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
/// A device the GPU lost (<see cref="HardwareDevice.IsLost"/>) is never lent again: the next
/// borrow for its backend creates a new one, and a fault on an item that decoded on it evicts it
/// sooner (<see cref="EvictIfLost"/>). Any other fault, a corrupt packet or a sink's for instance,
/// leaves the device cached, so the next item does not pay for a new one (#759). Decoders hold
/// their own references, so evicting or disposing the cache never pulls a device from under a
/// decoder that is using it.
/// </para>
/// </remarks>
internal sealed partial class HardwareDeviceCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<HardwareDecodeBackendKind, HardwareDevice> _devices = [];
    private readonly Func<HardwareDecodeBackendKind, HardwareDevice> _create;
    private readonly ILogger _logger;
    private bool _disposed;

    public HardwareDeviceCache(ILogger? logger = null)
        : this(logger, create: null) { }

    /// <param name="logger">Where the cache and the devices it creates log.</param>
    /// <param name="create">
    /// Creates the device for a backend. Null has FFmpeg create it. Internal: tests create devices
    /// that need no GPU.
    /// </param>
    internal HardwareDeviceCache(ILogger? logger, Func<HardwareDecodeBackendKind, HardwareDevice>? create)
    {
        _logger = logger ?? NullLogger.Instance;
        _create = create ?? (backend => HardwareDevice.Create(backend, logger: _logger));
    }

    /// <summary>
    /// A new reference to the device for <paramref name="backend"/>, creating it on first use and
    /// in place of one the GPU lost, for a decoder to own and release. Zero when the cache is
    /// disposed or the device cannot be created, and the decoder then creates its own, as it would
    /// without a cache.
    /// </summary>
    /// <remarks>Taken under the cache's lock, so a concurrent eviction or dispose cannot race it.</remarks>
    internal nint Borrow(HardwareDecodeBackendKind backend) => Borrow(backend, out _);

    /// <inheritdoc cref="Borrow(HardwareDecodeBackendKind)"/>
    /// <param name="backend">The backend.</param>
    /// <param name="owner">
    /// The cached device the reference was taken from, for the decoder to report a device loss to
    /// (#759) and for <see cref="EvictIfLost"/>. Null when the result is zero.
    /// </param>
    internal nint Borrow(HardwareDecodeBackendKind backend, out HardwareDevice? owner)
    {
        HardwareDevice? lost = null;
        try
        {
            lock (_gate)
            {
                owner = null;
                if (_disposed)
                    return nint.Zero;

                // A lost device stays lost: a decoder lent it would only fail on it.
                if (_devices.TryGetValue(backend, out var device) && device.IsLost)
                {
                    _devices.Remove(backend);
                    lost = device;
                    device = null;
                }

                if (device is null)
                {
                    try
                    {
                        device = _create(backend);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                    {
                        LogCreateFailed(_logger, backend.ToString(), ex.Message);
                        return nint.Zero;
                    }
                    _devices[backend] = device;
                    LogCreated(_logger, backend.ToString());
                }

                owner = device;
                return device.Borrow();
            }
        }
        finally
        {
            // Released outside the lock, as an eviction and the dispose release theirs.
            if (lost is not null)
                Release(lost);
        }
    }

    /// <summary>
    /// Evicts <paramref name="device"/> if the GPU lost it and it is still the device the cache
    /// lends for its backend, so the next decoder to bind that backend gets a new one. Decoders
    /// already using it keep their references.
    /// </summary>
    /// <remarks>
    /// By identity, not by backend: a fault reported late, by an item that decoded on a device the
    /// cache has since replaced, leaves the replacement alone. A live device stays: a corrupt
    /// packet or a sink's fault says nothing about the device, and a new one costs the next item
    /// what the cache exists to save. A device the cache never lent, one lent to the player
    /// instead, is not the cache's to evict.
    /// </remarks>
    public void EvictIfLost(HardwareDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (!device.IsLost
                || !_devices.TryGetValue(device.Backend, out var cached)
                || !ReferenceEquals(cached, device))
            {
                return;
            }
            _devices.Remove(device.Backend);
        }
        Release(device);
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

    private void Release(HardwareDevice lost)
    {
        lost.Dispose();
        LogDroppedLost(_logger, lost.Backend.ToString(), lost.LostReason);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Created a {Backend} device that later items will share.")]
    private static partial void LogCreated(ILogger logger, string backend);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not create a shared {Backend} device: {Reason}. The decoder creates its own.")]
    private static partial void LogCreateFailed(ILogger logger, string backend, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped the shared {Backend} device, which the GPU lost (0x{RemovedReason:X8}). A new device replaces it.")]
    private static partial void LogDroppedLost(ILogger logger, string backend, int removedReason);
}
