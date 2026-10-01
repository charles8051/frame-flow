// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding;

/// <summary>
/// The Vulkan device FFmpeg decodes on (#535), from <see cref="GpuVideoFrame.TryGetVulkanDevice"/>
/// or <see cref="HardwareDevice.TryGetVulkanDevice"/>: the instance, physical device and device a
/// consumer builds its own objects on, the queue families FFmpeg created queues on, and the lock
/// those queues need.
/// </summary>
/// <remarks>
/// <para>
/// This object holds a reference to FFmpeg's device context, so the handles stay valid until it is
/// disposed, after the frame or device it came from if need be. Dispose it after every Vulkan
/// object built on <see cref="Device"/>.
/// </para>
/// <para>
/// A submission to one of FFmpeg's queues goes inside <see cref="LockQueue"/>, as FFmpeg's own do.
/// Load functions through <see cref="GetInstanceProcAddr"/>, which is the loader FFmpeg used.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDevice : IDisposable
{
    // Guards _deviceCtxRef between reading it and taking a new reference from it, against Dispose.
    private readonly Lock _gate = new();
    private nint _deviceCtxRef;

    /// <param name="deviceCtxRef">An <c>AVBufferRef*</c> to a Vulkan device context, which this object now owns.</param>
    internal VulkanDevice(nint deviceCtxRef)
    {
        _deviceCtxRef = deviceCtxRef;
        var vk = Context(deviceCtxRef);

        Instance = vk->inst;
        PhysicalDevice = vk->phys_dev;
        Device = vk->act_dev;
        GetInstanceProcAddr = vk->get_proc_addr;
        EnabledInstanceExtensions = Strings(vk->enabled_inst_extensions, vk->nb_enabled_inst_extensions);
        EnabledDeviceExtensions = Strings(vk->enabled_dev_extensions, vk->nb_enabled_dev_extensions);

        var families = new VulkanQueueFamily[Math.Clamp(vk->nb_qf, 0, AVVulkanDeviceContext.MaxQueueFamilies)];
        for (int i = 0; i < families.Length; i++)
        {
            var qf = vk->QueueFamily(i);
            families[i] = new VulkanQueueFamily((uint)qf.idx, qf.num, qf.flags, qf.video_caps);
        }
        QueueFamilies = families;
    }

    /// <summary><c>VkInstance</c>.</summary>
    public nint Instance { get; }

    /// <summary><c>VkPhysicalDevice</c>.</summary>
    public nint PhysicalDevice { get; }

    /// <summary><c>VkDevice</c>.</summary>
    public nint Device { get; }

    /// <summary><c>PFN_vkGetInstanceProcAddr</c> FFmpeg loaded its functions through.</summary>
    public nint GetInstanceProcAddr { get; }

    /// <summary>The queue families FFmpeg created queues on, in its order of preference.</summary>
    public IReadOnlyList<VulkanQueueFamily> QueueFamilies { get; }

    /// <summary>The instance extensions FFmpeg enabled.</summary>
    public IReadOnlyList<string> EnabledInstanceExtensions { get; }

    /// <summary>
    /// The device extensions FFmpeg enabled. Exporting memory and semaphores needs
    /// <c>VK_KHR_external_memory_fd</c> and <c>VK_KHR_external_semaphore_fd</c>, which FFmpeg
    /// enables when the driver has them.
    /// </summary>
    public IReadOnlyList<string> EnabledDeviceExtensions { get; }

    /// <summary>
    /// Locks one of FFmpeg's queues against other threads' submissions, until the returned lock is
    /// disposed. Submit inside it, and dispose it straight after, without waiting for the work.
    /// </summary>
    /// <param name="family">A queue family in <see cref="QueueFamilies"/>.</param>
    /// <param name="index">A queue index below that family's <see cref="VulkanQueueFamily.QueueCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">FFmpeg created no such queue.</exception>
    /// <exception cref="InvalidOperationException">FFmpeg's device has no queue lock callbacks.</exception>
    /// <exception cref="ObjectDisposedException">This object is disposed.</exception>
    public VulkanQueueLock LockQueue(uint family, uint index)
    {
        bool known = false;
        foreach (var qf in QueueFamilies)
            known |= qf.Index == family && index < (uint)qf.QueueCount;
        if (!known)
        {
            throw new ArgumentOutOfRangeException(
                nameof(family), $"FFmpeg created no queue {index} in family {family}.");
        }

        // The lock holds its own reference, so disposing this object first cannot free the
        // context before the queue is unlocked.
        nint lockRef;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deviceCtxRef == nint.Zero, this);
            lockRef = FFAvUtil.av_buffer_ref(_deviceCtxRef);
        }
        if (lockRef == nint.Zero)
            throw new InvalidOperationException("av_buffer_ref returned null (out of memory).");

        // FFmpeg 9.0 fills both in at init, as no-ops where the driver synchronises its queues.
        var deviceCtx = (AVHWDeviceContext*)((AVBufferRef*)lockRef)->data;
        var vk = (AVVulkanDeviceContext*)deviceCtx->hwctx;
        if (vk->lock_queue == null || vk->unlock_queue == null)
        {
            FFAvUtil.av_buffer_unref(ref lockRef);
            throw new InvalidOperationException("FFmpeg's device has no queue lock callbacks.");
        }

        vk->lock_queue(deviceCtx, family, index);
        return new VulkanQueueLock(lockRef, family, index);
    }

    /// <summary>Drops this object's reference to FFmpeg's device context.</summary>
    public void Dispose()
    {
        nint deviceCtxRef;
        lock (_gate)
        {
            deviceCtxRef = _deviceCtxRef;
            _deviceCtxRef = nint.Zero;
        }
        if (deviceCtxRef != nint.Zero)
            FFAvUtil.av_buffer_unref(ref deviceCtxRef);
    }

    private static AVVulkanDeviceContext* Context(nint deviceCtxRef) =>
        (AVVulkanDeviceContext*)((AVHWDeviceContext*)((AVBufferRef*)deviceCtxRef)->data)->hwctx;

    private static string[] Strings(byte** names, int count)
    {
        var result = new string[names == null ? 0 : Math.Max(count, 0)];
        for (int i = 0; i < result.Length; i++)
            result[i] = Marshal.PtrToStringUTF8((nint)names[i]) ?? "";
        return result;
    }
}

