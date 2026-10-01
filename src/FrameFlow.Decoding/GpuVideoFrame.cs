// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.CodeAnalysis;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using FrameFlow.Graph;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding;

/// <summary>
/// A decoded video frame whose pixel data lives on a hardware-accelerator
/// device (CUDA, D3D11VA, VAAPI, VideoToolbox, etc.) rather than in CPU
/// memory (ADR-0038). Produced by <see cref="VideoDecoder"/> when
/// hardware decode is active and
/// <see cref="VideoDecoder.YieldHardwareFrames"/> is
/// <see langword="true"/>; otherwise the decoder performs an internal
/// readback and yields a CPU-resident <see cref="CpuVideoFrame"/> as
/// before.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership &amp; ref counting.</b> The frame wraps an
/// <c>AVFrame*</c> produced by <c>av_frame_clone</c>, so the underlying
/// device-side buffer (for D3D11VA, a slice of the decoder's
/// decode-texture array) is reference-counted by FFmpeg. On top of that
/// the <see cref="GpuVideoFrame"/> carries its own atomic object-level
/// ref count — exactly like <c>CpuVideoFrame</c> /
/// <c>PcmAudioBuffer</c>. <see cref="AddRef"/> hands the <i>same</i>
/// instance to an additional consumer and bumps the count; each
/// <see cref="Dispose"/> decrements it; only the final release calls
/// <c>av_frame_free</c>, which unrefs the device buffer and lets the
/// decoder recycle the texture slice. This is what lets one hardware
/// frame fan out to multiple GPU sinks (multi-pane / multicast) without
/// a per-consumer readback or clone — the substrate's fan-out
/// (<c>NodePumps</c>) calls <see cref="AddRef"/> once per extra branch.
/// </para>
/// <para>
/// <b>Reading the pixels.</b> Use the
/// <c>VideoOperators.ToCpu(id)</c> node from <c>FrameFlow.Video</c>, or
/// invoke <see cref="ReadbackToCpuBgra32"/> directly for a one-shot
/// readback. The <see cref="IVideoFrame.AsCpu"/> path returns
/// <see langword="null"/> (no in-place CPU view exists for a GPU
/// frame) and the <see cref="IVideoFrame.ToCpu"/> interface method
/// throws because it has no clean lifetime story for a temporary
/// readback buffer.
/// </para>
/// <para>
/// <b>Software pixel format.</b> The <see cref="Format"/> property
/// reports the format the frame would have after
/// <c>av_hwframe_transfer_data</c>, which is the pool's
/// <c>sw_format</c>: <see cref="PixelFormat.Nv12"/> for an 8-bit 4:2:0
/// stream and <see cref="PixelFormat.P010"/> for a 10-bit one. The GPU
/// buffer itself is in an opaque device-specific layout.
/// </para>
/// </remarks>
public sealed class GpuVideoFrame : IVideoFrame
{
    private FrameHandle? _handle;

    // Atomic object-level ref count (ADR-0038 fan-out). Starts at 1 for the
    // creating owner; AddRef bumps it, Dispose decrements it, and the wrapped
    // AVFrame is freed only at zero. The shared rule: Graph.RefCounting.
    private int _refCount = 1;

    // The pool this frame's surface belongs to, held until the final release so the pool's
    // surfaces stay in DecodePoolMetrics.Capacity while this frame pins one of them (#229), and
    // so the pool's guard can count the frame against its budget (#383).
    private DecodePoolGeneration? _pool;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Reports the <i>software</i> pixel format the frame would have
    /// after <c>av_hwframe_transfer_data</c>, not the GPU-side opaque
    /// format.
    /// </remarks>
    public PixelFormat Format { get; }

    /// <inheritdoc/>
    public TimeSpan Pts { get; }

    /// <inheritdoc/>
    public TimeSpan Duration { get; }

    /// <inheritdoc/>
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Gpu;

    /// <summary>
    /// The hardware-decode backend that produced this frame (ADR-0038
    /// Phase B). Determines how the underlying device handle is
    /// interpreted — e.g. <see cref="HardwareDecodeBackendKind.D3D11Va"/>
    /// means <see cref="TryGetD3D11Texture"/> can surface the
    /// <c>ID3D11Texture2D</c>.
    /// </summary>
    public HardwareDecodeBackendKind Backend { get; }

