using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanPresenterProbe;

/// <summary>One image of the ring the compositor imports, and what guards it.</summary>
internal sealed class RingSlot
{
    public Image Image;
    public DeviceMemory Memory;
    public ulong MemorySize;
    public ImageView View;
    public VkSemaphore RenderDone;       // binary: we signal, the compositor waits
    public VkSemaphore CompositorDone;   // binary: the compositor signals, we wait before reuse
    public Fence Fence;
    public CommandBuffer Commands;
    public DescriptorSet Descriptors;

    // Held until Fence signals: the frame whose image the command buffer read, and its plane views.
    public GpuVideoFrame? HeldFrame;
    public VkSemaphore HeldTimeline;
    public ulong HeldTimelineTarget;
    public ImageView LumaView, ChromaView;

    // 0 free, 1 queued for the UI thread, 2 handed to the compositor (LastPresent set).
    public int State;
    public bool CompositorSignalPending;
    public Task? LastPresent;

    // Imported once, on the UI thread.
    public object? ImportedImage, ImportedRenderDone, ImportedCompositorDone;
}

/// <summary>
/// Converts Vulkan-decoded NV12 frames to RGBA8 on the decoder's own device, into a ring of
/// images exported as opaque file descriptors.
/// </summary>
internal sealed unsafe class VulkanConverter : IDisposable
{
    public const int RingSize = 3;

    // Diagnostic: skip the wait on the compositor's release semaphore, to see whether it is the
    // dependency that stalls.
    public static readonly bool SkipCompositorWait = Environment.GetEnvironmentVariable("PROBE_NO_CD_WAIT") == "1";

    private readonly Vk _vk = Vk.GetApi();
    private readonly Action<string> _log;
    private readonly AVHWDeviceContext* _deviceCtx;
    private readonly AVVulkanDeviceContext* _vkDev;
    private readonly Device _device;
    private readonly PhysicalDevice _physical;
    private readonly uint _family;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly DescriptorPool _descriptorPool;
    private readonly delegate* unmanaged<Device, MemoryGetFdInfoKHR*, int*, Result> _getMemoryFd;
    private readonly delegate* unmanaged<Device, SemaphoreGetFdInfoKHR*, int*, Result> _getSemaphoreFd;

