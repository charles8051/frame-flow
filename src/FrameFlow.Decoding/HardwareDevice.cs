// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;
using FrameFlow.Media.Core;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// A hardware decode device that decoders borrow instead of each creating their own, so it
/// outlives them: a player's playlist items, a pass, and an inference stage built on the same
/// device all share one.
/// </summary>
/// <remarks>
/// <para>
/// Each decoder that borrows the device holds a reference to it, so disposing this object while a
/// decoder or one of its frames is alive leaves the device alive until they are released too.
/// Dispose it after them, and after anything built on <see cref="TryGetD3D12Device"/>.
/// </para>
/// <para>
/// A decoder given a device tries only its backend. A codec with no hardware configuration for
/// that backend falls back to software under <see cref="HardwareDecodeMode.Auto"/> and fails
/// under <see cref="HardwareDecodeMode.Required"/>, as it does without one.
/// </para>
/// <para>
/// When the GPU removes or resets the device, it raises <see cref="DeviceLost"/> once (#759). A
/// lost device stays lost, so every player decoding on it fails with
/// <see cref="ErrorCategory.DeviceLost"/>, and the way back is a new device. A decoder opened on
/// it once it is lost fails with <see cref="DeviceLostException"/> before it binds, under
/// <see cref="HardwareDecodeMode.Auto"/> as under <see cref="HardwareDecodeMode.Required"/>,
/// rather than decoding in software on a player its owner is about to rebuild.
/// </para>
/// </remarks>
public sealed partial class HardwareDevice : IDisposable
{
    private readonly Lock _lossGate = new();
    private readonly ILogger _logger;
    private nint _deviceCtxRef;
    private DeviceLossLatch _loss = DeviceLossLatch.Live;

    private HardwareDevice(nint deviceCtxRef, HardwareDecodeBackendKind backend, int avHwDeviceType, ILogger? logger)
    {
        _logger = logger ?? NullLogger.Instance;
        _deviceCtxRef = deviceCtxRef;
        Backend = backend;
        AvHwDeviceType = avHwDeviceType;
    }

    /// <summary>The backend the device decodes with.</summary>
    public HardwareDecodeBackendKind Backend { get; }

    /// <summary>
    /// Raised once, on the thread pool, when a decoder on this device finds it removed or reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every player decoding on the device fails with <see cref="ErrorCategory.DeviceLost"/> as
    /// well, each on its own, so a caller with several players rebuilds them all from this one
    /// event rather than from each failure. To recover, dispose this device and the players on it,
    /// create another device, and build the players again on it.
    /// </para>
    /// <para>
    /// It is raised on the thread pool, not on the decoder's thread, so a handler may dispose the
    /// players on the device. A handler that subscribes after the loss is not called: read
    /// <see cref="IsLost"/>.
    /// </para>
    /// <para>
    /// Each handler runs on its own. One that throws is logged to the logger the device was
    /// created with, and the handlers after it still run. Nothing is rethrown: there is no caller
    /// on the thread pool to receive it.
    /// </para>
    /// <para>
    /// Only D3D11VA and D3D12VA devices report removal. The event is never raised for another
    /// backend.
    /// </para>
    /// </remarks>
    public event EventHandler<HardwareDeviceLostEventArgs>? DeviceLost;

    /// <summary>Whether <see cref="DeviceLost"/> has been raised for this device.</summary>
    public bool IsLost
    {
        get
        {
            lock (_lossGate)
                return _loss.Lost;
        }
    }

    /// <summary>
    /// The reason the first loss report gave, which <see cref="DeviceLost"/> carried, or 0 while
    /// the device is live. A lost device keeps it, so it can be read after <see cref="IsLost"/>.
    /// </summary>
    internal int LostReason
    {
        get
        {
            lock (_lossGate)
                return _loss.Reason;
        }
    }