    /// <summary>
    /// The FFmpeg device context the frame's pool was made on: a borrowed
    /// <see cref="HardwareDevice"/>'s own, or the one its decoder created. Zero once disposed.
    /// </summary>
    internal nint HwDeviceContext =>
        NativeAvFrame is var frame and not 0 ? new AvFrameAccessor(frame).GetHwDeviceContextPointer() : 0;

    /// <summary>The frame's pool: its <c>AVHWFramesContext</c>. Zero once disposed.</summary>
    internal nint HwFramesContext =>
        NativeAvFrame is var frame and not 0 ? new AvFrameAccessor(frame).GetHwFramesContextPointer() : 0;

    /// <summary>
    /// Internal accessor for the wrapped <c>AVFrame*</c>. Used by the
    /// <c>FrameFlow.Video.ToCpu</c> operator (which has
    /// <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
    /// access) and by future GPU-aware sinks via downcasting. The public
    /// surface hands out the device texture instead, through
    /// <see cref="TryGetD3D11Texture"/> and <see cref="TryGetD3D12Texture"/>.
    /// </summary>
    internal nint NativeAvFrame
    {
        get
        {
            // Snapshot the field: with fan-out the frame is shared across
            // threads, so avoid reading _handle twice (it could be nulled by a
            // concurrent final Dispose between the check and the use).
            var h = _handle;
            return h is { IsInvalid: false } ? h.DangerousGetHandle() : nint.Zero;
        }
    }

    private GpuVideoFrame(
        FrameHandle handle,
        int width,
        int height,
        PixelFormat softwareFormat,
        TimeSpan pts,
        TimeSpan duration,
        HardwareDecodeBackendKind backend
    )
    {
        _handle = handle;
        Width = width;
        Height = height;
        Format = softwareFormat;
        Pts = pts;
        Duration = duration;
        Backend = backend;
    }

    /// <summary>
    /// Clones the source <c>AVFrame*</c> via <c>av_frame_clone</c> and
    /// wraps the resulting AVFrame in a <see cref="GpuVideoFrame"/>.
    /// The source frame is not consumed; the caller retains ownership
    /// of it.
    /// </summary>
    /// <param name="sourceAvFrame">
    /// Pointer to a live <c>AVFrame</c> whose pixel data is in
    /// hardware memory.
    /// </param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="softwareFormat">
    /// Pixel format the frame would have after readback: the pool's <c>sw_format</c>.
    /// </param>
    /// <param name="pts">Presentation timestamp.</param>
    /// <param name="duration">Frame duration.</param>
    /// <param name="backend">
    /// The hardware backend that produced the frame, so consumers can
    /// interpret the device handle (e.g. D3D11VA → <c>ID3D11Texture2D</c>).
    /// </param>
    /// <param name="pool">
    /// The pool the frame's surface belongs to, which the frame holds until its final release, or
    /// <see langword="null"/> when the caller does not track pools.
    /// </param>
    /// <returns>
    /// A new <see cref="GpuVideoFrame"/> that owns the cloned
    /// reference, or <see langword="null"/> if <c>av_frame_clone</c>
    /// failed (out of memory).
    /// </returns>
    internal static GpuVideoFrame? CloneFrom(
        nint sourceAvFrame,
        int width,
        int height,
        PixelFormat softwareFormat,
        TimeSpan pts,
        TimeSpan duration,
        HardwareDecodeBackendKind backend,
        DecodePoolGeneration? pool = null
    )
    {
        nint cloned = FFAvUtil.av_frame_clone(sourceAvFrame);
        if (cloned == nint.Zero)
            return null;

        var frame = FromOwnedAvFrame(cloned, width, height, softwareFormat, pts, duration, backend);
        pool?.AttachFrame();
        frame._pool = pool;
        return frame;
    }

