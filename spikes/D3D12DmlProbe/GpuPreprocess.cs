using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace D3D12DmlProbe;

/// <summary>
/// NV12 texture to YOLO's input tensor on the GPU: nearest luma and chroma, BT.601
/// limited range (what swscale assumes for an untagged stream, so the CPU path matches),
/// stretched to <c>size</c>, RGB planes in <c>[0, 1]</c>. It writes a D3D12 buffer that stays on
/// the GPU; <see cref="ReadBack"/> copies it out only for comparison.
/// </summary>
internal sealed class GpuPreprocess : IDisposable
{
    private const string Shader = """
        #define RS "RootConstants(num32BitConstants=8, b0), " \
                   "DescriptorTable(SRV(t0, numDescriptors=2)), " \
                   "UAV(u0), " \
                   "StaticSampler(s0, filter=FILTER_MIN_MAG_MIP_LINEAR, addressU=TEXTURE_ADDRESS_CLAMP, addressV=TEXTURE_ADDRESS_CLAMP)"

        cbuffer Params : register(b0)
        {
            uint FrameWidth;
            uint FrameHeight;
            uint TextureWidth;
            uint TextureHeight;
            uint OutWidth;
            uint OutHeight;
            float ScaleX;
            float ScaleY;
        };

        Texture2D<float> Luma : register(t0);
        Texture2D<float2> Chroma : register(t1);
        SamplerState Linear : register(s0);
        RWStructuredBuffer<float> Output : register(u0);

        [RootSignature(RS)]
        [numthreads(16, 16, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= OutWidth || id.y >= OutHeight)
                return;

            // The tensor pixel's centre, in frame pixels; the frame pixel it falls in.
            uint sx = min((uint)((id.x + 0.5) * ScaleX), FrameWidth - 1);
            uint sy = min((uint)((id.y + 0.5) * ScaleY), FrameHeight - 1);

            float y = Luma.Load(int3(sx, sy, 0));
            // The chroma sample whose 2x2 block the luma pixel is in. Of nearest, centred
            // bilinear and left-sited bilinear, nearest came closest to the CPU path.
            float2 c = Chroma.Load(int3(sx / 2, sy / 2, 0));

            float yl = (y - 16.0 / 255.0) * (255.0 / 219.0);
            float cb = (c.x - 128.0 / 255.0) * (255.0 / 224.0);
            float cr = (c.y - 128.0 / 255.0) * (255.0 / 224.0);

            uint plane = OutWidth * OutHeight;
            uint i = id.y * OutWidth + id.x;
            Output[i] = saturate(yl + 1.402 * cr);
            Output[plane + i] = saturate(yl - 0.344136 * cb - 0.714136 * cr);
            Output[2 * plane + i] = saturate(yl + 1.772 * cb);
        }
        """;

    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipeline;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly uint _descriptorSize;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _list;
    private readonly ID3D12Fence _fence;
    private readonly ID3D12Resource _readback;
    private readonly ID3D12Resource _upload;
    private ulong _fenceValue;

