using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="GpuVideoFrame.TryLockVulkanImage"/> and <see cref="GpuVideoFrame.TryGetVulkanDevice"/>
/// surface a Vulkan frame's image, its synchronisation, and the device it lives on (#535). The
/// semaphore is checked by waiting on it and signalling it from the host, through the device's own
/// loader, and the protocol by decoding a whole clip while every frame is touched that way.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class GpuVideoFrameVulkanTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-video-h264-yuv420p.mp4";

    // The fixture's expectedVideoFrames in tests/corpus/test-expectations.json.
    private const int FixtureFrames = 72;

    // VK_FORMAT_G8_B8R8_2PLANE_420_UNORM, VK_QUEUE_VIDEO_DECODE_BIT_KHR and
    // VK_VIDEO_CODEC_OPERATION_DECODE_H264_BIT_KHR.
    private const int FormatNv12 = 1000156003;
    private const uint QueueVideoDecode = 0x20;
    private const uint DecodeH264 = 0x1;

    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(30);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task AVulkanFrame_SurfacesItsImage_AndTheSemaphoreThatSaysItIsWritten()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        using var frame = await FirstFrameAsync(demux, decoder);
        Assert.True(frame.TryGetVulkanDevice(out var device));
        using (device)
        {
            var host = new HostVulkan(device);
            ulong semaphore, value;
            Assert.True(frame.TryLockVulkanImage(out var image));
            using (image)
            {
                Assert.NotEqual(0UL, image.Image);
                Assert.NotEqual(0UL, image.Semaphore);
                Assert.Equal(FormatNv12, image.Format);
                Assert.True(image.WaitValue > 0, "the wait value is zero, which every timeline semaphore starts at");
                Assert.Equal(image.WaitValue + 1, image.SignalValue);
                semaphore = image.Semaphore;
                value = image.WaitValue;
            }

            // The decoder signals the semaphore to the reported value once the frame is written.
            Assert.True(host.Wait(semaphore, value, FailureBound), $"the semaphore never reached {value}");
            Assert.True(host.CounterValue(semaphore) >= value);
        }
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task AVulkanFrame_ReportsTheDeviceItWasDecodedOn()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        using var frame = await FirstFrameAsync(demux, decoder);

        Assert.True(frame.TryGetVulkanDevice(out var device));
        using (device)
        {
            Assert.NotEqual(nint.Zero, device.Instance);
            Assert.NotEqual(nint.Zero, device.PhysicalDevice);
            Assert.NotEqual(nint.Zero, device.Device);
            Assert.NotEqual(nint.Zero, device.GetInstanceProcAddr);
            Assert.Contains("VK_KHR_video_decode_queue", device.EnabledDeviceExtensions);

            var decode = Assert.Single(device.QueueFamilies, f => (f.Flags & QueueVideoDecode) != 0);
            Assert.True((decode.VideoCodecOperations & DecodeH264) != 0, $"the decode family's operations are 0x{decode.VideoCodecOperations:x}");
            Assert.True(decode.QueueCount > 0);

            using (device.LockQueue(decode.Index, 0))
            {
            }

            uint unknown = device.QueueFamilies.Max(f => f.Index) + 1;
            Assert.Throws<ArgumentOutOfRangeException>(() => { using var _ = device.LockQueue(unknown, 0); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { using var _ = device.LockQueue(decode.Index, (uint)decode.QueueCount); });
        }
    }

    /// <summary>
    /// A commit is what FFmpeg's next use of the image, and the next lock, start from: the value to
    /// wait on moves to the one the submission signals.
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task ACommit_IsWhatTheNextLockSees()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        using var frame = await FirstFrameAsync(demux, decoder);
        Assert.True(frame.TryGetVulkanDevice(out var device));
        using (device)
        {
            var host = new HostVulkan(device);
            ulong signalled;
            int layout;
            Assert.True(frame.TryLockVulkanImage(out var first));
            using (first)
            {
                signalled = host.TouchUnchanged(first);
                layout = first.Layout;
            }

            Assert.True(frame.TryLockVulkanImage(out var second));
            using (second)
            {
                Assert.Equal(signalled, second.WaitValue);
                Assert.Equal(layout, second.Layout);
                Assert.Equal(signalled, host.CounterValue(second.Semaphore));
            }
        }
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task ALockDisposedWithoutACommit_LeavesTheFrameAsItWas()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        using var frame = await FirstFrameAsync(demux, decoder);

        Assert.True(frame.TryLockVulkanImage(out var first));
        var (waitValue, layout, access) = (first.WaitValue, first.Layout, first.Access);
        first.Dispose();
        first.Dispose();

        Assert.True(frame.TryLockVulkanImage(out var second));
        using (second)
        {
            Assert.Equal(waitValue, second.WaitValue);
            Assert.Equal(layout, second.Layout);
            Assert.Equal(access, second.Access);
        }

        Assert.Throws<InvalidOperationException>(() => first.Commit(layout, access));
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task ALock_CoversOneSubmission()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        using var frame = await FirstFrameAsync(demux, decoder);
        Assert.True(frame.TryGetVulkanDevice(out var device));
        using (device)
        {
            var host = new HostVulkan(device);
            Assert.True(frame.TryLockVulkanImage(out var image));
            using (image)
            {
                host.TouchUnchanged(image);
                var error = Assert.Throws<InvalidOperationException>(() => image.Commit(image.Layout, image.Access));
                Assert.Contains("already committed", error.Message, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The protocol is FFmpeg's, not only this class's: with every frame waited on, signalled from
    /// the host and committed as it arrives, the decoder carries on from the values it was left and
    /// decodes the whole clip. With the commit set to a value nothing signals, this test does not
    /// finish: the decoder waits in native code, where the cancellation does not reach.
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task EveryFrameTouched_TheDecoderCarriesOn()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        await QueueAllAsync(demux, decoder);

        VulkanDevice? device = null;
        int touched = 0;
        try
        {
            using var cts = new CancellationTokenSource(FailureBound);
            await foreach (var decoded in decoder.DecodeAsync(cts.Token))
            {
                using var frame = Assert.IsType<GpuVideoFrame>(decoded);
                if (device is null)
                    Assert.True(frame.TryGetVulkanDevice(out device));
                var host = new HostVulkan(device);
                Assert.True(frame.TryLockVulkanImage(out var image));
                using (image)
                    host.TouchUnchanged(image);
                touched++;
            }
        }
        finally
        {
            device?.Dispose();
        }

        Assert.Equal(FixtureFrames, touched);
        Assert.Equal(0, decoder.GetDiagnostics().DecodeErrors);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task ADecoderOnABorrowedDevice_ReportsThatDevice()
    {
        using var borrowed = HardwareDevice.Create(HardwareDecodeBackendKind.Vulkan);
        Assert.True(borrowed.TryGetVulkanDevice(out var expected));
        using (expected)
        {
            await using var demux = await OpenAsync();
            await using var decoder = VideoDecoder.Open(
                demux.FormatContextPtr,
                demux.MediaInfo.VideoStreams[0].StreamIndex,
                new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required },
                fixture.Capabilities,
                loggerFactory: null,
                new VideoDecoderOptions { Device = borrowed });
            decoder.YieldHardwareFrames = true;
            using var frame = await FirstFrameAsync(demux, decoder);

            Assert.True(frame.TryGetVulkanDevice(out var actual));
            using (actual)
            {
                Assert.Equal(expected.Device, actual.Device);
                Assert.Equal(expected.Instance, actual.Instance);
            }
        }
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Vulkan, Fixture)]
    public async Task ADisposedFrame_LocksNothing()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Vulkan);
        var frame = await FirstFrameAsync(demux, decoder);

        frame.Dispose();

        Assert.False(frame.TryLockVulkanImage(out var image));
        Assert.Null(image);
        Assert.False(frame.TryGetVulkanDevice(out var device));
        Assert.Null(device);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Fixture)]
    public async Task AD3D11VaFrame_HasNoVulkanImage()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.D3D11Va);
        using var frame = await FirstFrameAsync(demux, decoder);

        Assert.False(frame.TryLockVulkanImage(out _));
        Assert.False(frame.TryGetVulkanDevice(out _));

        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D11Va);
        Assert.False(device.TryGetVulkanDevice(out _));
    }

    private VideoDecoder OpenHardware(DemuxSession demux, HardwareDecodeBackendKind backend)
    {
        var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required, PreferredBackends = [backend] },
            fixture.Capabilities,
            loggerFactory: null
        );
        Assert.Equal(backend, decoder.HardwareBackend);
        decoder.YieldHardwareFrames = true;
        return decoder;
    }

    private static async Task<GpuVideoFrame> FirstFrameAsync(DemuxSession demux, VideoDecoder decoder)
    {
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        return Assert.IsType<GpuVideoFrame>(frames.Current);
    }

    private static async Task<DemuxSession> OpenAsync(string clip = Fixture)
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing.");
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
    }

    /// <summary>Queues every video packet and completes the queue.</summary>
    private static async Task QueueAllAsync(DemuxSession demux, VideoDecoder decoder)
    {
        int streamIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (new AvPacketAccessor(read).StreamIndex == streamIndex)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                FFAvCodec.av_packet_unref(read);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
    }

    /// <summary>
    /// Waits on and signals a timeline semaphore from the host, through the functions the device's
    /// own loader returns. Enough Vulkan to test the protocol without a binding.
    /// </summary>
    private sealed unsafe class HostVulkan
    {
        private const int StructureTypeSemaphoreWaitInfo = 1000207004;
        private const int StructureTypeSemaphoreSignalInfo = 1000207005;

        private readonly nint _device;
        private readonly delegate* unmanaged<nint, SemaphoreWaitInfo*, ulong, int> _waitSemaphores;
        private readonly delegate* unmanaged<nint, SemaphoreSignalInfo*, int> _signalSemaphore;
        private readonly delegate* unmanaged<nint, ulong, ulong*, int> _getCounterValue;

        public HostVulkan(VulkanDevice device)
        {
            _device = device.Device;
            var getInstanceProcAddr = (delegate* unmanaged<nint, nint, nint>)device.GetInstanceProcAddr;
            var getDeviceProcAddr = (delegate* unmanaged<nint, nint, nint>)Load(getInstanceProcAddr, device.Instance, "vkGetDeviceProcAddr");

            _waitSemaphores = (delegate* unmanaged<nint, SemaphoreWaitInfo*, ulong, int>)Load(getDeviceProcAddr, _device, "vkWaitSemaphores");
            _signalSemaphore = (delegate* unmanaged<nint, SemaphoreSignalInfo*, int>)Load(getDeviceProcAddr, _device, "vkSignalSemaphore");
            _getCounterValue = (delegate* unmanaged<nint, ulong, ulong*, int>)Load(getDeviceProcAddr, _device, "vkGetSemaphoreCounterValue");
        }

        /// <summary>
        /// Waits for the image, signals that the host is done with it, and commits with the layout
        /// and access unchanged, since the host touched no pixels. Returns the value signalled.
        /// </summary>
        public ulong TouchUnchanged(VulkanImageLock image)
        {
            Assert.True(Wait(image.Semaphore, image.WaitValue, FailureBound), $"the semaphore never reached {image.WaitValue}");
            Signal(image.Semaphore, image.SignalValue);
            image.Commit(image.Layout, image.Access);
            return image.SignalValue;
        }

        public bool Wait(ulong semaphore, ulong value, TimeSpan timeout)
        {
            var info = new SemaphoreWaitInfo
            {
                SType = StructureTypeSemaphoreWaitInfo,
                SemaphoreCount = 1,
                Semaphores = &semaphore,
                Values = &value,
            };
            int result = _waitSemaphores(_device, &info, (ulong)timeout.TotalNanoseconds);
            return result switch
            {
                0 => true,
                2 => false, // VK_TIMEOUT
                _ => throw new InvalidOperationException($"vkWaitSemaphores returned {result}."),
            };
        }

        public void Signal(ulong semaphore, ulong value)
        {
            var info = new SemaphoreSignalInfo { SType = StructureTypeSemaphoreSignalInfo, Semaphore = semaphore, Value = value };
            int result = _signalSemaphore(_device, &info);
            if (result != 0)
                throw new InvalidOperationException($"vkSignalSemaphore returned {result}.");
        }

        public ulong CounterValue(ulong semaphore)
        {
            ulong value;
            int result = _getCounterValue(_device, semaphore, &value);
            return result == 0 ? value : throw new InvalidOperationException($"vkGetSemaphoreCounterValue returned {result}.");
        }

        private static nint Load(delegate* unmanaged<nint, nint, nint> getProcAddr, nint handle, string name)
        {
            nint utf8 = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                nint function = getProcAddr(handle, utf8);
                return function != nint.Zero ? function : throw new InvalidOperationException($"{name} did not load.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        /// <summary><c>VkSemaphoreWaitInfo</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SemaphoreWaitInfo
        {
            public int SType;
            public nint Next;
            public uint Flags;
            public uint SemaphoreCount;
            public ulong* Semaphores;
            public ulong* Values;
        }

        /// <summary><c>VkSemaphoreSignalInfo</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SemaphoreSignalInfo
        {
            public int SType;
            public nint Next;
            public ulong Semaphore;
            public ulong Value;
        }
    }
}

/// <summary>
/// The Vulkan hwcontext mirrors against the offsets <c>hwcontext_vulkan.h</c> gives them in FFmpeg
/// 9.0 on x64 (#535). A mirror that drifts reads the wrong field with no error, so this is checked
/// without FFmpeg or a GPU.
/// </summary>
public sealed unsafe class AvVulkanLayoutTests
{
    [Fact]
    public void TheDeviceContextMatchesTheHeader()
    {
        AVVulkanDeviceContext d = default;
        var b = (byte*)&d;
        Assert.Equal(16, (byte*)&d.inst - b);
        Assert.Equal(32, (byte*)&d.act_dev - b);
        Assert.Equal(40, d.device_features - b);
        Assert.Equal(280, (byte*)&d.enabled_inst_extensions - b);
        Assert.Equal(296, (byte*)&d.enabled_dev_extensions - b);
        Assert.Equal(312, (byte*)&d.lock_queue - b);
        Assert.Equal(320, (byte*)&d.unlock_queue - b);
        Assert.Equal(328, d.qf - b);
        Assert.Equal(1352, (byte*)&d.nb_qf - b);
        Assert.Equal(16, sizeof(AVVulkanDeviceQueueFamily));
        Assert.Equal(1360, sizeof(AVVulkanDeviceContext));
    }

    [Fact]
    public void TheFramesContextMatchesTheHeader()
    {
        AVVulkanFramesContext f = default;
        var b = (byte*)&f;
        Assert.Equal(80, (byte*)&f.flags - b);
        Assert.Equal(88, (byte*)f.format - b);
        Assert.Equal(128, (byte*)&f.lock_frame - b);
        Assert.Equal(136, (byte*)&f.unlock_frame - b);
    }

    [Fact]
    public void TheFrameMatchesTheHeader()
    {
        AVVkFrame v = default;
        var b = (byte*)&v;
        Assert.Equal(64, (byte*)&v.tiling - b);
        Assert.Equal(72, (byte*)v.mem - b);
        Assert.Equal(200, (byte*)&v.flags - b);
        Assert.Equal(208, (byte*)v.access - b);
        Assert.Equal(272, (byte*)v.layout - b);
        Assert.Equal(304, (byte*)v.sem - b);
        Assert.Equal(368, (byte*)v.sem_value - b);
        Assert.Equal(432, (byte*)v.offset - b);
        Assert.Equal(496, (byte*)v.queue_family - b);
        Assert.Equal(528, (byte*)&v.@internal - b);
    }
}