    /// <summary>
    /// Wraps an <c>AVFrame*</c> that this frame takes <b>ownership</b> of (no
    /// clone). The frame is responsible for freeing it via <c>av_frame_free</c>
    /// at the final ref-count release; the caller must not free or reuse the
    /// pointer afterward. Used by <see cref="CloneFrom"/> (which owns the
    /// just-cloned frame) and by ref-count tests that mint a frame from a bare
    /// <c>av_frame_alloc</c>.
    /// </summary>
    /// <param name="ownedAvFrame">A live <c>AVFrame*</c> this frame will own.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="softwareFormat">Pixel format after readback: the pool's <c>sw_format</c>.</param>
    /// <param name="pts">Presentation timestamp.</param>
    /// <param name="duration">Frame duration.</param>
    /// <param name="backend">The hardware backend that produced the frame.</param>
    internal static GpuVideoFrame FromOwnedAvFrame(
        nint ownedAvFrame,
        int width,
        int height,
        PixelFormat softwareFormat,
        TimeSpan pts,
        TimeSpan duration,
        HardwareDecodeBackendKind backend
    )
    {
        var handle = new FrameHandle(ownedAvFrame);
        // One hwframe-pool slice is now pinned by this frame (perf survey §A1
        // pool-occupancy telemetry). Released at the final ref-drop in Dispose.
        DecodePoolMetrics.OnLeaseAcquired();
        return new GpuVideoFrame(handle, width, height, softwareFormat, pts, duration, backend);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Increments the atomic object-level ref count and returns the
    /// <i>same</i> instance (the codebase-wide <c>AddRef</c> contract —
    /// <c>Assert.Same</c> holds, and the graph's fan-out relies on
    /// reference equality to tell the inherit branch from AddRef
    /// siblings). The wrapped <c>AVFrame</c> — and the D3D11VA
    /// decode-texture slice it pins — survives until the final matching
    /// <see cref="Dispose"/>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The frame has already been fully released (ref count reached zero).
    /// </exception>
    public IVideoFrame AddRef()
    {
        RefCounting.AddRef(ref _refCount, this);
        return this;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always returns <see langword="null"/> — a GPU frame has no
    /// in-place CPU view. Use <see cref="ReadbackToCpuBgra32"/>, or
    /// <c>VideoOperators.ToCpu(id)</c> from <c>FrameFlow.Video</c>
    /// inside a graph, to obtain a CPU copy.
    /// </remarks>
    public CpuFrameData? AsCpu() => null;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// Always thrown. The interface method has no clean lifetime
    /// story for the temporary readback buffer (the returned
    /// <see cref="CpuFrameData"/> is a struct of memory views;
    /// who owns the backing buffer?). Use
    /// <see cref="ReadbackToCpuBgra32"/> instead — it returns a
    /// disposable <see cref="CpuVideoFrame"/> with explicit
    /// ownership.
    /// </exception>
    public CpuFrameData ToCpu() =>
        throw new NotSupportedException(
            "Use GpuVideoFrame.ReadbackToCpuBgra32(), or FrameFlow.Video's "
                + "VideoOperators.ToCpu(id) node inside a graph, to obtain a CPU copy with "
                + "explicit ownership."
        );

    /// <summary>
    /// Performs an explicit GPU→CPU readback of this frame's
    /// pixel data via <c>av_hwframe_transfer_data</c>, then
    /// <c>sws_scale</c>s the result to a tightly-packed
    /// <see cref="PixelFormat.Bgra32"/> <see cref="CpuVideoFrame"/>.
    /// The output's <c>PresentationTime</c> and <c>Duration</c>
    /// match this frame's. Caller owns the returned frame and must
    /// dispose it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocates a temporary <c>AVFrame</c> + <c>SwsContext</c> per
    /// call, and <c>VideoOperators.ToCpu(id)</c> calls this per frame,
    /// so it pays the same. Reuse across frames is #279's follow-up;
    /// next to the PCIe transfer and the full-frame scale it is small.
    /// Prefer the operator inside a graph because it composes, not
    /// because it is cheaper.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the frame has already been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the native readback or scale call fails.
    /// </exception>
    public CpuVideoFrame ReadbackToCpuBgra32()
    {
        var h = _handle;
        if (h is null || h.IsInvalid)
            throw new ObjectDisposedException(nameof(GpuVideoFrame));

        return GpuFrameReadback.ReadbackToBgra32(
            h.DangerousGetHandle(),
            Width,
            Height,
            Pts,
            Duration
        );
    }

    /// <summary>
    /// Surfaces the underlying Direct3D 11 decode texture for a zero-copy
    /// GPU presenter (ADR-0038 Phase B / ADR-0016 amendment). Only valid
    /// when <see cref="Backend"/> is
    /// <see cref="HardwareDecodeBackendKind.D3D11Va"/>: a D3D11VA frame
    /// stores the <c>ID3D11Texture2D*</c> in <c>AVFrame.data[0]</c> and the
    /// decode texture-array slice index in <c>AVFrame.data[1]</c> (NOT
    /// <c>linesize</c>). The texture is a shared decode-target array;
    /// <paramref name="subresourceIndex"/> selects the slice this frame
    /// occupies, which a presenter passes as the
    /// <c>ID3D11VideoProcessorInputView</c> array slice.
    /// </summary>
    /// <param name="texture">
    /// On success, a native <c>ID3D11Texture2D*</c>. The caller must NOT
    /// release it — it is owned by this frame's AVFrame and stays alive
    /// until the frame is disposed.
    /// </param>
    /// <param name="subresourceIndex">On success, the texture-array slice index.</param>
    /// <param name="device">
    /// On success, the native <c>ID3D11Device*</c> the decode texture lives on — a
    /// <b>stable per-decoder identity</b> (ADR-0064): every frame from one decoder
    /// reports the same pointer, a new decoder reports a different one. A zero-copy
    /// presenter compares it against the device its color-converter borrowed, so a
    /// player swap onto a warm sink (new decode device, same sink instance) is detected
    /// and the converter rebuilt instead of issuing cross-device copies against a
    /// disposed device. May be <see cref="nint.Zero"/> if the device chain is
    /// unavailable, even when the texture is surfaced — callers must treat
    /// <see cref="nint.Zero"/> as "identity unknown" and not as a converter mismatch.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a D3D11 texture was surfaced;
    /// <see langword="false"/> for non-D3D11VA frames or a disposed frame.
    /// </returns>
    public unsafe bool TryGetD3D11Texture(out nint texture, out int subresourceIndex, out nint device)
    {
        texture = nint.Zero;
        subresourceIndex = 0;
        device = nint.Zero;

        if (Backend != HardwareDecodeBackendKind.D3D11Va)
            return false;

        var h = _handle;
        if (h is null)
            return false;

        bool held = false;
        try
        {
            // Hold the handle across the read: a concurrent final Dispose would otherwise free
            // the AVFrame between the check and the dereference.
            h.DangerousAddRef(ref held);
            if (h.IsInvalid)
                return false;

            var accessor = new AvFrameAccessor(h.DangerousGetHandle());
            texture = (nint)accessor.GetDataPointer(0);
            subresourceIndex = (int)(nint)accessor.GetDataPointer(1);
            device = accessor.GetD3D11DevicePointer();
            return texture != nint.Zero;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (held)
                h.DangerousRelease();
        }
    }

    /// <summary>
    /// Surfaces the Direct3D 12 texture a D3D12VA frame was decoded into, and the fence that
    /// says when it is written, for a consumer that reads the frame on the GPU: a compute
    /// shader, or an inference session on the same device (#289). Only valid when
    /// <see cref="Backend"/> is <see cref="HardwareDecodeBackendKind.D3D12Va"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decoder writes the frame on its own queue and signals <paramref name="fence"/> to
    /// <paramref name="fenceValue"/> when it is done. A consumer on another queue waits for it
    /// on the GPU with <c>ID3D12CommandQueue::Wait(fence, fenceValue)</c> before reading the
    /// texture.
    /// </para>
    /// <para>
    /// The texture and fence belong to the decoder's device. Build GPU work that reads them on
    /// that device, from <c>ID3D12DeviceChild::GetDevice</c> on the texture. A device created on
    /// the same adapter is by default the same device, but one created through
    /// <c>ID3D12DeviceFactory</c> is not. Holding a reference to the device past the decoder's
    /// last frame crashes the process when a TensorRT-RTX session is loaded (#422).
    /// </para>
    /// <para>
    /// The pointers are borrowed, and the frame's surface goes back to the decoder's pool when the
    /// frame is released. Keep the frame, or an <see cref="AddRef"/> of it, until every piece of
    /// GPU work that reads them has finished, not only until it is submitted, and do not dispose
    /// it on another thread meanwhile.
    /// </para>
    /// </remarks>
    /// <param name="texture">
    /// On success, a native <c>ID3D12Resource*</c> (NV12, or P010 for 10-bit). The caller must
    /// not release it: the frame owns it until the frame is disposed. AddRef it to keep it longer.
    /// </param>
    /// <param name="subresourceIndex">
    /// On success, the subresource the frame occupies: 0, since FrameFlow's D3D12VA pool gives
    /// each frame its own texture (ADR-0081, 2026-09-27 revision).
    /// </param>
    /// <param name="fence">
    /// On success, a native <c>ID3D12Fence*</c>, owned by the frame like <paramref name="texture"/>.
    /// </param>
    /// <param name="fenceValue">On success, the value <paramref name="fence"/> reaches once the frame is written.</param>
    /// <returns>
    /// <see langword="true"/> when a D3D12 texture was surfaced; <see langword="false"/> for
    /// non-D3D12VA frames or a disposed frame.
    /// </returns>
    public unsafe bool TryGetD3D12Texture(out nint texture, out int subresourceIndex, out nint fence, out ulong fenceValue)
    {
        texture = nint.Zero;
        subresourceIndex = 0;
        fence = nint.Zero;
        fenceValue = 0;

        if (Backend != HardwareDecodeBackendKind.D3D12Va)
            return false;

        var h = _handle;
        if (h is null)
            return false;

        bool held = false;
        try
        {
            // Hold the handle across the read, as TryGetD3D11Texture does.
            h.DangerousAddRef(ref held);
            if (h.IsInvalid)
                return false;

            var frame = new AvFrameAccessor(h.DangerousGetHandle()).GetD3D12Frame();
            if (frame is null)
                return false;

            texture = frame->texture;
            subresourceIndex = frame->subresource_index;
            fence = frame->sync_ctx.fence;
            fenceValue = frame->sync_ctx.fence_value;
            return texture != nint.Zero && fence != nint.Zero;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (held)
                h.DangerousRelease();
        }
    }

    /// <summary>
    /// Surfaces where a CUDA-decoded (NVDEC) frame's samples are in device memory, for a consumer
    /// that reads them on the GPU, such as an inference session bound to CUDA memory (#289). Only
    /// valid when <see cref="Backend"/> is <see cref="HardwareDecodeBackendKind.Cuda"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The planes are laid out as <see cref="Format"/> says: NV12 for an 8-bit stream, P010 for a
    /// 10-bit one, each with its luma plane first and its chroma interleaved at half the height. A
    /// frame in any other layout, such as 4:4:4, surfaces nothing.
    /// The decoder copies the frame into this memory asynchronously on
    /// <see cref="CudaFramePlanes.Stream"/>, so read on that stream or synchronize with it first,
    /// with <see cref="CudaFramePlanes.Context"/> current.
    /// </para>
    /// <para>
    /// The pointers are borrowed, and the frame's surface goes back to the decoder's pool when the
    /// frame is released. Keep the frame, or an <see cref="AddRef"/> of it, until every piece of
    /// GPU work that reads them has finished, not only until it is submitted, and do not dispose
    /// it on another thread meanwhile.
    /// </para>
    /// </remarks>
    /// <param name="planes">On success, the planes, their pitches, and the context and stream they belong to.</param>
    /// <returns>
    /// <see langword="true"/> when the planes were surfaced; <see langword="false"/> for a frame
    /// from another backend, one that is neither NV12 nor P010, or a disposed frame.
    /// </returns>
    public unsafe bool TryGetCudaPlanes(out CudaFramePlanes planes)
    {
        planes = default;

        // Two planes, luma and interleaved chroma, is all CudaFramePlanes describes.
        if (Backend != HardwareDecodeBackendKind.Cuda || Format is not (PixelFormat.Nv12 or PixelFormat.P010))
            return false;

        var h = _handle;
        if (h is null)
            return false;

        bool held = false;
        try
        {
            // Hold the handle across the read, as TryGetD3D11Texture does.
            h.DangerousAddRef(ref held);
            if (h.IsInvalid)
                return false;

            var accessor = new AvFrameAccessor(h.DangerousGetHandle());
            var device = accessor.GetCudaDeviceContext();
            var luma = (nint)accessor.GetDataPointer(0);
            var chroma = (nint)accessor.GetDataPointer(1);
            if (device is null || luma == nint.Zero || chroma == nint.Zero)
                return false;

            planes = new CudaFramePlanes(
                luma,
                accessor.GetLineSize(0),
                chroma,
                accessor.GetLineSize(1),
                device->cuda_ctx,
                device->stream);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (held)
                h.DangerousRelease();
        }
    }

    /// <summary>
    /// Locks a Vulkan-decoded frame's image for one submission and surfaces what the submission
    /// needs: the image, its layout and last access, and the timeline semaphore values to wait on
    /// and signal (#535). Only valid when <see cref="Backend"/> is
    /// <see cref="HardwareDecodeBackendKind.Vulkan"/>.
    /// </summary>
    /// <remarks>
    /// Lock just before submitting and dispose the lock straight after, without waiting for the
    /// work: FFmpeg's decoder waits on the lock to use the image as a reference picture.
    /// <see cref="VulkanImageLock"/> has the rules. The device the image lives on is
    /// <see cref="TryGetVulkanDevice"/>'s.
    /// </remarks>
    /// <param name="image">On success, the locked image, which the caller disposes.</param>
    /// <returns>
    /// <see langword="true"/> when the image was locked; <see langword="false"/> for a frame from
    /// another backend, one held in more than one image, or a disposed frame.
    /// </returns>
    public unsafe bool TryLockVulkanImage([NotNullWhen(true)] out VulkanImageLock? image)
    {
        image = null;
        // The hwcontext mirrors have the 64-bit layout, which every runtime FrameFlow ships uses.
        if (Backend != HardwareDecodeBackendKind.Vulkan || !Environment.Is64BitProcess)
            return false;

        var h = _handle;
        if (h is null)
            return false;

        bool held = false;
        try
        {
            // Held until the lock is disposed, so the frame's AVVkFrame outlives a concurrent Dispose.
            h.DangerousAddRef(ref held);
            if (h.IsInvalid)
                return false;

            var accessor = new AvFrameAccessor(h.DangerousGetHandle());
            var framesCtx = accessor.GetHwFramesContext();
            var frame = accessor.GetVulkanFrame();
            // The decoder's pool holds every plane in one multi-planar image; a frame split across
            // images, as a pool with AV_VK_FRAME_FLAG_DISABLE_MULTIPLANE makes, has more to lock.
            if (framesCtx is null || framesCtx->hwctx is null || frame is null || frame->img[0] == 0 || frame->img[1] != 0)
                return false;

            // FFmpeg fills both in at init; without them the lock would protect nothing.
            var vulkanFrames = (AVVulkanFramesContext*)framesCtx->hwctx;
            if (vulkanFrames->lock_frame == null || vulkanFrames->unlock_frame == null)
                return false;

            vulkanFrames->lock_frame(framesCtx, frame);
            image = new VulkanImageLock(h, framesCtx, frame);
            held = false;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (held)
                h.DangerousRelease();
        }
    }

    /// <summary>
    /// The Vulkan device a Vulkan-decoded frame was decoded on (#535), for building the objects that
    /// read its image. Only valid when <see cref="Backend"/> is
    /// <see cref="HardwareDecodeBackendKind.Vulkan"/>.
    /// </summary>
    /// <param name="device">
    /// On success, the device, which holds a reference that keeps it alive until the caller
    /// disposes it.
    /// </param>
    /// <returns><see langword="false"/> for a frame from another backend, or a disposed frame.</returns>
    public unsafe bool TryGetVulkanDevice([NotNullWhen(true)] out VulkanDevice? device)
    {
        device = null;
        if (Backend != HardwareDecodeBackendKind.Vulkan || !Environment.Is64BitProcess)
            return false;

        var h = _handle;
        if (h is null)
            return false;

        bool held = false;
        try
        {
            h.DangerousAddRef(ref held);
            if (h.IsInvalid)
                return false;

            var framesCtx = new AvFrameAccessor(h.DangerousGetHandle()).GetHwFramesContext();
            if (framesCtx is null || framesCtx->device_ref is null)
                return false;

            nint deviceCtxRef = FFAvUtil.av_buffer_ref((nint)framesCtx->device_ref);
            if (deviceCtxRef == nint.Zero)
                throw new InvalidOperationException("av_buffer_ref returned null (out of memory).");
            device = new VulkanDevice(deviceCtxRef);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (held)
                h.DangerousRelease();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Decrements the object-level ref count. Only the final release
    /// (count &#8594; 0) frees the wrapped <c>AVFrame</c> via
    /// <c>av_frame_free</c>, which unrefs the device buffer and returns the
    /// decode-texture slice to the decoder's hwframe pool. Disposes past
    /// zero frees nothing and is counted as an over-release
    /// (<see cref="RefCounting"/>).
    /// </remarks>
    public void Dispose()
    {
        if (!RefCounting.Release(ref _refCount, this))
            return;

        // The final release. av_frame_free unrefs the device buffer / decode-texture slice.
        _handle?.Dispose();
        _handle = null;
        // The pinned hwframe-pool slice is returned (perf survey §A1 telemetry).
        DecodePoolMetrics.OnLeaseReleased();
        _pool?.DetachFrame();
        _pool = null;
    }
}
