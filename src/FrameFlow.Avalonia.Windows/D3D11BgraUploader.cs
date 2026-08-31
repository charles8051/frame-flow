// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// CPU-frame fallback for the composition-interop presenter: uploads a BGRA32
/// <see cref="CpuFrameData"/> into a <b>ring of shared keyed-mutex BGRA textures</b>
/// that Avalonia's compositor can import — the same import/present path the zero-copy
/// GPU source uses, so the presenter never goes blank when hardware decode doesn't bind.
/// Windows / D3D11 only.
/// </summary>
/// <remarks>
/// This is a <i>single-copy</i> path (CPU → staging → shared texture), not zero-copy:
/// software decode already paid for a CPU frame, so the upload is the unavoidable cost.
/// It owns its own D3D11 device (there is no decoder device to borrow on the CPU path)
/// and a reusable dynamic staging texture; <see cref="UploadInto"/> maps the staging
/// texture, copies the rows, then <c>CopyResource</c>s it into the chosen ring buffer
/// under that buffer's keyed mutex (producer side: acquire 0, release 1).
/// </remarks>
internal sealed class D3D11BgraUploader : IDisposable
{
    private readonly ILogger _logger;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _staging;
    private readonly RingBuffer[] _buffers;
    private bool _disposed;
    private bool _deviceLost;

    public int Width { get; }
    public int Height { get; }

    public nint GetSharedHandle(int index) => _buffers[index].SharedHandle;

    /// <summary>
    /// <see langword="true"/> once a GPU device-loss / TDR has been observed on this
    /// uploader's own device. Sticky; the presenter drops the ring and rebuilds a fresh
    /// uploader on the next frame rather than blocking a doomed keyed-mutex acquire
    /// (investigation 2026-06-12, §6 step 6). The CPU path owns its device, so it does not
    /// have the borrowed-device flush hazard (Mechanism B) — but the same keyed-mutex
    /// teardown-ordering rule (Mechanism A) applies, so the view drains presents and
    /// disposes the imported images before disposing this uploader.
    /// </summary>
    public bool IsDeviceLost => _deviceLost;

    private sealed class RingBuffer
    {
        public required ID3D11Texture2D Texture;
        public required IDXGIKeyedMutex KeyedMutex;
        public nint SharedHandle;
    }

    public D3D11BgraUploader(int width, int height, ILogger logger)
    {
        _logger = logger;
        Width = width;
        Height = height;

        // The CPU path has no decoder device to borrow — create our own. BGRA support
        // is required so the compositor can import B8G8R8A8 textures we share.
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device!
        ).CheckError();
        _context = _device.ImmediateContext;

        // Reusable CPU-writable staging texture (mapped each frame, copied into the ring).
        _staging = _device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None,
            }
        );

        _buffers = new RingBuffer[D3D11Nv12SharedConverter.BufferCount];
        for (var i = 0; i < _buffers.Length; i++)
        {
            var tex = _device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    // RenderTarget | ShaderResource matches the GPU path's shared textures —
                    // Avalonia's compositor import expects render-target-capable textures, and
                    // a ShaderResource-only texture leaves the keyed-mutex hand-off incomplete.
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
                }
            );

            nint handle;
            using (var dxgi = tex.QueryInterface<IDXGIResource>())
                handle = dxgi.SharedHandle;

            _buffers[i] = new RingBuffer
            {
                Texture = tex,
                KeyedMutex = tex.QueryInterface<IDXGIKeyedMutex>(),
                SharedHandle = handle,
            };
        }

        _logger.LogInformation(
            "D3D11 BGRA uploader ready (CPU-frame fallback): {W}x{H}, {N}-buffer shared keyed-mutex ring.",
            width, height, _buffers.Length
        );
    }

    /// <summary>
    /// Uploads <paramref name="cpu"/> (BGRA32) into ring buffer <paramref name="index"/>:
    /// map the staging texture, copy rows (honoring the source stride), then
    /// <c>CopyResource</c> into the shared buffer under its keyed mutex.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the buffer was filled and is ready to present;
    /// <see langword="false"/> if a GPU device-loss / TDR was observed — in which case
    /// <see cref="IsDeviceLost"/> is now set and the caller should drop this frame and
    /// rebuild the uploader rather than presenting (step 6). Any non-device-loss failure
    /// still throws.
    /// </returns>
    public bool UploadInto(int index, CpuFrameData cpu)
    {
        if (IsDeviceLost)
            return false;

        var mapped = _context.Map(_staging, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var src = cpu.PlaneY.Span;
            var dstRowPitch = (int)mapped.RowPitch;
            var rowBytes = Math.Min(cpu.StrideY, dstRowPitch);
            unsafe
            {
                fixed (byte* s = src)
                {
                    var dst = (byte*)mapped.DataPointer;
                    for (var y = 0; y < Height; y++)
                        Buffer.MemoryCopy(
                            s + (long)y * cpu.StrideY,
                            dst + (long)y * dstRowPitch,
                            dstRowPitch,
                            rowBytes
                        );
                }
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        var buf = _buffers[index];
        var acquired = false;
        try
        {
            buf.KeyedMutex.AcquireSync(0, 1000);
            acquired = true;
            _context.CopyResource(buf.Texture, _staging);
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            _deviceLost = true;
            _logger.LogWarning(ex, "GPU device lost during BGRA upload; dropping ring for rebuild.");
            return false;
        }
        finally
        {
            if (acquired)
            {
                try { buf.KeyedMutex.ReleaseSync(1); }
                catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex)) { _deviceLost = true; }
            }
        }

        return true;
    }

    /// <summary>
    /// Releases the ring textures, staging texture, and this uploader's own device.
    /// <para>
    /// <b>Threading contract.</b> Like the GPU converter, the presenter must invoke this
    /// only after the compositor has released the shared keyed mutex (presents drained,
    /// imported images disposed) — see investigation 2026-06-12, §7.3. Each native release
    /// is guarded so a device-loss cannot throw out of teardown.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var b in _buffers)
        {
            if (b is null)
                continue;
            Release(() => b.KeyedMutex.Dispose());
            Release(() => b.Texture.Dispose());
        }
        Release(() => _staging.Dispose());
        Release(() => _context.Dispose());
        Release(() => _device.Dispose());
    }

    private void Release(Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignored fault releasing a D3D11 resource during uploader teardown.");
        }
    }
}
