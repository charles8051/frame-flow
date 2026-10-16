using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A shared <see cref="HardwareDevice"/> raises <see cref="HardwareDevice.DeviceLost"/> once when a
/// decoder on it reports a loss (#759), and reads a live device as live. No test removes a real
/// device: the reports are fed in, and the native read is checked against a device that is up.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class HardwareDeviceLossTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string H264 = "test-video-h264-yuv420p.mp4";
    private const int DeviceReset = unchecked((int)0x887A0007);
    private const int DeviceHung = unchecked((int)0x887A0006);
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(30);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, H264)]
    public async Task TheFirstLossReport_RaisesTheEvent_OnceAndWithItsReason()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D11Va);
        var raised = new TaskCompletionSource<(object? Sender, HardwareDeviceLostEventArgs Args)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        device.DeviceLost += (sender, args) =>
        {
            Interlocked.Increment(ref count);
            raised.TrySetResult((sender, args));
        };

        Assert.False(device.ReportRemovedReason(0));
        Assert.False(device.IsLost);

        Assert.True(device.ReportRemovedReason(DeviceReset));
        Assert.True(device.IsLost);

        // Every later report, from any decoder on the device, is answered synchronously: none of
        // them queues a second raise.
        Assert.False(device.ReportRemovedReason(DeviceReset));
        Assert.False(device.ReportRemovedReason(DeviceHung));

        var (sender, args) = await raised.Task.WaitAsync(FailureBound);
        Assert.Same(device, sender);
        Assert.Equal(DeviceReset, args.RemovedReason);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, H264)]
    public async Task TheEvent_IsRaisedOffTheReportingThread()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D11Va);
        var handlerThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The handler blocks until the report has returned. Raised on the reporting thread, the
        // report would never return and the test would fail at its bound.
        using var reported = new ManualResetEventSlim();
        device.DeviceLost += (_, _) =>
        {
            reported.Wait(FailureBound);
            handlerThread.TrySetResult(Environment.CurrentManagedThreadId);
        };

        Assert.True(device.ReportRemovedReason(DeviceReset));
        reported.Set();

        Assert.NotEqual(Environment.CurrentManagedThreadId, await handlerThread.Task.WaitAsync(FailureBound));
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, H264)]
    public void ALiveD3D11Device_ReportsNoRemovedReason() =>
        AssertLive(HardwareDecodeBackendKind.D3D11Va);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public void ALiveD3D12Device_ReportsNoRemovedReason() =>
        AssertLive(HardwareDecodeBackendKind.D3D12Va);

    /// <summary>
    /// A decoder on a lent device asks it after a fault. On a live device the answer is
    /// <c>S_OK</c> and the device is not marked lost.
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, H264)]
    public async Task ADecoderOnALiveLentDevice_ProbesItAsLive()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D11Va);
        await using var demux = (DemuxSession)await new DemuxSessionFactory().OpenAsync(
            MediaSource.FromFile(TestEnvironment.GetCorpusFile(H264)!));
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required },
            fixture.Capabilities,
            loggerFactory: null,
            new VideoDecoderOptions { Device = device });

        Assert.Equal(HardwareDecodeBackendKind.D3D11Va, decoder.HardwareBackend);

        Assert.Equal(0, ((IDecodeCodec<IVideoFrame>)decoder).ProbeDeviceRemoved());
        Assert.False(device.IsLost);
    }

    private static void AssertLive(HardwareDecodeBackendKind backend)
    {
        using var device = HardwareDevice.Create(backend);
        nint reference = device.Borrow();
        try
        {
            Assert.Equal(0, DeviceRemovedReason.Query(reference, backend));
        }
        finally
        {
            FFAvUtil.av_buffer_unref(ref reference);
        }
    }
}
