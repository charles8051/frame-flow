// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Media.Core;
using FrameFlow.Media.Diagnostics;
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
/// <b>Frame lifetime.</b> The GPU reads the decode texture after <see cref="ConvertInto(int, GpuVideoFrame)"/> returns,
/// so the converter holds a reference to the frame until the draw has finished. Up to
/// <see cref="InFlight"/> draws are outstanding, each with its own command list, descriptors and
/// frame. A call releases the frames whose draws have finished without waiting; when every draw
/// is still outstanding it drops the frame rather than wait on the UI thread. The sink's
/// <c>MaxHeldFrames</c> counts the held frames.
/// </para>
/// <para>
/// <b>The ring follows the view (#774).</b> The draw target, its D3D11 view and the ring are the
/// size the view draws the frame's visible part at, up to that part's coded size, and the shader
/// reads only that part (<see cref="SetSource"/>) with the D3D11 converter's area filter where the
/// ring is smaller than it. <see cref="TryResizeRing"/> replaces those three and keeps the devices,
/// the root signature, the pipeline state and the draw slots. The view decides the size with
/// <see cref="ViewSizedRingPolicy"/> and <see cref="RingSettle"/>, as it does for the D3D11
/// converter. A frame of another size still rebuilds the converter.
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
internal sealed unsafe class D3D12YuvSharedConverter : IViewSizedRing, IDisposable
{
    /// <summary>Number of shared BGRA textures in the ring, as the D3D11 converter has.</summary>
    public const int BufferCount = D3D11Nv12SharedConverter.BufferCount;

    /// <summary>Draws outstanding at once, each holding its frame until the GPU has read it.</summary>
    public const int InFlight = 2;

    // How long Dispose waits, off the UI thread, for the GPU to finish before it releases anything.
    private static readonly TimeSpan GpuWaitLimit = TimeSpan.FromSeconds(1);