    /// <param name="shareable">
    /// Create the tensor buffer on a shared heap, so another API (CUDA) can import it through an
    /// NT handle.
    /// </param>
    public GpuPreprocess(ID3D12Device device, ID3D12CommandQueue queue, int size, bool shareable = false)
    {
        _device = device;
        _queue = queue;
        Size = size;

        ReadOnlyMemory<byte> bytecode = Compiler.Compile(Shader, "main", "nv12-to-tensor.hlsl", "cs_5_1");
        _rootSignature = CreateRootSignature(device, bytecode.Span);
        _pipeline = device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _rootSignature,
            ComputeShader = bytecode.ToArray(),
        });

        _heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
        _descriptorSize = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        _allocator = device.CreateCommandAllocator(CommandListType.Compute);
        _list = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Compute, _allocator, _pipeline);
        _list.Close();
        _fence = device.CreateFence(0);

        TensorBytes = 3L * size * size * sizeof(float);
        Tensor = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            shareable ? HeapFlags.Shared : HeapFlags.None,
            ResourceDescription.Buffer((ulong)TensorBytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        _readback = device.CreateCommittedResource(
            new HeapProperties(HeapType.Readback),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)TensorBytes),
            ResourceStates.CopyDest);
        _upload = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)TensorBytes),
            ResourceStates.GenericRead);
    }

    public int Size { get; }

    /// <summary>The tensor buffer, in <c>UNORDERED_ACCESS</c>, which DirectML reads as its input.</summary>
    public ID3D12Resource Tensor { get; }

    public long TensorBytes { get; }

    /// <summary>
    /// Records and submits the conversion. The queue waits on the frame's fence first, so the
    /// shader reads the texture only after the decoder has written it. Returns once the GPU is
    /// done, so the caller can time it.
    /// </summary>
    public void Run(ID3D12Resource texture, ID3D12Fence frameFence, ulong frameFenceValue, int frameWidth, int frameHeight)
    {
        var desc = texture.Description;
        var cpu = _heap.GetCPUDescriptorHandleForHeapStart();
        _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, PlaneSlice = 0 },
        }, cpu);
        _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, PlaneSlice = 1 },
        }, cpu + (int)_descriptorSize);

        _allocator.Reset();
        _list.Reset(_allocator, _pipeline);
        _list.SetComputeRootSignature(_rootSignature);
        _list.SetDescriptorHeaps(_heap);
        var constants = new Params(
            (uint)frameWidth, (uint)frameHeight, (uint)desc.Width, desc.Height,
            (uint)Size, (uint)Size, (float)frameWidth / Size, (float)frameHeight / Size);
        unsafe
        {
            _list.SetComputeRoot32BitConstants(0, 8, &constants, 0);
        }
        _list.SetComputeRootDescriptorTable(1, _heap.GetGPUDescriptorHandleForHeapStart());
        _list.SetComputeRootUnorderedAccessView(2, Tensor.GPUVirtualAddress);
        _list.Dispatch((uint)((Size + 15) / 16), (uint)((Size + 15) / 16), 1);
        _list.Close();

        _queue.Wait(frameFence, frameFenceValue);
        _queue.ExecuteCommandList(_list);
        WaitForGpu();
    }

    /// <summary>Copies the tensor out, for comparison with the CPU path only.</summary>
    public float[] ReadBack()
    {
        _allocator.Reset();
        _list.Reset(_allocator, null);
        _list.ResourceBarrierTransition(Tensor, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _list.CopyResource(_readback, Tensor);
        _list.ResourceBarrierTransition(Tensor, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        _list.Close();
        _queue.ExecuteCommandList(_list);
        WaitForGpu();

        var values = new float[TensorBytes / sizeof(float)];
        unsafe
        {
            void* mapped;
            _readback.Map(0, &mapped);
            new ReadOnlySpan<float>(mapped, values.Length).CopyTo(values);
            _readback.Unmap(0);
        }
        return values;
    }

    /// <summary>The root signature the shader declares, read from its bytecode.</summary>
    private static unsafe ID3D12RootSignature CreateRootSignature(ID3D12Device device, ReadOnlySpan<byte> bytecode)
    {
        // ID3D12Device::CreateRootSignature is vtable slot 16: IUnknown 0-2, ID3D12Object 3-6,
        // then GetNodeCount, CreateCommandQueue, CreateCommandAllocator, the two pipeline
        // creators, CreateCommandList, CheckFeatureSupport, CreateDescriptorHeap and
        // GetDescriptorHandleIncrementSize.
        nint* vtable = *(nint**)device.NativePointer;
        var create = (delegate* unmanaged<nint, uint, byte*, nuint, Guid*, nint*, int>)vtable[16];
        Guid iid = typeof(ID3D12RootSignature).GUID;
        nint rootSignature = 0;
        fixed (byte* blob = bytecode)
            Marshal.ThrowExceptionForHR(create(device.NativePointer, 0, blob, (nuint)bytecode.Length, &iid, &rootSignature));
        return new ID3D12RootSignature(rootSignature);
    }

    /// <summary>Writes <paramref name="values"/> into the tensor buffer, to test the binding alone.</summary>
    public unsafe void Upload(float[] values)
    {
        void* mapped;
        _upload.Map(0, &mapped);
        values.AsSpan().CopyTo(new Span<float>(mapped, values.Length));
        _upload.Unmap(0);

        _allocator.Reset();
        _list.Reset(_allocator, null);
        _list.ResourceBarrierTransition(Tensor, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
        _list.CopyResource(Tensor, _upload);
        _list.ResourceBarrierTransition(Tensor, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        _list.Close();
        _queue.ExecuteCommandList(_list);
        WaitForGpu();
    }

    private void WaitForGpu()
    {
        _queue.Signal(_fence, ++_fenceValue);
        if (_fence.CompletedValue < _fenceValue)
        {
            using var done = new ManualResetEvent(false);
            _fence.SetEventOnCompletion(_fenceValue, done.SafeWaitHandle.DangerousGetHandle());
            done.WaitOne();
        }
    }

    public void Dispose()
    {
        _readback.Dispose();
        _upload.Dispose();
        Tensor.Dispose();
        _fence.Dispose();
        _list.Dispose();
        _allocator.Dispose();
        _heap.Dispose();
        _pipeline.Dispose();
        _rootSignature.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Params(
        uint FrameWidth,
        uint FrameHeight,
        uint TextureWidth,
        uint TextureHeight,
        uint OutWidth,
        uint OutHeight,
        float ScaleX,
        float ScaleY);
}
