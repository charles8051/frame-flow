// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Inference.D3D12.Core;
using FrameFlow.Media;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Inference.D3D12;

/// <summary>
/// The device side of <see cref="ImageToTensor"/>: a D3D12VA frame becomes a model's input tensor
/// in a D3D12 buffer, on the GPU. One compute shader crops, fits, samples, converts from YUV,
/// normalizes and lays out, as <see cref="ImageToTensorOptions"/> says, and the frame is never
/// downloaded (decision 1 in <c>docs/feature-specs/gpu-resident-inference/adr.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Build it on the frame's own device, from <c>ID3D12DeviceChild::GetDevice</c> on the texture
/// <see cref="GpuVideoFrame.TryGetD3D12Texture"/> gives, and on a compute or direct queue of that
/// device. Each <see cref="Write"/> makes the queue wait on the frame's decode fence on the GPU,
/// dispatches the shader into <see cref="Tensor"/>, and signals <see cref="CompletionFence"/> to
/// <see cref="CompletionValue"/>.
/// </para>
/// <para>
/// Work later on the same queue, such as a DirectML session built on it, sees the tensor with no
/// further wait. A consumer on another queue or API waits on the completion fence. There is one
/// tensor buffer, so a write overwrites the last one: order the next write after the consumer.
/// </para>
/// <para>
/// A write holds a reference to its frame until the GPU has read it, so the caller may dispose the
/// frame straight after. Up to three writes are in flight; a fourth waits on the CPU for the
/// first. Not thread-safe.
/// </para>
/// <para>
/// This object holds references to the frame's device. Build it on a
/// <see cref="HardwareDevice"/> the decoders borrow, from <see cref="HardwareDevice.TryGetD3D12Device"/>,
/// so the device outlives them. Built on a device taken from a frame instead, with a TensorRT-RTX
/// session loaded, it has to be disposed before the decoder's last frame is freed (#422).
/// </para>
/// </remarks>
public sealed unsafe class D3D12ImageToTensor : IDisposable
{
    private const int InFlight = 3;

    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipeline;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly uint _descriptorSize;
    private readonly ID3D12Fence _fence;
    private readonly Slot[] _slots = new Slot[InFlight];
    private readonly ManualResetEvent _completed = new(false);
    private ID3D12Resource? _readback;
    private int _next;
    private bool _disposed;