/// <summary>A queue family FFmpeg created queues on, from <see cref="VulkanDevice.QueueFamilies"/>.</summary>
/// <param name="Index">The queue family index.</param>
/// <param name="QueueCount">How many of the family's queues FFmpeg uses, from index 0.</param>
/// <param name="Flags">The <c>VkQueueFlags</c> FFmpeg uses the family for.</param>
/// <param name="VideoCodecOperations">The <c>VkVideoCodecOperationFlagsKHR</c> its queues support; zero for a family that does no video.</param>
public readonly record struct VulkanQueueFamily(uint Index, int QueueCount, uint Flags, uint VideoCodecOperations);

/// <summary>
/// One of FFmpeg's queues, locked by <see cref="VulkanDevice.LockQueue"/> until this is disposed.
/// It holds its own reference to the device context, so it can be disposed after the
/// <see cref="VulkanDevice"/> it came from.
/// </summary>
public sealed unsafe class VulkanQueueLock : IDisposable
{
    private nint _deviceCtxRef;
    private readonly uint _family;
    private readonly uint _index;

    /// <param name="deviceCtxRef">An <c>AVBufferRef*</c> this lock owns, to a context whose queue is locked.</param>
    internal VulkanQueueLock(nint deviceCtxRef, uint family, uint index)
    {
        _deviceCtxRef = deviceCtxRef;
        _family = family;
        _index = index;
    }

    /// <summary>Unlocks the queue and drops the lock's reference. A second call does nothing.</summary>
    public void Dispose()
    {
        nint deviceCtxRef = Interlocked.Exchange(ref _deviceCtxRef, nint.Zero);
        if (deviceCtxRef == nint.Zero)
            return;

        var deviceCtx = (AVHWDeviceContext*)((AVBufferRef*)deviceCtxRef)->data;
        ((AVVulkanDeviceContext*)deviceCtx->hwctx)->unlock_queue(deviceCtx, _family, _index);
        FFAvUtil.av_buffer_unref(ref deviceCtxRef);
    }
}