    /// <summary>
    /// Records that a decoder on this device read <paramref name="removedReason"/> from it, and
    /// raises <see cref="DeviceLost"/> if this is the first report of a loss.
    /// </summary>
    /// <returns>Whether this report raised the event.</returns>
    internal bool ReportRemovedReason(int removedReason)
    {
        bool raise;
        lock (_lossGate)
            (_loss, raise) = _loss.Observe(removedReason);

        if (raise)
        {
            var args = new HardwareDeviceLostEventArgs(removedReason);
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Device.RaiseDeviceLost(state.Args),
                (Device: this, Args: args),
                preferLocal: false
            );
        }
        return raise;
    }

    // One subscriber at a time: a chain invocation abandons every handler after the one that
    // threw, and an exception escaping a thread-pool callback ends the process.
    private void RaiseDeviceLost(HardwareDeviceLostEventArgs args)
    {
        if (DeviceLost is not { } handler)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<HardwareDeviceLostEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                try
                {
                    LogDeviceLostHandlerFailed(_logger, ex, Backend.ToString());
                }
                catch (Exception)
                {
                    // A logger that throws here would escape the thread-pool callback too. The
                    // handler's failure goes unrecorded rather than ending the process.
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A DeviceLost handler for the {Backend} device threw; the handlers after it still run.")]
    private static partial void LogDeviceLostHandlerFailed(ILogger logger, Exception exception, string backend);

    internal int AvHwDeviceType { get; }

    /// <summary>The <c>AVHWDeviceContext*</c> this device is, which every frame decoded on it reports.</summary>
    internal unsafe nint ContextPointer
    {
        get
        {
            nint deviceCtxRef = Volatile.Read(ref _deviceCtxRef);
            return deviceCtxRef == nint.Zero ? 0 : (nint)((AVBufferRef*)deviceCtxRef)->data;
        }
    }

    /// <summary>
    /// A device for <paramref name="backend"/> that FFmpeg creates and FrameFlow holds.
    /// </summary>
    /// <param name="backend">The backend.</param>
    /// <param name="device">
    /// Which device, in the backend's own terms, as FFmpeg's <c>av_hwdevice_ctx_create</c> takes
    /// it: an adapter index for D3D11VA and D3D12VA, a DRM node such as
    /// <c>/dev/dri/renderD128</c> for VAAPI, a device index for CUDA. Null for the default.
    /// </param>
    /// <param name="logger">
    /// Where a <see cref="DeviceLost"/> handler that throws is reported. Null for none; the
    /// failure is still contained.
    /// </param>
    /// <exception cref="NotSupportedException"><paramref name="backend"/> is not an FFmpeg device type.</exception>
    /// <exception cref="InvalidOperationException">FFmpeg could not create the device.</exception>
    public static HardwareDevice Create(HardwareDecodeBackendKind backend, string? device = null, ILogger? logger = null)
    {
        int type = HardwareDecodeProbeBridge.AvDeviceTypeOf(backend)
            ?? throw new NotSupportedException($"{backend} is not a hardware device FFmpeg can create.");

        int rc = FFAvUtil.av_hwdevice_ctx_create(out nint deviceCtxRef, type, device, nint.Zero, 0);
        if (rc < 0 || deviceCtxRef == nint.Zero)
        {
            throw new InvalidOperationException(
                $"FFmpeg could not create a {backend} device{(device is null ? "" : $" '{device}'")}: "
                    + $"av_hwdevice_ctx_create returned {rc}.");
        }

        return new HardwareDevice(deviceCtxRef, backend, type, logger);
    }

    /// <summary>
    /// A device context for <paramref name="backend"/> that FFmpeg allocated and nothing opened.
    /// It touches no hardware and cannot decode: it is for testing what holds and lends devices,
    /// on a machine with no GPU.
    /// </summary>
    /// <exception cref="NotSupportedException">This FFmpeg build has no device type for <paramref name="backend"/>.</exception>
    internal static HardwareDevice Unopened(HardwareDecodeBackendKind backend)
    {
        int type = HardwareDecodeProbeBridge.AvDeviceTypeOf(backend)
            ?? throw new NotSupportedException($"{backend} is not a hardware device FFmpeg can create.");

        nint deviceCtxRef = FFAvUtil.av_hwdevice_ctx_alloc(type);
        return deviceCtxRef != nint.Zero
            ? new HardwareDevice(deviceCtxRef, backend, type, logger: null)
            : throw new NotSupportedException($"This FFmpeg build has no {backend} device type.");
    }

    /// <summary>
    /// A D3D12VA device on <paramref name="d3d12Device"/>, which the caller created. FFmpeg takes
    /// its own reference and releases it when the last decoder and this object are done.
    /// </summary>
    /// <param name="d3d12Device">An <c>ID3D12Device*</c>.</param>
    /// <param name="logger">
    /// Where a <see cref="DeviceLost"/> handler that throws is reported. Null for none; the
    /// failure is still contained.
    /// </param>
    /// <exception cref="InvalidOperationException">FFmpeg could not set up D3D12VA on the device.</exception>
    public static unsafe HardwareDevice FromD3D12Device(nint d3d12Device, ILogger? logger = null)
    {
        if (d3d12Device == 0)
            throw new ArgumentNullException(nameof(d3d12Device));

        int type = FFAvUtil.AvHwDeviceTypeD3D12Va;
        nint deviceCtxRef = FFAvUtil.av_hwdevice_ctx_alloc(type);
        if (deviceCtxRef == nint.Zero)
            throw new InvalidOperationException("av_hwdevice_ctx_alloc returned null (out of memory).");

        // FFmpeg releases hwctx->device when the context is freed, including when init fails.
        Marshal.AddRef(d3d12Device);
        HwContext(deviceCtxRef)->device = d3d12Device;

        int rc = FFAvUtil.av_hwdevice_ctx_init(deviceCtxRef);
        if (rc < 0)
        {
            FFAvUtil.av_buffer_unref(ref deviceCtxRef);
            throw new InvalidOperationException(
                $"FFmpeg could not set up D3D12VA on the device: av_hwdevice_ctx_init returned {rc}.");
        }

        return new HardwareDevice(deviceCtxRef, HardwareDecodeBackendKind.D3D12Va, type, logger);
    }

    /// <summary>
    /// The <c>ID3D12Device*</c> a D3D12VA device decodes on, for building inference on the same
    /// device. Borrowed: valid while this object is undisposed; <c>AddRef</c> it to keep it longer.
    /// </summary>
    /// <returns>False for any other backend, or once disposed.</returns>
    public unsafe bool TryGetD3D12Device(out nint device)
    {
        nint deviceCtxRef = Volatile.Read(ref _deviceCtxRef);
        device = Backend == HardwareDecodeBackendKind.D3D12Va && deviceCtxRef != nint.Zero
            ? HwContext(deviceCtxRef)->device
            : 0;
        return device != 0;
    }

    /// <summary>A new reference to the device context, for a decoder to own.</summary>
    internal nint Borrow()
    {
        nint deviceCtxRef = Volatile.Read(ref _deviceCtxRef);
        ObjectDisposedException.ThrowIf(deviceCtxRef == nint.Zero, this);
        nint borrowed = FFAvUtil.av_buffer_ref(deviceCtxRef);
        return borrowed != nint.Zero
            ? borrowed
            : throw new InvalidOperationException("av_buffer_ref returned null (out of memory).");
    }

    /// <summary>
    /// Drops this object's reference. Decoders and frames that borrowed the device keep it alive
    /// until they are released.
    /// </summary>
    public void Dispose()
    {
        nint deviceCtxRef = Interlocked.Exchange(ref _deviceCtxRef, nint.Zero);
        if (deviceCtxRef != nint.Zero)
            FFAvUtil.av_buffer_unref(ref deviceCtxRef);
    }

    private static unsafe AVD3D12VADeviceContext* HwContext(nint deviceCtxRef)
    {
        var context = (AVHWDeviceContext*)((AVBufferRef*)deviceCtxRef)->data;
        return (AVD3D12VADeviceContext*)context->hwctx;
    }
}
