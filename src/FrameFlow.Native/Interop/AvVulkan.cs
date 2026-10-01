// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Interop;

// FFmpeg.AutoGen does not generate the Vulkan hwcontext types, so these mirror
// libavutil/hwcontext_vulkan.h from FFmpeg 9.0 (avutil 61). FF_API_VULKAN_SYNC_QUEUES is still on
// in avutil 61, so AVVulkanDeviceContext carries the deprecated lock_queue and unlock_queue. The
// layouts are the 64-bit ones, which x64 and arm64 share and every runtime FrameFlow ships uses;
// the accessors refuse a 32-bit process. AvVulkanLayoutTests checks the offsets; a new FFmpeg
// major has to be read against the header again.

/// <summary>FFmpeg's <c>AVVulkanDeviceQueueFamily</c>: one queue family the device created queues on.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVVulkanDeviceQueueFamily
{
    /// <summary>The queue family index.</summary>
    public int idx;

    /// <summary>How many of the family's queues the device uses.</summary>
    public int num;

    /// <summary><c>VkQueueFlagBits</c> this family is used for.</summary>
    public uint flags;

    /// <summary><c>VkVideoCodecOperationFlagBitsKHR</c> the family's queues support.</summary>
    public uint video_caps;
}

/// <summary>
/// FFmpeg's <c>AVVulkanDeviceContext</c>: what <c>AVHWDeviceContext.hwctx</c> points to for a
/// Vulkan device. Only the fields FrameFlow reads are named; <see cref="device_features"/> is a
/// <c>VkPhysicalDeviceFeatures2</c> kept as bytes.
/// </summary>
/// <remarks>
/// x64 layout: <c>inst</c> at 16, <c>act_dev</c> at 32, <c>device_features</c> at 40 (240 bytes),
/// <c>enabled_inst_extensions</c> at 280, <c>enabled_dev_extensions</c> at 296, <c>lock_queue</c>
/// at 312, <c>qf</c> at 328 (64 of 16 bytes), <c>nb_qf</c> at 1352.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVVulkanDeviceContext
{
    public const int MaxQueueFamilies = 64;

    /// <summary><c>const VkAllocationCallbacks*</c>.</summary>
    public nint alloc;

    /// <summary><c>PFN_vkGetInstanceProcAddr</c>.</summary>
    public nint get_proc_addr;

    /// <summary><c>VkInstance</c>.</summary>
    public nint inst;

    /// <summary><c>VkPhysicalDevice</c>.</summary>
    public nint phys_dev;

    /// <summary><c>VkDevice</c>.</summary>
    public nint act_dev;

    /// <summary><c>VkPhysicalDeviceFeatures2</c>: sType, pNext and 55 <c>VkBool32</c>, padded to 8.</summary>
    public fixed byte device_features[240];

    /// <summary><c>const char* const*</c>.</summary>
    public byte** enabled_inst_extensions;

    public int nb_enabled_inst_extensions;

    /// <summary><c>const char* const*</c>.</summary>
    public byte** enabled_dev_extensions;

    public int nb_enabled_dev_extensions;

    /// <summary>Locks a queue against other threads' submissions. Deprecated in favour of internally synchronized queues.</summary>
    public delegate* unmanaged<AVHWDeviceContext*, uint, uint, void> lock_queue;

    /// <summary>Unlocks a queue <see cref="lock_queue"/> locked.</summary>
    public delegate* unmanaged<AVHWDeviceContext*, uint, uint, void> unlock_queue;

    /// <summary><c>AVVulkanDeviceQueueFamily qf[64]</c>; the first <see cref="nb_qf"/> are in use.</summary>
    public fixed byte qf[MaxQueueFamilies * 16];

    public int nb_qf;

    /// <summary><c>VkDeviceQueueCreateFlags</c> for <c>vkGetDeviceQueue2</c>.</summary>
    public uint queue_flags;

    public AVVulkanDeviceQueueFamily QueueFamily(int i)
    {
        fixed (byte* p = qf)
            return ((AVVulkanDeviceQueueFamily*)p)[i];
    }
}

/// <summary>
/// FFmpeg's <c>AVVulkanFramesContext</c>: what <c>AVHWFramesContext.hwctx</c> points to for a
/// Vulkan pool.
/// </summary>
/// <remarks>x64 layout: <c>format</c> at 88 (8 ints), <c>lock_frame</c> at 128, <c>unlock_frame</c> at 136.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVVulkanFramesContext
{
    /// <summary><c>VkImageTiling</c>.</summary>
    public int tiling;

    /// <summary><c>VkImageUsageFlagBits</c>.</summary>
    public uint usage;

    public nint create_pnext;

    public fixed ulong alloc_pnext[8];

    /// <summary><c>AVVkFrameFlags</c>.</summary>
    public int flags;

    /// <summary><c>VkImageCreateFlags</c>.</summary>
    public uint img_flags;

    /// <summary><c>VkFormat</c> of each image.</summary>
    public fixed int format[8];

    public int nb_layers;

    /// <summary>Locks a frame's properties, just before a submission that uses it.</summary>
    public delegate* unmanaged<AVHWFramesContext*, AVVkFrame*, void> lock_frame;

    /// <summary>Unlocks a frame <see cref="lock_frame"/> locked, right after the submission.</summary>
    public delegate* unmanaged<AVHWFramesContext*, AVVkFrame*, void> unlock_frame;
}

/// <summary>
/// FFmpeg's <c>AVVkFrame</c>: what <c>AVFrame.data[0]</c> points to for a Vulkan hardware frame.
/// </summary>
/// <remarks>
/// x64 layout: <c>img</c> at 0, <c>tiling</c> at 64, <c>mem</c> at 72, <c>size</c> at 136,
/// <c>flags</c> at 200, <c>access</c> at 208 (<c>VkAccessFlagBits2</c> since FFmpeg 9.0),
/// <c>layout</c> at 272, <c>sem</c> at 304, <c>sem_value</c> at 368, <c>offset</c> at 432,
/// <c>queue_family</c> at 496, <c>internal</c> at 528.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVVkFrame
{
    /// <summary><c>VkImage</c>: one for a multi-planar format, or one per plane.</summary>
    public fixed ulong img[8];

    /// <summary><c>VkImageTiling</c>.</summary>
    public int tiling;

    /// <summary><c>VkDeviceMemory</c>.</summary>
    public fixed ulong mem[8];

    /// <summary><c>size_t</c>.</summary>
    public fixed ulong size[8];

    /// <summary><c>VkMemoryPropertyFlagBits</c>.</summary>
    public uint flags;

    /// <summary><c>VkAccessFlagBits2</c> of each image's last access, updated after every barrier.</summary>
    public fixed ulong access[8];

    /// <summary><c>VkImageLayout</c> of each image, updated after every barrier.</summary>
    public fixed int layout[8];

    /// <summary>Timeline <c>VkSemaphore</c> of each image.</summary>
    public fixed ulong sem[8];

    /// <summary>The value each image's semaphore reaches when it becomes accessible.</summary>
    public fixed ulong sem_value[8];

    /// <summary><c>ptrdiff_t</c> binding offset of each image.</summary>
    public fixed long offset[8];

    /// <summary>Queue family of each image; <c>VK_QUEUE_FAMILY_IGNORED</c> when shared concurrently.</summary>
    public fixed uint queue_family[8];

    public nint @internal;
}
