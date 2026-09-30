// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

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
/// </remarks>
public sealed class HardwareDevice : IDisposable
{
    private nint _deviceCtxRef;

    private HardwareDevice(nint deviceCtxRef, HardwareDecodeBackendKind backend, int avHwDeviceType)
    {
        _deviceCtxRef = deviceCtxRef;
        Backend = backend;
        AvHwDeviceType = avHwDeviceType;
    }

    /// <summary>The backend the device decodes with.</summary>
    public HardwareDecodeBackendKind Backend { get; }

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
    /// <exception cref="NotSupportedException"><paramref name="backend"/> is not an FFmpeg device type.</exception>
    /// <exception cref="InvalidOperationException">FFmpeg could not create the device.</exception>
    public static HardwareDevice Create(HardwareDecodeBackendKind backend, string? device = null)
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

        return new HardwareDevice(deviceCtxRef, backend, type);
    }

    /// <summary>
    /// A D3D12VA device on <paramref name="d3d12Device"/>, which the caller created. FFmpeg takes
    /// its own reference and releases it when the last decoder and this object are done.
    /// </summary>
    /// <param name="d3d12Device">An <c>ID3D12Device*</c>.</param>
    /// <exception cref="InvalidOperationException">FFmpeg could not set up D3D12VA on the device.</exception>
    public static unsafe HardwareDevice FromD3D12Device(nint d3d12Device)
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

        return new HardwareDevice(deviceCtxRef, HardwareDecodeBackendKind.D3D12Va, type);
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

    /// <summary>
    /// The Vulkan device a Vulkan device decodes on, for building objects that read its frames on the
    /// same device (#535).
    /// </summary>
    /// <param name="device">
    /// On success, the device, which holds its own reference to FFmpeg's device context, so it
    /// outlives this object until the caller disposes it.
    /// </param>
    /// <returns><see langword="false"/> for any other backend, or once disposed.</returns>
    public bool TryGetVulkanDevice([NotNullWhen(true)] out VulkanDevice? device)
    {
        device = null;
        if (Backend != HardwareDecodeBackendKind.Vulkan || Volatile.Read(ref _deviceCtxRef) == nint.Zero)
            return false;

        try
        {
            device = new VulkanDevice(Borrow());
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
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
