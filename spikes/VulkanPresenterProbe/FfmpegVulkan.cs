using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding;

namespace VulkanPresenterProbe;

// Mirrors of libavutil/hwcontext_vulkan.h at FFmpeg e47273f4d9 (n9.0.1-11), the pinned runtime.
// FF_API_VULKAN_SYNC_QUEUES is still on in avutil 61, so the deprecated lock_queue/unlock_queue
// pointers are in AVVulkanDeviceContext. AV_NUM_DATA_POINTERS is 8.

[StructLayout(LayoutKind.Sequential)]
internal struct AVVulkanDeviceQueueFamily
{
    public int idx;
    public int num;
    public uint flags;        // VkQueueFlagBits
    public uint video_caps;   // VkVideoCodecOperationFlagBitsKHR
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VkPhysicalDeviceFeatures2Blob
{
    public int sType;
    public nint pNext;
    public fixed uint features[55];   // VkPhysicalDeviceFeatures: 55 VkBool32
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVVulkanDeviceContext
{
    public nint alloc;
    public nint get_proc_addr;
    public nint inst;
    public nint phys_dev;
    public nint act_dev;
    public VkPhysicalDeviceFeatures2Blob device_features;
    public byte** enabled_inst_extensions;
    public int nb_enabled_inst_extensions;
    public byte** enabled_dev_extensions;
    public int nb_enabled_dev_extensions;
    public delegate* unmanaged<AVHWDeviceContext*, uint, uint, void> lock_queue;
    public delegate* unmanaged<AVHWDeviceContext*, uint, uint, void> unlock_queue;
    public fixed byte qf_bytes[64 * 16];
    public int nb_qf;
    public uint queue_flags;

    public AVVulkanDeviceQueueFamily Qf(int i)
    {
        fixed (byte* p = qf_bytes)
            return ((AVVulkanDeviceQueueFamily*)p)[i];
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVVulkanFramesContext
{
    public int tiling;
    public uint usage;
    public nint create_pnext;
    public fixed ulong alloc_pnext[8];
    public int flags;
    public uint img_flags;
    public fixed int format[8];
    public int nb_layers;
    public delegate* unmanaged<AVHWFramesContext*, AVVkFrame*, void> lock_frame;
    public delegate* unmanaged<AVHWFramesContext*, AVVkFrame*, void> unlock_frame;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVVkFrame
{
    public fixed ulong img[8];          // VkImage
    public int tiling;
    public fixed ulong mem[8];          // VkDeviceMemory
    public fixed ulong size[8];         // size_t
    public uint flags;                  // VkMemoryPropertyFlagBits
    public fixed ulong access[8];       // VkAccessFlagBits2
    public fixed int layout[8];         // VkImageLayout
    public fixed ulong sem[8];          // VkSemaphore (timeline)
    public fixed ulong sem_value[8];
    public fixed long offset[8];        // ptrdiff_t
    public fixed uint queue_family[8];
    public nint @internal;
}

/// <summary>The FFmpeg objects behind one Vulkan-decoded frame.</summary>
internal readonly unsafe struct VulkanFrameRefs(
    AVVkFrame* frame,
    AVHWFramesContext* framesContext,
    AVVulkanFramesContext* vulkanFrames,
    AVHWDeviceContext* deviceContext,
    AVVulkanDeviceContext* vulkanDevice,
    int swFormat,
    bool bt709,
    bool fullRange)
{
    public AVVkFrame* Frame { get; } = frame;
    public AVHWFramesContext* FramesContext { get; } = framesContext;
    public AVVulkanFramesContext* VulkanFrames { get; } = vulkanFrames;
    public AVHWDeviceContext* DeviceContext { get; } = deviceContext;
    public AVVulkanDeviceContext* VulkanDevice { get; } = vulkanDevice;
    public int SwFormat { get; } = swFormat;

    /// <summary>The frame says BT.709. Unspecified is BT.601, as FrameFlow's CPU path treats it.</summary>
    public bool Bt709 { get; } = bt709;

    public bool FullRange { get; } = fullRange;
}

internal static unsafe class FfmpegVulkan
{
    // Checked once: the layouts above are only as good as these offsets.
    public static void CheckLayouts()
    {
        AVVulkanDeviceContext d = default;
        var db = (byte*)&d;
        Check("device_features", (byte*)&d.device_features - db, 40);
        Check("enabled_inst_extensions", (byte*)&d.enabled_inst_extensions - db, 280);
        Check("lock_queue", (byte*)&d.lock_queue - db, 312);
        Check("qf", d.qf_bytes - db, 328);
        Check("nb_qf", (byte*)&d.nb_qf - db, 1352);

        AVVulkanFramesContext f = default;
        Check("lock_frame", (byte*)&f.lock_frame - (byte*)&f, 128);

        AVVkFrame v = default;
        var vb = (byte*)&v;
        Check("mem", (byte*)v.mem - vb, 72);
        Check("access", (byte*)v.access - vb, 208);
        Check("layout", (byte*)v.layout - vb, 272);
        Check("sem", (byte*)v.sem - vb, 304);
        Check("sem_value", (byte*)v.sem_value - vb, 368);
        Check("queue_family", (byte*)v.queue_family - vb, 496);
        Check("internal", (byte*)&v.@internal - vb, 528);

        static void Check(string field, long actual, int expected)
        {
            if (actual != expected)
                throw new InvalidOperationException($"{field} is at {actual}, expected {expected}.");
        }
    }

    private static readonly FieldInfo HandleField =
        typeof(GpuVideoFrame).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(GpuVideoFrame), "_handle");

    /// <summary>
    /// The AVFrame behind a GpuVideoFrame, by reflection. Nothing public exposes it; what a real
    /// accessor needs to return is one of the things this spike is for.
    /// </summary>
    public static bool TryGet(GpuVideoFrame frame, out VulkanFrameRefs refs)
    {
        refs = default;
        if (frame.Backend != FrameFlow.Media.HardwareDecodeBackendKind.Vulkan)
            return false;
        if (HandleField.GetValue(frame) is not SafeHandle handle || handle.IsInvalid)
            return false;

        var avFrame = (AVFrame*)handle.DangerousGetHandle();
        if (avFrame->hw_frames_ctx == null)
            return false;

        var framesCtx = (AVHWFramesContext*)avFrame->hw_frames_ctx->data;
        var deviceCtx = framesCtx->device_ctx;
        refs = new VulkanFrameRefs(
            (AVVkFrame*)avFrame->data[0],
            framesCtx,
            (AVVulkanFramesContext*)framesCtx->hwctx,
            deviceCtx,
            (AVVulkanDeviceContext*)deviceCtx->hwctx,
            (int)framesCtx->sw_format,
            avFrame->colorspace == AVColorSpace.AVCOL_SPC_BT709,
            avFrame->color_range == AVColorRange.AVCOL_RANGE_JPEG);
        return true;
    }

    public static List<string> EnabledDeviceExtensions(AVVulkanDeviceContext* dev)
    {
        var names = new List<string>();
        for (int i = 0; i < dev->nb_enabled_dev_extensions; i++)
            names.Add(Marshal.PtrToStringUTF8((nint)dev->enabled_dev_extensions[i]) ?? "");
        return names;
    }
}