    public RingSlot[] Slots { get; } = new RingSlot[RingSize];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] DeviceUuid { get; } = new byte[16];

    public VulkanConverter(VulkanFrameRefs first, Action<string> log)
    {
        _log = log;
        _deviceCtx = first.DeviceContext;
        _vkDev = first.VulkanDevice;
        _vk.CurrentInstance = new Instance(_vkDev->inst);
        _device = new Device(_vkDev->act_dev);
        _vk.CurrentDevice = _device;
        _physical = new PhysicalDevice(_vkDev->phys_dev);

        var idProps = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
        var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &idProps };
        _vk.GetPhysicalDeviceProperties2(_physical, &props2);
        for (int i = 0; i < 16; i++)
            DeviceUuid[i] = idProps.DeviceUuid[i];
        _log($"decode device: {Marshal.PtrToStringUTF8((nint)props2.Properties.DeviceName)}, uuid {System.Convert.ToHexString(DeviceUuid)}");
        _log($"enabled device extensions: {string.Join(" ", FfmpegVulkan.EnabledDeviceExtensions(_vkDev))}");

        // A compute family FFmpeg created queues on. Its images are CONCURRENT across the families
        // it uses, so no ownership transfer is needed.
        _family = uint.MaxValue;
        for (int i = 0; i < _vkDev->nb_qf; i++)
        {
            var qf = _vkDev->Qf(i);
            _log($"queue family {qf.idx}: num {qf.num} flags 0x{qf.flags:x} video 0x{qf.video_caps:x}");
            if (_family == uint.MaxValue && (qf.flags & (uint)QueueFlags.ComputeBit) != 0)
                _family = (uint)qf.idx;
        }
        if (_family == uint.MaxValue)
            throw new InvalidOperationException("FFmpeg's device has no compute queue family.");
        _vk.GetDeviceQueue(_device, _family, 0, out _queue);
        _log($"converter queue: family {_family}, index 0");

        _getMemoryFd = (delegate* unmanaged<Device, MemoryGetFdInfoKHR*, int*, Result>)
            RequireProc("vkGetMemoryFdKHR");
        _getSemaphoreFd = (delegate* unmanaged<Device, SemaphoreGetFdInfoKHR*, int*, Result>)
            RequireProc("vkGetSemaphoreFdKHR");

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _family,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool), "vkCreateCommandPool");

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint b = 0; b < 3; b++)
        {
            bindings[b] = new DescriptorSetLayoutBinding
            {
                Binding = b,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, &setLayoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");

        var push = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = 16 };
        var setLayout = _setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push,
        };
        Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        var spirv = LoadShader();
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            Check(_vk.CreateShaderModule(_device, &moduleInfo, null, out module), "vkCreateShaderModule");
        }
        var entry = Marshal.StringToHGlobalAnsi("main");
        try
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = (byte*)entry,
                },
                Layout = _pipelineLayout,
            };
            Pipeline pipeline;
            Check(_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, &pipeline), "vkCreateComputePipelines");
            _pipeline = pipeline;
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            _vk.DestroyShaderModule(_device, module, null);
        }

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 3 * RingSize };
        var descriptorPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = RingSize,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Check(_vk.CreateDescriptorPool(_device, &descriptorPoolInfo, null, out _descriptorPool), "vkCreateDescriptorPool");
    }

    private nint RequireProc(string name)
    {
        var p = (nint)_vk.GetDeviceProcAddr(_device, name).Handle;
        return p != 0 ? p : throw new InvalidOperationException($"{name} is not available on FFmpeg's device.");
    }

    private static byte[] LoadShader()
    {
        using var s = typeof(VulkanConverter).Assembly.GetManifestResourceStream("nv12_to_rgba.spv")
            ?? throw new InvalidOperationException("nv12_to_rgba.spv is not embedded.");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Creates the ring at the frame's size. Once: this spike does not resize.</summary>
    public void CreateRing(int width, int height)
    {
        Width = width;
        Height = height;
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var memProps);

        for (int i = 0; i < RingSize; i++)
        {
            var slot = new RingSlot();

            var external = new ExternalMemoryImageCreateInfo
            {
                SType = StructureType.ExternalMemoryImageCreateInfo,
                HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
            };
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                PNext = &external,
                ImageType = ImageType.Type2D,
                Format = Format.R8G8B8A8Unorm,
                Extent = new Extent3D((uint)width, (uint)height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out slot.Image), "vkCreateImage");

            _vk.GetImageMemoryRequirements(_device, slot.Image, out var req);
            var dedicated = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo, Image = slot.Image };
            var export = new ExportMemoryAllocateInfo
            {
                SType = StructureType.ExportMemoryAllocateInfo,
                PNext = &dedicated,
                HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
            };
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &export,
                AllocationSize = req.Size,
                MemoryTypeIndex = MemoryType(memProps, req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &allocInfo, null, out slot.Memory), "vkAllocateMemory");
            Check(_vk.BindImageMemory(_device, slot.Image, slot.Memory, 0), "vkBindImageMemory");
            slot.MemorySize = req.Size;

            slot.View = CreateView(slot.Image, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit);

            var exportSem = new ExportSemaphoreCreateInfo
            {
                SType = StructureType.ExportSemaphoreCreateInfo,
                HandleTypes = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
            };
            var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &exportSem };
            Check(_vk.CreateSemaphore(_device, &semInfo, null, out slot.RenderDone), "vkCreateSemaphore");
            Check(_vk.CreateSemaphore(_device, &semInfo, null, out slot.CompositorDone), "vkCreateSemaphore");

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            Check(_vk.CreateFence(_device, &fenceInfo, null, out slot.Fence), "vkCreateFence");

            var cbInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(_vk.AllocateCommandBuffers(_device, &cbInfo, out slot.Commands), "vkAllocateCommandBuffers");

            var setLayout = _setLayout;
            var setInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(_vk.AllocateDescriptorSets(_device, &setInfo, out slot.Descriptors), "vkAllocateDescriptorSets");

            Slots[i] = slot;
        }

        _log($"ring: {RingSize} x {width}x{height} RGBA8, {Slots[0].MemorySize} bytes each");
    }

    private static uint MemoryType(PhysicalDeviceMemoryProperties props, uint bits, MemoryPropertyFlags flags)
    {
        for (int i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((bits & (1u << i)) != 0 && (props.MemoryTypes[i].PropertyFlags & flags) == flags)
                return (uint)i;
        }
        throw new InvalidOperationException("No device-local memory type for the ring image.");
    }

    private ImageView CreateView(Image image, Format format, ImageAspectFlags aspect)
    {
        // Restricted to STORAGE: a plane view's format supports it where the multi-planar one may not.
        var usage = new ImageViewUsageCreateInfo { SType = StructureType.ImageViewUsageCreateInfo, Usage = ImageUsageFlags.StorageBit };
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            PNext = &usage,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        Check(_vk.CreateImageView(_device, &info, null, out var view), "vkCreateImageView");
        return view;
    }

    /// <summary>Whether a slot's last conversion and presentation have both finished.</summary>
    public bool IsReusable(RingSlot slot) =>
        Volatile.Read(ref slot.State) switch
        {
            0 => true,
            // Only a successful update submitted the compositor's signal. A faulted one did not, so
            // waiting on its release semaphore would never return: the slot is retired.
            2 => slot.LastPresent is { IsCompletedSuccessfully: true } && _vk.GetFenceStatus(_device, slot.Fence) == Result.Success,
            _ => false,
        };

    /// <summary>
    /// Records and submits the conversion of <paramref name="frame"/> into <paramref name="slot"/>,
    /// following FFmpeg's rules for an AVVkFrame: lock it, wait on its timeline semaphore at
    /// sem_value, signal sem_value + 1, and write back the layout and access the barrier left.
    /// </summary>
    public void Convert(GpuVideoFrame frame, VulkanFrameRefs refs, RingSlot slot)
    {
        // The previous conversion into this slot is done (IsReusable checked the fence).
        ReleaseHeld(slot);
        var fence = slot.Fence;
        Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences");

        var vkf = refs.Frame;
        var decoded = new Image(vkf->img[0]);
        slot.LumaView = CreateView(decoded, Format.R8Unorm, ImageAspectFlags.Plane0Bit);
        slot.ChromaView = CreateView(decoded, Format.R8G8Unorm, ImageAspectFlags.Plane1Bit);
        slot.HeldFrame = frame;

        var imageInfos = stackalloc DescriptorImageInfo[3];
        imageInfos[0] = new DescriptorImageInfo { ImageView = slot.LumaView, ImageLayout = ImageLayout.General };
        imageInfos[1] = new DescriptorImageInfo { ImageView = slot.ChromaView, ImageLayout = ImageLayout.General };
        imageInfos[2] = new DescriptorImageInfo { ImageView = slot.View, ImageLayout = ImageLayout.General };
        var writes = stackalloc WriteDescriptorSet[3];
        for (uint b = 0; b < 3; b++)
        {
            writes[b] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = slot.Descriptors,
                DstBinding = b,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &imageInfos[b],
            };
        }
        _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);

        var framesCtx = refs.FramesContext;
        refs.VulkanFrames->lock_frame(framesCtx, vkf);
        try
        {
            var cb = slot.Commands;
            Check(_vk.ResetCommandBuffer(cb, 0), "vkResetCommandBuffer");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(_vk.BeginCommandBuffer(cb, &begin), "vkBeginCommandBuffer");

            var toRead = stackalloc ImageMemoryBarrier2[2];
            toRead[0] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = (AccessFlags2)vkf->access[0],
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                OldLayout = (ImageLayout)vkf->layout[0],
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = decoded,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            toRead[1] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageWriteBit,
                OldLayout = ImageLayout.Undefined,   // the compositor signals with no layout
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = slot.Image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 2, PImageMemoryBarriers = toRead };
            _vk.CmdPipelineBarrier2(cb, &dep);

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Compute, _pipeline);
            var set = slot.Descriptors;
            _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
            var pc = stackalloc int[4] { Width, Height, refs.Bt709 ? 1 : 0, refs.FullRange ? 1 : 0 };
            _vk.CmdPushConstants(cb, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 16, pc);
            _vk.CmdDispatch(cb, (uint)((Width + 15) / 16), (uint)((Height + 15) / 16), 1);

            // Avalonia's GL import waits with GL_LAYOUT_TRANSFER_SRC_EXT.
            var toPresent = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.AllCommandsBit,
                DstAccessMask = AccessFlags2.MemoryReadBit,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = slot.Image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            var dep2 = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &toPresent };
            _vk.CmdPipelineBarrier2(cb, &dep2);
            Check(_vk.EndCommandBuffer(cb), "vkEndCommandBuffer");

            ulong waitValue = vkf->sem_value[0];
            var waits = stackalloc SemaphoreSubmitInfo[2];
            uint waitCount = 0;
            waits[waitCount++] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = new VkSemaphore(vkf->sem[0]),
                Value = waitValue,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            if (slot.CompositorSignalPending && !SkipCompositorWait)
            {
                waits[waitCount++] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = slot.CompositorDone,
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }
            var signals = stackalloc SemaphoreSubmitInfo[2];
            signals[0] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = new VkSemaphore(vkf->sem[0]),
                Value = waitValue + 1,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            signals[1] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = slot.RenderDone,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            var cbInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = cb };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = waitCount,
                PWaitSemaphoreInfos = waits,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &cbInfo,
                SignalSemaphoreInfoCount = 2,
                PSignalSemaphoreInfos = signals,
            };

            _vkDev->lock_queue(_deviceCtx, _family, 0);
            Result submitted;
            try
            {
                submitted = _vk.QueueSubmit2(_queue, 1, &submit, slot.Fence);
            }
            finally
            {
                _vkDev->unlock_queue(_deviceCtx, _family, 0);
            }
            Check(submitted, "vkQueueSubmit2");

            vkf->sem_value[0] = waitValue + 1;
            slot.HeldTimeline = new VkSemaphore(vkf->sem[0]);
            slot.HeldTimelineTarget = waitValue + 1;
            vkf->layout[0] = (int)ImageLayout.General;
            vkf->access[0] = (ulong)AccessFlags2.ShaderStorageReadBit;
        }
        finally
        {
            refs.VulkanFrames->unlock_frame(framesCtx, vkf);
        }

        slot.CompositorSignalPending = false;
        Volatile.Write(ref slot.State, 1);
    }

    /// <summary>One line per slot, for the stall dump.</summary>
    public string Describe(RingSlot slot)
    {
        var fence = _vk.GetFenceStatus(_device, slot.Fence);
        string timeline = "none";
        if (slot.HeldTimeline.Handle != 0)
        {
            _vk.GetSemaphoreCounterValue(_device, slot.HeldTimeline, out var value);
            timeline = $"{value}/{slot.HeldTimelineTarget}";
        }
        return $"state {Volatile.Read(ref slot.State)} present {slot.LastPresent?.Status.ToString() ?? "none"} fence {fence} timeline {timeline} pending {slot.CompositorSignalPending}";
    }

    private void ReleaseHeld(RingSlot slot)
    {
        if (slot.LumaView.Handle != 0)
            _vk.DestroyImageView(_device, slot.LumaView, null);
        if (slot.ChromaView.Handle != 0)
            _vk.DestroyImageView(_device, slot.ChromaView, null);
        slot.LumaView = default;
        slot.ChromaView = default;
        slot.HeldFrame?.Dispose();
        slot.HeldFrame = null;
    }

    public int ExportMemoryFd(RingSlot slot)
    {
        var info = new MemoryGetFdInfoKHR
        {
            SType = StructureType.MemoryGetFDInfoKhr,
            Memory = slot.Memory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
        };
        int fd;
        Check(_getMemoryFd(_device, &info, &fd), "vkGetMemoryFdKHR");
        return fd;
    }

    public int ExportSemaphoreFd(VkSemaphore semaphore)
    {
        var info = new SemaphoreGetFdInfoKHR
        {
            SType = StructureType.SemaphoreGetFDInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
        };
        int fd;
        Check(_getSemaphoreFd(_device, &info, &fd), "vkGetSemaphoreFdKHR");
        return fd;
    }

    private static void Check(Result result, string call)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{call} returned {result}.");
    }

    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);
        foreach (var slot in Slots)
        {
            if (slot is null)
                continue;
            ReleaseHeld(slot);
            _vk.DestroyFence(_device, slot.Fence, null);
            _vk.DestroySemaphore(_device, slot.RenderDone, null);
            _vk.DestroySemaphore(_device, slot.CompositorDone, null);
            _vk.DestroyImageView(_device, slot.View, null);
            _vk.DestroyImage(_device, slot.Image, null);
            _vk.FreeMemory(_device, slot.Memory, null);
        }
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyPipeline(_device, _pipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        _vk.DestroyCommandPool(_device, _pool, null);
    }
}
