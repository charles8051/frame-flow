// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Decoding;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Converts D3D12VA frames, NV12 or P010, into the ring of shared keyed-mutex BGRA textures
/// Avalonia's compositor imports (#429). The conversion runs on Direct3D 12, on the decoder's
/// device, and a D3D11 device of the converter's own copies the result into the ring.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two devices.</b> Avalonia's Windows compositor imports D3D11 textures synchronised by a
/// keyed mutex, and nothing else; a D3D12 resource cannot carry a keyed mutex
/// (<c>docs/investigations/2026-09-28-d3d12-presenter.md</c>). So a pixel shader on D3D12 draws the
/// frame into a BGRA texture on a shared heap, and a D3D11 device on the same adapter opens that
/// texture and copies it into the ring buffer the compositor presents. That copy is the one extra
/// step over the D3D11VA path, which copies the NV12 slice before converting instead of after.
/// </para>
/// <para>
/// <b>Synchronisation.</b> One shared fence, signalled by both devices in turn
/// (<see cref="BridgeFenceValues"/>). The D3D12 queue waits for the frame's decode fence and for the
/// previous frame's copy, draws, and signals; the D3D11 context waits for that on the GPU, copies,
/// and signals back. The ring's keyed mutex is the D3D11 converter's: acquire 0, release 1.
/// </para>
/// <para>
/// <b>Frame lifetime.</b> The GPU reads the decode texture after <see cref="ConvertInto"/> returns,
/// so the converter holds a reference to the frame until the draw has finished, and releases it at
/// the next call or at <see cref="Dispose"/>. The sink's <c>MaxHeldFrames</c> counts it.
/// </para>
/// <para>
/// <b>The decoder's device.</b> The D3D12 half is built on the device the frames are decoded on,
/// and holds references to it. With a TensorRT-RTX session loaded, a reference held past the
/// decoder's last frame crashes the process (#422); give the player a <see cref="HardwareDevice"/>
/// so the device outlives its decoders.
/// </para>
/// <para>
/// UI thread only, like the D3D11 converter; <see cref="Dispose"/> runs off it.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2213", Justification = "Every object is released through _owned, in reverse creation order.")]
internal sealed unsafe class D3D12YuvSharedConverter : IDisposable
{
    /// <summary>Number of shared BGRA textures in the ring, as the D3D11 converter has.</summary>
    public const int BufferCount = D3D11Nv12SharedConverter.BufferCount;

    // How long a CPU wait on the GPU may take before the device is treated as hung. A draw and a
    // copy take well under a frame; this only bounds a device that stopped making progress.
    private static readonly TimeSpan GpuWaitLimit = TimeSpan.FromSeconds(1);

    // The D3D11 converter's conversion (ADR-0063), with the sample levels and the frame's part of
    // the texture as root constants, so NV12 and P010 share one pipeline.
    private const string Hlsl = """
        cbuffer Constants : register(b0)
        {
            float2 UvScale;
            float2 ChromaUvMax;
            float YOffset, YScale, COffset, CScale;
        };

        Texture2D<float>  Luma   : register(t0);
        Texture2D<float2> Chroma : register(t1);
        SamplerState      Samp   : register(s0);

        struct VSOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        VSOut VSMain(uint vid : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((vid << 1) & 2, vid & 2);
            o.uv  = uv;
            o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return o;
        }

        float4 PSMain(VSOut input) : SV_Target
        {
            float2 uv = input.uv * UvScale;
            float  y  = Luma.Sample(Samp, uv);
            float2 c  = Chroma.Sample(Samp, min(uv, ChromaUvMax));

            float Y = (y   - YOffset) * YScale;
            float U = (c.x - COffset) * CScale;
            float V = (c.y - COffset) * CScale;

            // BT.709 inverse matrix (Kr=0.2126, Kb=0.0722).
            float3 rgb;
            rgb.r = Y +               1.5748 * V;
            rgb.g = Y - 0.1873  * U - 0.4681 * V;
            rgb.b = Y + 1.8556  * U;
            return float4(saturate(rgb), 1.0);
        }
        """;