    // The D3D11 converter's conversion (ADR-0063), with the sample levels, the frame's part of the
    // texture, the part of the frame the ring holds and the frame's matrix (#841) as root constants,
    // so NV12 and P010, and every matrix, share one pipeline.
    private const string Hlsl = """
        cbuffer Constants : register(b0)
        {
            float2 UvScale;                         // the frame's part of the texture
            float2 ChromaUvMax;                     // the frame's last chroma sample, in texture uv
            float YOffset, YScale, COffset, CScale; // YuvLevels
            float2 SourceOrigin, SourceSize;        // ShaderSampling: the part of the frame read,
            float2 Footprint, Unused;               // and the frame each ring pixel covers, in uv
            float RV, GU, GV, BU;                   // YcbcrCoefficients: the frame's matrix
        };

        // The most texels per axis the area filter averages: a 64:1 shrink. Past it the filter reads
        // the 64 nearest the ring pixel's centre.
        static const int MaxSpan = 64;

        Texture2D    Luma   : register(t0);   // R8_UNORM or R16_UNORM view of the Y plane
        Texture2D    Chroma : register(t1);   // R8G8_UNORM or R16G16_UNORM view of the UV plane
        SamplerState Samp   : register(s0);

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

        // One plane's value for the ring pixel centred at `centre`, in the frame's uv (#774), as the
        // D3D11 converter reads it (#767). Where the pixel covers at most a texel of the plane on each
        // axis, one bilinear sample, at most `uvMax`. Where it covers more, the area filter: every
        // texel of the frame under the pixel's footprint, weighted by how much of it the footprint
        // covers. The texture can be larger than the frame: the frame's texels are its first
        // UvScale * size, and the filter reads none past them.
        float4 Read(Texture2D plane, float2 centre, float2 uvMax)
        {
            float2 size;
            plane.GetDimensions(size.x, size.y);
            float2 frame = UvScale * size;
            float2 texels = Footprint * frame;
            if (max(texels.x, texels.y) <= 1.001)
                return plane.SampleLevel(Samp, min(centre * UvScale, uvMax), 0);

            float2 lo = centre * frame - texels * 0.5;
            float2 hi = lo + texels;
            int2 first = max((int2)floor(lo), 0);
            int2 last = min((int2)ceil(hi) - 1, (int2)ceil(frame) - 1);
            int2 skip = max(last - first + 1 - MaxSpan, 0) / 2;
            first += skip;
            last = min(last - skip, first + MaxSpan - 1);

            float4 sum = 0.0;
            float total = 0.0;
            [loop] for (int y = first.y; y <= last.y; y++)
            {
                float wy = min(hi.y, y + 1.0) - max(lo.y, (float)y);
                [loop] for (int x = first.x; x <= last.x; x++)
                {
                    float w = (min(hi.x, x + 1.0) - max(lo.x, (float)x)) * wy;
                    sum += plane.Load(int3(x, y, 0)) * w;
                    total += w;
                }
            }
            return sum / total;
        }

        float4 PSMain(VSOut input) : SV_Target
        {
            // The ring pixel's centre in the frame: the ring covers the source part only.
            float2 centre = SourceOrigin + input.uv * SourceSize;
            float  y      = Read(Luma, centre, float2(1.0, 1.0)).r;
            float2 c      = Read(Chroma, centre, ChromaUvMax).rg;

            float Y = (y   - YOffset) * YScale;
            float U = (c.x - COffset) * CScale;
            float V = (c.y - COffset) * CScale;

            // The frame's YCbCr-to-RGB matrix (YcbcrCoefficients).
            float3 rgb;
            rgb.r = Y +          RV * V;
            rgb.g = Y - GU * U - GV * V;
            rgb.b = Y + BU * U;
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
    private readonly DrawSlot[] _slots = new DrawSlot[InFlight];
    private readonly ID3D12Fence _fence;
    private readonly ManualResetEvent _fenceReached = new(false);

    // D3D11, the converter's own device on the same adapter.
    private readonly ID3D11Device1 _device11;
    private readonly ID3D11DeviceContext4 _context;
    private readonly ID3D11Fence _fenceOn11;

    // Sized by the ring: the draw target, its D3D11 view and the ring. Replaced by TryResizeRing.
    private Sized _sized;

    private ulong _frames;
    private int _nextSlot;
    private bool _deviceLost;
    private bool _disposed;

    /// <summary>One outstanding draw: its command list, its two plane descriptors, and its frame.</summary>
    private sealed class DrawSlot
    {
        public required ID3D12CommandAllocator Allocator;
        public required ID3D12GraphicsCommandList List;
        public required int FirstDescriptor;

        /// <summary>The frame the draw reads, until the fence reaches <see cref="Drawn"/>.</summary>
        public IDisposable? Frame;
        public ulong Drawn;
    }

    private sealed class RingBuffer
    {
        public required ID3D11Texture2D Texture;
        public required IDXGIKeyedMutex KeyedMutex;
        public nint SharedHandle;
    }

    /// <summary>
    /// The parts the ring's size fixes: the texture the shader draws into, which the D3D11 device
    /// copies from, that device's view of it, and the ring. Released in reverse creation order.
    /// </summary>
    private sealed class Sized(ILogger logger) : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public ID3D12Resource Drawn = null!;
        public ID3D11Texture2D DrawnOn11 = null!;
        public readonly RingBuffer[] Buffers = new RingBuffer[BufferCount];

        public T Own<T>(T disposable) where T : IDisposable
        {
            _owned.Add(disposable);
            return disposable;
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    _owned[i].Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Ignored fault releasing a D3D12 presenter ring.");
                }
            }

            _owned.Clear();
        }
    }

    /// <summary>
    /// The sized parts <see cref="TryResizeRing"/> replaced. The caller disposes it off the UI
    /// thread, after detaching the old ring's compositor imports. D3D12 does not keep a resource
    /// alive for work in flight, so it first waits, bounded, for the shared fence to reach the copy
    /// of the last frame drawn into them. It holds references of its own to the fence and its
    /// device, so it can outlive the converter.
    /// </summary>
    private sealed class Retired(Sized sized, ID3D12Device device, ID3D12Fence fence, ulong copied, ILogger logger)
        : IDisposable
    {
        public void Dispose()
        {
            try
            {
                using var reached = new ManualResetEvent(false);
                if (copied > 0 && !GpuFinished(device, fence, copied, reached, logger))
                {
                    logger.LogWarning(
                        "A D3D12 presenter ring was retired while its GPU work had not finished in {Limit}ms and the "
                            + "device reports no removal; leaving it unreleased rather than freeing it under that work.",
                        (int)GpuWaitLimit.TotalMilliseconds);
                    return;
                }

                sized.Dispose();
            }
            finally
            {
                fence.Dispose();
                device.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds the converter for <paramref name="width"/> by <paramref name="height"/> frames on the
    /// device <paramref name="texture"/>, a frame's <c>ID3D12Resource*</c>, belongs to, with a ring
    /// the frame's size.
    /// </summary>
    public D3D12YuvSharedConverter(nint texture, int width, int height, ILogger logger)
        : this(texture, width, height, new RingSize(width, height), logger)
    {
    }

    /// <summary>
    /// Builds the converter for <paramref name="width"/> by <paramref name="height"/> frames on the
    /// device <paramref name="texture"/>, a frame's <c>ID3D12Resource*</c>, belongs to, with a
    /// <paramref name="ring"/>-sized ring (#774) that holds the whole frame until
    /// <see cref="SetSource"/> crops it.
    /// </summary>
    public D3D12YuvSharedConverter(nint texture, int width, int height, RingSize ring, ILogger logger)
    {
        _logger = logger;
        Width = width;
        Height = height;
        Ring = ring;
        Source = SourceRect.Whole(width, height);

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
            _srvHeap = Own(_device12.CreateDescriptorHeap(
                new DescriptorHeapDescription(ViewHeap, 2 * InFlight, DescriptorHeapFlags.ShaderVisible)));
            _srvSize = _device12.GetDescriptorHandleIncrementSize(ViewHeap);
            _rtvHeap = Own(_device12.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1)));
            for (var i = 0; i < InFlight; i++)
            {
                var allocator = Own(_device12.CreateCommandAllocator(CommandListType.Direct));
                var list = Own(_device12.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator, _pipeline));
                list.Close();
                _slots[i] = new DrawSlot { Allocator = allocator, List = list, FirstDescriptor = 2 * i };
            }

            // The fence the two devices signal in turn, shared so the D3D11 device can open it.
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

            _device11 = Own(device11.QueryInterface<ID3D11Device1>());
            _context = Own(device11.ImmediateContext.QueryInterface<ID3D11DeviceContext4>());
            using (var device5 = device11.QueryInterface<ID3D11Device5>())
            using (var handle = new SafeFileHandle(_device12.CreateSharedHandle(_fence, null, Unnamed), ownsHandle: true))
                _fenceOn11 = Own(device5.OpenSharedFence<ID3D11Fence>(handle.DangerousGetHandle()));

            _sized = CreateSized(ring);
            _device12.CreateRenderTargetView(_sized.Drawn, null, _rtvHeap.GetCPUDescriptorHandleForHeapStart());
        }
        catch
        {
            _sized?.Dispose();
            ReleaseOwned();
            throw;
        }

        _logger.LogInformation(
            "D3D12 presenter converter ready (#429): {W}x{H} frames, pixel shader on the decoder's D3D12 device "
                + "0x{Device:X}, copied by an own D3D11 device into a {N}-buffer {RW}x{RH} shared keyed-mutex ring.",
            width, height, _deviceIdentity, BufferCount, ring.Width, ring.Height);
    }

    /// <summary>The frame's width.</summary>
    public int Width { get; }

    /// <summary>The frame's height.</summary>
    public int Height { get; }

    /// <summary>The ring's size, and the draw target's (#774).</summary>
    public RingSize Ring { get; private set; }

    /// <summary>
    /// The part of the frame the ring holds, in coded pixels (#774). The whole frame until
    /// <see cref="SetSource"/> crops it.
    /// </summary>
    public SourceRect Source { get; private set; }

    /// <summary>Always: the D3D12 converter has only the shader (#774).</summary>
    public bool RingFollowsView => true;

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
    public nint GetSharedHandle(int index) => _sized.Buffers[index].SharedHandle;

    /// <summary>The <c>IUnknown</c> identity of the device a frame's <c>ID3D12Resource*</c> belongs to.</summary>
    public static nint DeviceIdentity(nint texture)
    {
        using var resource = Borrow<ID3D12Resource>(texture);
        using var device = resource.GetDevice<ID3D12Device>();
        return Identity(device.NativePointer);
    }

    /// <summary>
    /// Converts <paramref name="frame"/> into ring buffer <paramref name="index"/>, whose previous
    /// present must have completed. Holds the frame until the GPU has read it. Never waits on the
    /// GPU.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the buffer is ready to present. <see langword="false"/> when a
    /// device was lost, in which case <see cref="IsDeviceLost"/> is set, or when every draw is still
    /// outstanding and the frame was dropped.
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

        return ConvertInto(index, textureHandle, fenceHandle, fenceValue, frame.AddRef, frame.Matrix);
    }

    /// <summary>
    /// Converts the frame in <paramref name="textureHandle"/>, an <c>ID3D12Resource*</c> written
    /// once <paramref name="fenceHandle"/> reaches <paramref name="fenceValue"/>, into ring buffer
    /// <paramref name="index"/>. <paramref name="hold"/> keeps the texture alive; the converter
    /// disposes what it returns once the GPU has read the texture. The frame converts with
    /// <paramref name="matrix"/> (#841). For tests: a texture of their own.
    /// </summary>
    internal bool ConvertInto(
        int index, nint textureHandle, nint fenceHandle, ulong fenceValue, Func<IDisposable> hold,
        YcbcrMatrix matrix = YcbcrMatrix.Bt709)
    {
        if (_deviceLost || _disposed)
            return false;

        if (!ReleaseFinished())
            return false;

        // The slot's list, descriptors and frame are reused below, so its last draw must be done.
        var slot = _slots[_nextSlot];
        if (slot.Frame is not null)
            return false;

        using var texture = Borrow<ID3D12Resource>(textureHandle);
        using var decoded = Borrow<ID3D12Fence>(fenceHandle);
        var description = texture.Description;
        var (samples, lumaFormat, chromaFormat) = YuvTextureFormats.For(description.Format);
        var constants = D3D12PresentConstants.Create(
            samples, (int)description.Width, (int)description.Height, Width, Height, Source, Ring, matrix);
        var sized = _sized;

        int descriptorOffset = slot.FirstDescriptor * (int)_srvSize;
        var srv = _srvHeap.GetCPUDescriptorHandleForHeapStart() + descriptorOffset;
        _device12.CreateShaderResourceView(texture, PlaneView(lumaFormat, 0), srv);
        _device12.CreateShaderResourceView(texture, PlaneView(chromaFormat, 1), srv + (int)_srvSize);

        var list = slot.List;
        slot.Allocator.Reset();
        list.Reset(slot.Allocator, _pipeline);
        list.SetGraphicsRootSignature(_rootSignature);
        list.SetDescriptorHeaps(_srvHeap);
        list.SetGraphicsRoot32BitConstants(0, D3D12PresentConstants.Count, &constants, 0);
        list.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart() + descriptorOffset);
        list.RSSetViewport(0, 0, Ring.Width, Ring.Height, 0, 1);
        list.RSSetScissorRect(Ring.Width, Ring.Height);
        list.ResourceBarrierTransition(sized.Drawn, ResourceStates.Common, ResourceStates.RenderTarget);
        list.OMSetRenderTargets(_rtvHeap.GetCPUDescriptorHandleForHeapStart(), null);
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        list.DrawInstanced(3, 1, 0, 0);
        // COMMON, so the D3D11 device, which tracks no D3D12 state, can read it.
        list.ResourceBarrierTransition(sized.Drawn, ResourceStates.RenderTarget, ResourceStates.Common);
        list.Close();

        var values = BridgeFenceValues.For(_frames + 1);

        // From here the GPU may read the frame, so hold it whatever happens next.
        slot.Frame = hold();
        slot.Drawn = values.Drawn;
        _nextSlot = (_nextSlot + 1) % InFlight;
        try
        {
            _queue.Wait(_fence, values.CopiedBefore).CheckError();
            _queue.Wait(decoded, fenceValue).CheckError();
            _queue.ExecuteCommandList(list);
            _queue.Signal(_fence, values.Drawn).CheckError();
            FrameCopyMetrics.Record(FrameCopySite.PresenterGpuConvert);
        }
        catch (Exception ex)
        {
            // The draw may not signal, so the next one would wait forever: start again.
            MarkLost(ex, "Submitting the D3D12 draw failed");
            return false;
        }

        _frames++;

        var buffer = sized.Buffers[index];
        var acquired = false;
        var copied = false;
        try
        {
            _context.Wait(_fenceOn11, values.Drawn);
            buffer.KeyedMutex.AcquireSync(0, 1000);
            acquired = true;
            _context.CopyResource(buffer.Texture, sized.DrawnOn11);
            copied = true;
            FrameCopyMetrics.Record(FrameCopySite.PresenterGpuCopy);
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
    /// Resizes the draw target and the ring for the size the view draws the frame at (#774). The
    /// devices, the root signature, the pipeline state and the draw slots are kept. The ring is new,
    /// so the caller imports it again.
    /// </summary>
    /// <returns>
    /// The old draw target and ring, for the caller to dispose off the UI thread once it has detached
    /// their compositor imports; the disposal waits for the GPU work that used them.
    /// <see langword="null"/> when the converter is lost or disposed, or allocating the new parts
    /// failed; the converter is then unchanged.
    /// </returns>
    public IDisposable? TryResizeRing(RingSize ring)
    {
        if (_disposed || _deviceLost)
            return null;

        long started = Stopwatch.GetTimestamp();
        Sized sized;
        try
        {
            // CA2000 is a false positive: the new parts become the converter's below.
#pragma warning disable CA2000
            sized = CreateSized(ring);
#pragma warning restore CA2000
        }
        catch (Exception ex)
        {
            if (D3D11DeviceLoss.IsDeviceLost(ex) || _fence.CompletedValue == ulong.MaxValue)
                MarkLost(ex, "Resizing the D3D12 presenter's ring lost a device");
            else
                _logger.LogWarning(ex, "D3D12 converter could not resize its ring to {W}x{H}; it keeps the old one.", ring.Width, ring.Height);
            return null;
        }

        // The draws recorded so far hold the old target's descriptor as it was: a render target view
        // is read when the list is recorded, not when it runs.
        var retired = new Retired(
            _sized,
            Borrow<ID3D12Device>(_device12.NativePointer),
            Borrow<ID3D12Fence>(_fence.NativePointer),
            _frames > 0 ? BridgeFenceValues.For(_frames).Copied : 0,
            _logger);
        var previous = Ring;
        _sized = sized;
        Ring = ring;
        _device12.CreateRenderTargetView(sized.Drawn, null, _rtvHeap.GetCPUDescriptorHandleForHeapStart());

        _logger.LogDebug(
            "D3D12 converter resized its ring from {OW}x{OH} to {W}x{H} for the view in {Ms:F1} ms ({FW}x{FH} frames).",
            previous.Width, previous.Height, ring.Width, ring.Height,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, Width, Height);
        return retired;
    }

    /// <summary>
    /// Sets the part of the frame the ring holds, in coded pixels (#774): the crop a filled view
    /// shows, or the whole frame. The shader reads only that part from the next conversion. An
    /// empty part reads the whole frame.
    /// </summary>
    public void SetSource(SourceRect source)
    {
        if (source.IsEmpty)
            source = SourceRect.Whole(Width, Height);
        Source = source;
    }

    /// <summary>
    /// Waits for the GPU to finish with everything the converter submitted, bounded, then releases
    /// the held frames and every D3D12 and D3D11 object. A device that neither finishes nor reports
    /// removal keeps its objects and frames: releasing them under GPU work in flight would be a
    /// use-after-free on the device.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_frames > 0 && !GpuFinished(_device12, _fence, BridgeFenceValues.For(_frames).Copied, _fenceReached, _logger))
        {
            _logger.LogWarning(
                "D3D12 presenter converter disposed while its GPU work had not finished in {Limit}ms and the device "
                    + "reports no removal; leaving its objects and held frames unreleased rather than freeing them under it.",
                (int)GpuWaitLimit.TotalMilliseconds);
            return;
        }

        foreach (var slot in _slots)
        {
            slot.Frame?.Dispose();
            slot.Frame = null;
        }

        _sized.Dispose();
        ReleaseOwned();
    }

    /// <summary>
    /// Releases the frames whose draws have finished, without waiting. False, with the converter
    /// marked lost, when the device was removed; its work is abandoned, so every frame goes.
    /// </summary>
    private bool ReleaseFinished()
    {
        ulong completed = _fence.CompletedValue;
        bool removed = completed == ulong.MaxValue;
        foreach (var slot in _slots)
        {
            if (slot.Frame is not null && slot.Drawn <= completed)
            {
                slot.Frame.Dispose();
                slot.Frame = null;
            }
        }

        if (removed)
            MarkLost(null, "The D3D12 device was removed");
        return !removed;
    }

    /// <summary>
    /// True once <paramref name="fence"/> reaches <paramref name="value"/>, waiting on the CPU for at
    /// most <see cref="GpuWaitLimit"/>, or once <paramref name="device"/> is removed, which abandons
    /// its work. Only disposals call it, off the UI thread.
    /// </summary>
    private static bool GpuFinished(ID3D12Device device, ID3D12Fence fence, ulong value, ManualResetEvent reached, ILogger logger)
    {
        try
        {
            // A removed device's fence reads ulong.MaxValue, which passes this.
            if (fence.CompletedValue >= value)
                return true;

            reached.Reset();
            fence.SetEventOnCompletion(value, reached.SafeWaitHandle.DangerousGetHandle()).CheckError();
            if (reached.WaitOne(GpuWaitLimit))
                return true;
        }
        catch (Exception ex)
        {
            // Removal between the read and the registration fails the registration.
            logger.LogDebug(ex, "Waiting for the D3D12 presenter's GPU work failed; checking for device removal.");
        }

        try
        {
            return device.DeviceRemovedReason.Failure || fence.CompletedValue == ulong.MaxValue;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading the D3D12 device's removal state failed; treating it as removed.");
            return true;
        }
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

    /// <summary>
    /// The draw target, shared so the D3D11 device can open it, that device's view of it, and the
    /// ring, all <paramref name="ring"/>-sized. Releases what it made when a step fails.
    /// </summary>
    private Sized CreateSized(RingSize ring)
    {
        var sized = new Sized(_logger);
        try
        {
            sized.Drawn = sized.Own(_device12.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.Shared,
                ResourceDescription.Texture2D(
                    Format.B8G8R8A8_UNorm, (uint)ring.Width, (uint)ring.Height, 1, 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.Common));
            using (var handle = new SafeFileHandle(_device12.CreateSharedHandle(sized.Drawn, null, Unnamed), ownsHandle: true))
                sized.DrawnOn11 = sized.Own(_device11.OpenSharedResource1<ID3D11Texture2D>(handle.DangerousGetHandle()));

            for (var i = 0; i < BufferCount; i++)
            {
                var texture = sized.Own(_device11.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)ring.Width,
                    Height = (uint)ring.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
                }));
                using var dxgi = texture.QueryInterface<IDXGIResource>();
                sized.Buffers[i] = new RingBuffer
                {
                    Texture = texture,
                    KeyedMutex = sized.Own(texture.QueryInterface<IDXGIKeyedMutex>()),
                    SharedHandle = dxgi.SharedHandle,
                };
            }

            return sized;
        }
        catch
        {
            sized.Dispose();
            throw;
        }
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