    /// <summary>Builds the stage on <paramref name="device"/> and <paramref name="commandQueue"/>.</summary>
    /// <param name="device">An <c>ID3D12Device*</c>: the device the frames are decoded on.</param>
    /// <param name="commandQueue">An <c>ID3D12CommandQueue*</c> of type compute or direct on that device.</param>
    /// <param name="options">
    /// The tensor and how to fill it, including the frames' <see cref="ImageToTensorOptions.YuvMatrix"/> and
    /// <see cref="ImageToTensorOptions.YuvRange"/>.
    /// </param>
    public D3D12ImageToTensor(
        nint device,
        nint commandQueue,
        ImageToTensorOptions options)
    {
        ImageToTensor.ValidateOptions(options);
        if (device == 0)
            throw new ArgumentNullException(nameof(device));
        if (commandQueue == 0)
            throw new ArgumentNullException(nameof(commandQueue));

        Options = options;
        _device = Borrow<ID3D12Device>(device);
        _queue = Borrow<ID3D12CommandQueue>(commandQueue);
        var listType = _queue.GetDescription().Type;
        if (listType is not (CommandListType.Compute or CommandListType.Direct))
        {
            throw new ArgumentException(
                $"The queue must be a compute or direct queue; it is {listType}.", nameof(commandQueue));
        }

        ReadOnlyMemory<byte> bytecode = Compiler.Compile(
            ImageToTensorShader.Source, ImageToTensorShader.EntryPoint, "image-to-tensor.hlsl", ImageToTensorShader.Profile);
        _rootSignature = CreateRootSignature(_device, bytecode.Span);
        _pipeline = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _rootSignature,
            ComputeShader = bytecode.ToArray(),
        });

        const DescriptorHeapType ViewHeap = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        _heap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(ViewHeap, 2 * InFlight, DescriptorHeapFlags.ShaderVisible));
        _descriptorSize = _device.GetDescriptorHandleIncrementSize(ViewHeap);
        _fence = _device.CreateFence(0);
        for (int i = 0; i < InFlight; i++)
        {
            var allocator = _device.CreateCommandAllocator(listType);
            var list = _device.CreateCommandList<ID3D12GraphicsCommandList>(listType, allocator, _pipeline);
            list.Close();
            _slots[i] = new Slot(allocator, list);
        }

        TensorBytes = (long)options.ElementCount * sizeof(float);
        // A shared heap, so a consumer on another API (CUDA, for TensorRT-RTX) can import it.
        Tensor = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.Shared,
            ResourceDescription.Buffer((ulong)TensorBytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess).NativePointer;
        TensorResource = new ID3D12Resource(Tensor);
    }

    /// <summary>The tensor and how it is filled.</summary>
    public ImageToTensorOptions Options { get; }

    /// <summary>
    /// The <c>ID3D12Resource*</c> the tensor is written into: a buffer of
    /// <see cref="ImageToTensorOptions.ElementCount"/> floats on a shared heap, in
    /// <c>UNORDERED_ACCESS</c>. Owned by this object.
    /// </summary>
    public nint Tensor { get; }

    /// <summary>The tensor's size in bytes.</summary>
    public long TensorBytes { get; }

    /// <summary>The <c>ID3D12Fence*</c> signalled after each write. Owned by this object.</summary>
    public nint CompletionFence => _fence.NativePointer;

    /// <summary>The value <see cref="CompletionFence"/> reaches once the last write is on the tensor.</summary>
    public ulong CompletionValue { get; private set; }

    /// <summary>
    /// The tensor as an <see cref="IDeviceInputSession"/> takes it: <see cref="Tensor"/> on this
    /// stage's device, shaped by <see cref="Options"/>' layout with a batch of one, and ready once
    /// <see cref="CompletionFence"/> reaches <see cref="CompletionValue"/>. It describes the last
    /// <see cref="Write"/>; the next one overwrites the buffer, so run the session first.
    /// </summary>
    public DeviceTensor DeviceTensor => new(
        DeviceTensorKind.D3D12,
        Tensor,
        _device.NativePointer,
        Options.Layout == TensorLayout.Nhwc
            ? new TensorShape(1, Options.Height, Options.Width, 3)
            : new TensorShape(1, 3, Options.Height, Options.Width),
        DType.Float32,
        CompletionFence,
        CompletionValue);

    private ID3D12Resource TensorResource { get; }

    /// <summary>
    /// Writes <paramref name="crop"/> of <paramref name="frame"/> into <see cref="Tensor"/>. It
    /// submits the work and returns; <see cref="CompletionValue"/> says when it is done.
    /// </summary>
    /// <returns>The mapping from tensor coordinates back to frame pixels, as the CPU stage returns.</returns>
    /// <exception cref="ArgumentException">
    /// The frame is not D3D12VA, or the crop is not finite or has no area.
    /// </exception>
    /// <exception cref="NotSupportedException">The frame is neither NV12 nor P010, or sits in a texture array.</exception>
    public TensorTransform Write(GpuVideoFrame frame, RotatedRect crop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ImageToTensor.Validate(crop, Options);
        if (!frame.TryGetD3D12Texture(out nint textureHandle, out int subresource, out nint fenceHandle, out ulong fenceValue))
            throw new ArgumentException($"A {frame.Backend} frame has no D3D12 texture.", nameof(frame));
        if (subresource != 0)
            throw new NotSupportedException("A frame in a texture array is not supported.");

        using var texture = Borrow<ID3D12Resource>(textureHandle);
        using var frameFence = Borrow<ID3D12Fence>(fenceHandle);
        var (lumaFormat, chromaFormat, samples) = texture.Description.Format switch
        {
            Format.NV12 => (Format.R8_UNorm, Format.R8G8_UNorm, YuvSamples.Nv12),
            Format.P010 => (Format.R16_UNorm, Format.R16G16_UNorm, YuvSamples.P010),
            var other => throw new NotSupportedException($"The frame's texture is {other}; NV12 and P010 are supported."),
        };

        var plan = ImageToTensorPlan.Create(crop, Options.Width, Options.Height, Options.Fit);
        var constants = KernelConstants.Create(plan, Options, samples, frame.Width, frame.Height);

        int index = _next;
        _next = (_next + 1) % InFlight;
        var slot = _slots[index];
        Retire(slot);

        var cpuHandle = _heap.GetCPUDescriptorHandleForHeapStart() + (int)(2 * index * _descriptorSize);
        _device.CreateShaderResourceView(texture, PlaneView(lumaFormat, 0), cpuHandle);
        _device.CreateShaderResourceView(texture, PlaneView(chromaFormat, 1), cpuHandle + (int)_descriptorSize);

        slot.Allocator.Reset();
        slot.List.Reset(slot.Allocator, _pipeline);
        slot.List.SetComputeRootSignature(_rootSignature);
        slot.List.SetDescriptorHeaps(_heap);
        slot.List.SetComputeRoot32BitConstants(0, KernelConstants.Count, &constants, 0);
        slot.List.SetComputeRootDescriptorTable(1, _heap.GetGPUDescriptorHandleForHeapStart() + (int)(2 * index * _descriptorSize));
        slot.List.SetComputeRootUnorderedAccessView(2, TensorResource.GPUVirtualAddress);
        // The last write, and whatever read it since, finish with the tensor before this one starts.
        slot.List.ResourceBarrierUnorderedAccessView(TensorResource);
        uint groupsX = (uint)((Options.Width + ImageToTensorShader.GroupSize - 1) / ImageToTensorShader.GroupSize);
        uint groupsY = (uint)((Options.Height + ImageToTensorShader.GroupSize - 1) / ImageToTensorShader.GroupSize);
        slot.List.Dispatch(groupsX, groupsY, 1);
        slot.List.Close();

        // The decoder writes on its own queue; the shader reads only once the decode fence passes.
        _queue.Wait(frameFence, fenceValue);
        _queue.ExecuteCommandList(slot.List);
        _queue.Signal(_fence, ++CompletionValue);

        slot.Value = CompletionValue;
        slot.Frame = frame.AddRef();
        return plan.Transform;
    }

    /// <summary>Waits on the CPU until the last write is on the tensor, and releases the frames it held.</summary>
    public void WaitForCompletion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var slot in _slots)
            Retire(slot);
    }

    /// <summary>Copies the tensor to the CPU. For tests and diagnostics; it waits for the GPU.</summary>
    internal float[] ReadBack()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readback ??= _device.CreateCommittedResource(
            new HeapProperties(HeapType.Readback),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)TensorBytes),
            ResourceStates.CopyDest);

        int index = _next;
        _next = (_next + 1) % InFlight;
        var slot = _slots[index];
        Retire(slot);
        slot.Allocator.Reset();
        slot.List.Reset(slot.Allocator, null);
        slot.List.ResourceBarrierTransition(TensorResource, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        slot.List.CopyResource(_readback, TensorResource);
        slot.List.ResourceBarrierTransition(TensorResource, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        slot.List.Close();
        _queue.ExecuteCommandList(slot.List);
        _queue.Signal(_fence, ++CompletionValue);
        slot.Value = CompletionValue;
        Retire(slot);

        var values = new float[TensorBytes / sizeof(float)];
        void* mapped;
        _readback.Map(0, &mapped);
        new ReadOnlySpan<float>(mapped, values.Length).CopyTo(values);
        _readback.Unmap(0);
        return values;
    }

    /// <summary>Waits for every write in flight, releases the frames, and releases the D3D12 objects.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var slot in _slots)
        {
            Retire(slot);
            slot.List.Dispose();
            slot.Allocator.Dispose();
        }
        _disposed = true;
        _readback?.Dispose();
        TensorResource.Dispose();
        _fence.Dispose();
        _heap.Dispose();
        _pipeline.Dispose();
        _rootSignature.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _completed.Dispose();
    }

    /// <summary>Waits until the slot's last write is done, then releases the frame it held.</summary>
    private void Retire(Slot slot)
    {
        if (slot.Value > _fence.CompletedValue)
        {
            _completed.Reset();
            _fence.SetEventOnCompletion(slot.Value, _completed.SafeWaitHandle.DangerousGetHandle());
            _completed.WaitOne();
        }

        slot.Frame?.Dispose();
        slot.Frame = null;
    }

    private static ShaderResourceViewDescription PlaneView(Format format, uint plane) => new()
    {
        Format = format,
        ViewDimension = ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1, PlaneSlice = plane },
    };

    /// <summary>The root signature the shader declares, read from its bytecode.</summary>
    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device, ReadOnlySpan<byte> bytecode)
    {
        // ID3D12Device::CreateRootSignature is vtable slot 16: IUnknown 0-2, ID3D12Object 3-6, then
        // GetNodeCount, CreateCommandQueue, CreateCommandAllocator, the two pipeline creators,
        // CreateCommandList, CheckFeatureSupport, CreateDescriptorHeap and
        // GetDescriptorHandleIncrementSize.
        nint* vtable = *(nint**)device.NativePointer;
        var create = (delegate* unmanaged<nint, uint, byte*, nuint, Guid*, nint*, int>)vtable[16];
        Guid iid = typeof(ID3D12RootSignature).GUID;
        nint rootSignature = 0;
        fixed (byte* blob = bytecode)
            Marshal.ThrowExceptionForHR(create(device.NativePointer, 0, blob, (nuint)bytecode.Length, &iid, &rootSignature));
        return new ID3D12RootSignature(rootSignature);
    }

    /// <summary>A wrapper that owns its own reference to a borrowed COM pointer.</summary>
    private static T Borrow<T>(nint pointer) where T : SharpGen.Runtime.ComObject
    {
        Marshal.AddRef(pointer);
        return (T)Activator.CreateInstance(typeof(T), pointer)!;
    }

    private sealed class Slot(ID3D12CommandAllocator allocator, ID3D12GraphicsCommandList list)
    {
        public ID3D12CommandAllocator Allocator { get; } = allocator;

        public ID3D12GraphicsCommandList List { get; } = list;

        /// <summary>The completion value of the slot's last submission.</summary>
        public ulong Value { get; set; }

        /// <summary>The frame the slot's last write reads, held until the GPU is done with it.</summary>
        public IVideoFrame? Frame { get; set; }
    }
}
