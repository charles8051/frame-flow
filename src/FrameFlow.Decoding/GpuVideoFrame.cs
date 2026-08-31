// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native.Interop;
using FrameFlow.Graph;
using FrameFlow.Decoding.Diagnostics;

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
/// ref count — exactly like <c>PooledCpuVideoFrame</c> /
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
/// <c>pipeline.ToCpu()</c> operator from <c>FrameFlow.Video</c>, or
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
/// <c>av_hwframe_transfer_data</c> — typically
/// <see cref="PixelFormat.Nv12"/> for CUDA / D3D11VA / VAAPI. This is
/// the format consumers should expect downstream of
/// <c>ToCpu()</c>; the GPU buffer itself is in an opaque device-
/// specific layout.
/// </para>
/// </remarks>
public sealed class GpuVideoFrame : IVideoFrame
{
    private FrameHandle? _handle;

    // Atomic object-level ref count (ADR-0038 fan-out). Starts at 1 for the
    // creating owner; AddRef bumps it, Dispose decrements it, and the wrapped
    // AVFrame is freed only at zero. Canonical pattern: PooledCpuVideoFrame.
    private int _refCount = 1;

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
    /// Internal accessor for the wrapped <c>AVFrame*</c>. Used by the
    /// <c>FrameFlow.Video.ToCpu</c> operator (which has
    /// <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
    /// access) and by future GPU-aware sinks via downcasting. Public
    /// API surface intentionally avoids native pointers.
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
    /// Pixel format the frame would have after readback (typically
    /// <see cref="PixelFormat.Nv12"/>).
    /// </param>
    /// <param name="pts">Presentation timestamp.</param>
    /// <param name="duration">Frame duration.</param>
    /// <param name="backend">
    /// The hardware backend that produced the frame, so consumers can
    /// interpret the device handle (e.g. D3D11VA → <c>ID3D11Texture2D</c>).
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
        HardwareDecodeBackendKind backend
    )
    {
        nint cloned = FFAvUtil.av_frame_clone(sourceAvFrame);
        if (cloned == nint.Zero)
            return null;

        return FromOwnedAvFrame(cloned, width, height, softwareFormat, pts, duration, backend);
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
    /// <param name="softwareFormat">Pixel format after readback (typically NV12).</param>
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
        // Spin until we either increment or discover the frame is disposed.
        // Mirrors PooledCpuVideoFrame / PcmAudioBuffer.
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    nameof(GpuVideoFrame),
                    "Cannot AddRef a GPU frame whose ref count has reached zero."
                );
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return this;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always returns <see langword="null"/> — a GPU frame has no
    /// in-place CPU view. Use <see cref="ReadbackToCpuBgra32"/> or
    /// the <c>pipeline.ToCpu()</c> operator to obtain a CPU copy.
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
            "Use GpuVideoFrame.ReadbackToCpuBgra32() or the FrameFlow.Video pipeline operator "
                + "pipeline.ToCpu() to obtain a CPU copy with explicit ownership."
        );

    /// <summary>
    /// Performs an explicit GPU&rarr;CPU readback of this frame's
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
    /// call. For pipeline use, prefer the
    /// <c>pipeline.ToCpu()</c> operator which caches the sws
    /// context across frames.
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
        if (h is null || h.IsInvalid)
            return false;

        var accessor = new AvFrameAccessor(h.DangerousGetHandle());
        texture = (nint)accessor.GetDataPointer(0);
        subresourceIndex = (int)(nint)accessor.GetDataPointer(1);
        device = accessor.GetD3D11DevicePointer();
        return texture != nint.Zero;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Decrements the object-level ref count. Only the final release
    /// (count &#8594; 0) frees the wrapped <c>AVFrame</c> via
    /// <c>av_frame_free</c>, which unrefs the device buffer and returns the
    /// decode-texture slice to the decoder's hwframe pool. Disposes past
    /// zero are no-ops (idempotent), matching the rest of the refcounted
    /// frame / buffer types.
    /// </remarks>
    public void Dispose()
    {
        int newCount = Interlocked.Decrement(ref _refCount);

        if (newCount > 0)
            return;

        if (newCount < 0)
        {
            // Over-dispose — restore to zero and bail (idempotent).
            Interlocked.Increment(ref _refCount);
            return;
        }

        // newCount == 0 — final release. av_frame_free unrefs the device
        // buffer / decode-texture slice.
        _handle?.Dispose();
        _handle = null;
        // The pinned hwframe-pool slice is returned (perf survey §A1 telemetry).
        DecodePoolMetrics.OnLeaseReleased();
    }
}