    // The native API takes NULL for a handle with no name; Vortice's signature is not annotated for it.
    private static readonly string Unnamed = null!;

    private readonly ILogger _logger;

    // Everything created here, released in reverse at Dispose or when the constructor fails.
    private readonly List<IDisposable> _owned = [];

    // D3D12, on the decoder's device.
    private readonly ID3D12Device _device12;
    private readonly nint _deviceIdentity;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipeline;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly uint _srvSize;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _list;
    private readonly ID3D12Resource _drawn;
    private readonly ID3D12Fence _fence;
    private readonly ManualResetEvent _fenceReached = new(false);

    // D3D11, the converter's own device on the same adapter.
    private readonly ID3D11DeviceContext4 _context;
    private readonly ID3D11Texture2D _drawnOn11;
    private readonly ID3D11Fence _fenceOn11;
    private readonly RingBuffer[] _buffers = new RingBuffer[BufferCount];

    private ulong _frames;
    private IVideoFrame? _held;
    private ulong _heldUntil;
    private bool _deviceLost;
    private bool _disposed;

    private sealed class RingBuffer
    {
        public required ID3D11Texture2D Texture;
        public required IDXGIKeyedMutex KeyedMutex;
        public nint SharedHandle;
    }

    /// <summary>
    /// Builds the converter for <paramref name="width"/> by <paramref name="height"/> frames on the
    /// device <paramref name="texture"/>, a frame's <c>ID3D12Resource*</c>, belongs to.
    /// </summary>
    public D3D12YuvSharedConverter(nint texture, int width, int height, ILogger logger)
    {
        _logger = logger;
        Width = width;
        Height = height;

        _owned.Add(_fenceReached);
        try
        {
            using (var resource = Borrow<ID3D12Resource>(texture))
                _device12 = Own(resource.GetDevice<ID3D12Device>());
            _deviceIdentity = Identity(_device12.NativePointer);

            _queue = Own(_device12.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct)));
            _rootSignature = Own(_device12.CreateRootSignature(RootSignature()));
            _pipeline = Own(_device12.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = _rootSignature,
                VertexShader = Compile("VSMain", "vs_5_0"),
                PixelShader = Compile("PSMain", "ps_5_0"),
                BlendState = Vortice.Direct3D12.BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = Vortice.Direct3D12.RasterizerDescription.CullNone,
                DepthStencilState = Vortice.Direct3D12.DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.B8G8R8A8_UNorm],
                SampleDescription = new SampleDescription(1, 0),
            }));

            const DescriptorHeapType ViewHeap = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
            _srvHeap = Own(_device12.CreateDescriptorHeap(new DescriptorHeapDescription(ViewHeap, 2, DescriptorHeapFlags.ShaderVisible)));
            _srvSize = _device12.GetDescriptorHandleIncrementSize(ViewHeap);
            _rtvHeap = Own(_device12.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1)));
            _allocator = Own(_device12.CreateCommandAllocator(CommandListType.Direct));
            _list = Own(_device12.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _allocator, _pipeline));
            _list.Close();

            // The texture the shader draws into and the D3D11 device copies from, and the fence the
            // two signal in turn. Both are shared, so the D3D11 device can open them.
            _drawn = Own(_device12.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.Shared,
                ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.Common));
            _device12.CreateRenderTargetView(_drawn, null, _rtvHeap.GetCPUDescriptorHandleForHeapStart());
            _fence = Own(_device12.CreateFence(0, Vortice.Direct3D12.FenceFlags.Shared));

            // A D3D11 device on the same adapter; BgraSupport so the compositor can import the ring.
            ID3D11Device device11;
            using (var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false))
            using (var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(Luid(_device12.AdapterLuid)))
            {
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? created)
                    .CheckError();
                device11 = Own(created!);
            }

            _context = Own(device11.ImmediateContext.QueryInterface<ID3D11DeviceContext4>());
            using (var device1 = device11.QueryInterface<ID3D11Device1>())
            using (var handle = new SafeFileHandle(_device12.CreateSharedHandle(_drawn, null, Unnamed), ownsHandle: true))
                _drawnOn11 = Own(device1.OpenSharedResource1<ID3D11Texture2D>(handle.DangerousGetHandle()));
            using (var device5 = device11.QueryInterface<ID3D11Device5>())
            using (var handle = new SafeFileHandle(_device12.CreateSharedHandle(_fence, null, Unnamed), ownsHandle: true))
                _fenceOn11 = Own(device5.OpenSharedFence<ID3D11Fence>(handle.DangerousGetHandle()));

            for (var i = 0; i < BufferCount; i++)
            {
                var ring = Own(device11.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
                }));
                using var dxgi = ring.QueryInterface<IDXGIResource>();
                _buffers[i] = new RingBuffer
                {
                    Texture = ring,
                    KeyedMutex = Own(ring.QueryInterface<IDXGIKeyedMutex>()),
                    SharedHandle = dxgi.SharedHandle,
                };
            }
        }
        catch
        {
            ReleaseOwned();
            throw;
        }

        _logger.LogInformation(
            "D3D12 presenter converter ready (#429): {W}x{H}, pixel shader on the decoder's D3D12 device 0x{Device:X}, "
                + "copied by an own D3D11 device into a {N}-buffer shared keyed-mutex ring.",
            width, height, _deviceIdentity, BufferCount);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The decoder's device, as its <c>IUnknown</c> identity. Frames from another device need a new
    /// converter. The converter holds references to the device, so the pointer is not reused while
    /// it lives.
    /// </summary>
    public nint SourceDevicePointer => _deviceIdentity;

    /// <summary>
    /// <see langword="true"/> once either device was lost or stopped making progress, or the fence
    /// chain between them broke. Sticky: the presenter drops the converter and builds a new one.
    /// </summary>
    public bool IsDeviceLost => _deviceLost;

    /// <summary>The legacy global shared handle for ring buffer <paramref name="index"/>.</summary>
    public nint GetSharedHandle(int index) => _buffers[index].SharedHandle;

    /// <summary>The <c>IUnknown</c> identity of the device a frame's <c>ID3D12Resource*</c> belongs to.</summary>
    public static nint DeviceIdentity(nint texture)
    {
        using var resource = Borrow<ID3D12Resource>(texture);
        using var device = resource.GetDevice<ID3D12Device>();
        return Identity(device.NativePointer);
    }

    /// <summary>
    /// Converts <paramref name="frame"/> into ring buffer <paramref name="index"/>, whose previous
    /// present must have completed. Holds the frame until the GPU has read it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the buffer is ready to present; <see langword="false"/> when a
    /// device was lost or hung, in which case <see cref="IsDeviceLost"/> is set.
    /// </returns>
    /// <exception cref="NotSupportedException">The frame is neither NV12 nor P010, or sits in a texture array.</exception>
    public bool ConvertInto(int index, GpuVideoFrame frame)
    {
        if (_deviceLost || _disposed)
            return false;
        if (!frame.TryGetD3D12Texture(out nint textureHandle, out int subresource, out nint fenceHandle, out ulong fenceValue))
            throw new ArgumentException($"A {frame.Backend} frame has no D3D12 texture.", nameof(frame));
        if (subresource != 0)
            throw new NotSupportedException("A frame in a texture array is not supported.");

        // The last draw read its frame and its descriptors; both are reused below.
        if (!Retire())
            return false;

        using var texture = Borrow<ID3D12Resource>(textureHandle);
        using var decoded = Borrow<ID3D12Fence>(fenceHandle);
        var description = texture.Description;
        var (samples, lumaFormat, chromaFormat) = description.Format switch
        {
            Format.NV12 => (YuvSampleFormat.Nv12, Format.R8_UNorm, Format.R8G8_UNorm),
            Format.P010 => (YuvSampleFormat.P010, Format.R16_UNorm, Format.R16G16_UNorm),
            var other => throw new NotSupportedException($"The frame's texture is {other}; NV12 and P010 are supported."),
        };
        var constants = D3D12PresentConstants.Create(
            samples, (int)description.Width, (int)description.Height, Width, Height);

        var srv = _srvHeap.GetCPUDescriptorHandleForHeapStart();
        _device12.CreateShaderResourceView(texture, PlaneView(lumaFormat, 0), srv);
        _device12.CreateShaderResourceView(texture, PlaneView(chromaFormat, 1), srv + (int)_srvSize);

        _allocator.Reset();
        _list.Reset(_allocator, _pipeline);
        _list.SetGraphicsRootSignature(_rootSignature);
        _list.SetDescriptorHeaps(_srvHeap);
        _list.SetGraphicsRoot32BitConstants(0, D3D12PresentConstants.Count, &constants, 0);
        _list.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart());
        _list.RSSetViewport(0, 0, Width, Height, 0, 1);
        _list.RSSetScissorRect(Width, Height);
        _list.ResourceBarrierTransition(_drawn, ResourceStates.Common, ResourceStates.RenderTarget);
        _list.OMSetRenderTargets(_rtvHeap.GetCPUDescriptorHandleForHeapStart(), null);
        _list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _list.DrawInstanced(3, 1, 0, 0);
        // COMMON, so the D3D11 device, which tracks no D3D12 state, can read it.
        _list.ResourceBarrierTransition(_drawn, ResourceStates.RenderTarget, ResourceStates.Common);
        _list.Close();

        var values = BridgeFenceValues.For(_frames + 1);

        // From here the GPU may read the frame, so hold it whatever happens next.
        _held = frame.AddRef();
        _heldUntil = values.Drawn;
        try
        {
            _queue.Wait(_fence, values.CopiedBefore).CheckError();
            _queue.Wait(decoded, fenceValue).CheckError();
            _queue.ExecuteCommandList(_list);
            _queue.Signal(_fence, values.Drawn).CheckError();
        }
        catch (Exception ex)
        {
            // The draw may not signal, so the next one would wait forever: start again.
            MarkLost(ex, "Submitting the D3D12 draw failed");
            return false;
        }

        _frames++;

        var buffer = _buffers[index];
        var acquired = false;
        var copied = false;
        try
        {
            _context.Wait(_fenceOn11, values.Drawn);
            buffer.KeyedMutex.AcquireSync(0, 1000);
            acquired = true;
            _context.CopyResource(buffer.Texture, _drawnOn11);
            copied = true;
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            MarkLost(ex, "The D3D11 copy lost its device");
            return false;
        }
        finally
        {
            // Signalled on every path, so the next draw is not left waiting on this copy.
            TryDo(() => _context.Signal(_fenceOn11, values.Copied));
            TryDo(_context.Flush);
            // Released to 1 hands the buffer to the compositor; an aborted copy re-arms it at 0.
            if (acquired)
                TryDo(() => buffer.KeyedMutex.ReleaseSync(copied ? 1ul : 0ul));
        }

        return true;
    }

    /// <summary>
    /// Waits for the GPU to finish with everything the converter submitted, bounded, then releases
    /// the held frame and every D3D12 and D3D11 object. A device that neither finishes nor reports
    /// removal keeps its objects and the frame: releasing them under GPU work in flight would be a
    /// use-after-free on the device.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_frames > 0 && !WaitFor(BridgeFenceValues.For(_frames).Copied))
        {
            if (_device12.DeviceRemovedReason.Success)
            {
                _logger.LogWarning(
                    "D3D12 presenter converter disposed while its GPU work had not finished in {Limit}ms and the device "
                        + "reports no removal; leaving its objects and held frame unreleased rather than freeing them under it.",
                    (int)GpuWaitLimit.TotalMilliseconds);
                return;
            }
        }

        _held?.Dispose();
        _held = null;
        ReleaseOwned();
    }

    /// <summary>Releases the frame the last draw read, once that draw has finished.</summary>
    private bool Retire()
    {
        if (_held is null)
            return true;
        if (!WaitFor(_heldUntil))
            return false;
        _held.Dispose();
        _held = null;
        return true;
    }

    /// <summary>
    /// Waits on the CPU, bounded, for the shared fence to reach <paramref name="value"/>. False, with
    /// the converter marked lost, when the device was removed or did not get there in time.
    /// </summary>
    private bool WaitFor(ulong value)
    {
        ulong completed = _fence.CompletedValue;
        if (completed == ulong.MaxValue)
        {
            // What a removed device's fence reads. Its work is abandoned, so nothing reads the frame.
            MarkLost(null, "The D3D12 device was removed");
            return false;
        }

        if (completed >= value)
            return true;

        _fenceReached.Reset();
        _fence.SetEventOnCompletion(value, _fenceReached.SafeWaitHandle.DangerousGetHandle()).CheckError();
        if (_fenceReached.WaitOne(GpuWaitLimit) && _fence.CompletedValue != ulong.MaxValue)
            return true;

        MarkLost(null, $"The GPU did not reach fence value {value} within {(int)GpuWaitLimit.TotalMilliseconds}ms");
        return false;
    }

    private void MarkLost(Exception? ex, string what)
    {
        if (!_deviceLost)
            _logger.LogWarning(ex, "{What}; the D3D12 presenter converter will be rebuilt.", what);
        _deviceLost = true;
    }

    private void TryDo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            _deviceLost = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignored fault finishing a D3D12 presenter copy.");
        }
    }

    private void ReleaseOwned()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignored fault releasing a D3D12 presenter resource.");
            }
        }

        _owned.Clear();
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private static RootSignatureDescription1 RootSignature() => new(
        RootSignatureFlags.None,
        [
            new RootParameter1(
                new RootConstants { ShaderRegister = 0, RegisterSpace = 0, Num32BitValues = D3D12PresentConstants.Count },
                ShaderVisibility.Pixel),
            new RootParameter1(
                new RootDescriptorTable1(
                [
                    new DescriptorRange1
                    {
                        RangeType = DescriptorRangeType.ShaderResourceView,
                        NumDescriptors = 2,
                        BaseShaderRegister = 0,
                        RegisterSpace = 0,
                        OffsetInDescriptorsFromTableStart = 0,
                        Flags = DescriptorRangeFlags.None,
                    },
                ]),
                ShaderVisibility.Pixel),
        ],
        // Bilinear and clamped, as the D3D11 converter samples.
        [new StaticSamplerDescription(Vortice.Direct3D12.SamplerDescription.LinearClamp, ShaderVisibility.Pixel, 0, 0)]);

    private static ReadOnlyMemory<byte> Compile(string entryPoint, string profile) =>
        Compiler.Compile(Hlsl, entryPoint, "d3d12-present.hlsl", profile);

    private static Vortice.Direct3D12.ShaderResourceViewDescription PlaneView(Format format, uint plane) => new()
    {
        Format = format,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView { MipLevels = 1, PlaneSlice = plane },
    };

    private static Vortice.Luid Luid(long value) => new((uint)(value & 0xFFFFFFFF), (int)(value >> 32));

    private static nint Identity(nint unknown)
    {
        var iid = new Guid("00000000-0000-0000-c000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint identity));
        Marshal.Release(identity);
        return identity;
    }

    private static T Borrow<T>(nint pointer) where T : SharpGen.Runtime.ComObject
    {
        Marshal.AddRef(pointer);
        return (T)Activator.CreateInstance(typeof(T), pointer)!;
    }
}
