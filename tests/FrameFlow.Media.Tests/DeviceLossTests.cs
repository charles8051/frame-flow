using System.Runtime.InteropServices;
using FrameFlow.Media.Core;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Which HRESULTs mean a GPU device was lost, the once-per-device latch the device owner raises
/// its event from, and the category a failure on a lost device reports (#759, #530).
/// </summary>
public sealed class DeviceLossTests
{
    // Written as literals so a wrong constant in the code under test cannot agree with itself.
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceHung = unchecked((int)0x887A0006);
    private const int DeviceReset = unchecked((int)0x887A0007);
    private const int DriverInternalError = unchecked((int)0x887A0020);
    private const int DdiDeviceRemoved = unchecked((int)0x88760870);
    private const int InvalidCall = unchecked((int)0x887A0001);
    private const int InvalidData = -1094995529; // AVERROR_INVALIDDATA

    [Theory]
    [InlineData(DeviceRemoved)]
    [InlineData(DeviceHung)]
    [InlineData(DeviceReset)]
    [InlineData(DriverInternalError)]
    [InlineData(DdiDeviceRemoved)]
    public void EachRemovedReason_IsDeviceLoss(int hresult)
    {
        Assert.True(DeviceLoss.IsDeviceLostCode(hresult));
        Assert.True(DeviceLostException.IsDeviceLostCode(hresult));
        Assert.Contains(hresult, DeviceLoss.Codes);
    }

    [Theory]
    [InlineData(0)] // S_OK: the device is live
    [InlineData(InvalidCall)] // the application's mistake, not the driver's
    [InlineData(unchecked((int)0x8007000E))] // E_OUTOFMEMORY
    [InlineData(unchecked((int)0x887A0002))] // DXGI_ERROR_NOT_FOUND
    [InlineData(InvalidData)]
    public void OtherCodes_AreNotDeviceLoss(int hresult) =>
        Assert.False(DeviceLoss.IsDeviceLostCode(hresult));

    [Fact]
    public void TheList_HasExactlyTheFiveCodes() =>
        Assert.Equal(
            new[] { DeviceRemoved, DeviceHung, DeviceReset, DriverInternalError, DdiDeviceRemoved }.Order(),
            DeviceLoss.Codes.Order()
        );

    [Fact]
    public void ALiveDevice_RaisesNothing()
    {
        var (next, raise) = DeviceLossLatch.Live.Observe(0);

        Assert.False(raise);
        Assert.Equal(DeviceLossLatch.Live, next);
    }

    [Fact]
    public void ACodeThatIsNotLoss_RaisesNothing()
    {
        var (next, raise) = DeviceLossLatch.Live.Observe(InvalidCall);

        Assert.False(raise);
        Assert.False(next.Lost);
    }

    [Fact]
    public void TheFirstLoss_Raises_AndKeepsItsReason()
    {
        var (next, raise) = DeviceLossLatch.Live.Observe(DeviceReset);

        Assert.True(raise);
        Assert.True(next.Lost);
        Assert.Equal(DeviceReset, next.Reason);
    }

    [Theory]
    [InlineData(DeviceReset)] // a second decoder on the device reports the same reason
    [InlineData(DeviceRemoved)] // or another
    [InlineData(0)] // or reads S_OK, which does not bring a removed device back
    public void ALaterReport_RaisesNothing_AndKeepsTheFirstReason(int later)
    {
        var (lost, _) = DeviceLossLatch.Live.Observe(DeviceReset);

        var (next, raise) = lost.Observe(later);

        Assert.False(raise);
        Assert.True(next.Lost);
        Assert.Equal(DeviceReset, next.Reason);
    }

    [Fact]
    public void ManyReports_RaiseExactlyOnce()
    {
        var latch = DeviceLossLatch.Live;
        int raised = 0;
        foreach (var reason in new[] { 0, InvalidCall, DeviceHung, DeviceHung, DeviceReset, 0, DriverInternalError })
        {
            (latch, bool raise) = latch.Observe(reason);
            if (raise)
                raised++;
        }

        Assert.Equal(1, raised);
        Assert.Equal(DeviceHung, latch.Reason);
    }

    [Fact]
    public void TheException_CarriesItsReason_AsItsHResult()
    {
        var inner = new MediaException("decode", InvalidData, MediaFailureStage.ReceiveFrame);
        var lost = new DeviceLostException("lost", DriverInternalError, inner);

        Assert.Equal(DriverInternalError, lost.RemovedReason);
        Assert.Equal(DriverInternalError, lost.HResult);
        Assert.Same(inner, lost.InnerException);
        Assert.IsAssignableFrom<InvalidOperationException>(lost);
    }

    [Fact]
    public void ADecodeFailureOnALostDevice_IsDeviceLost_NotDecode()
    {
        var decode = new MediaException("decode", InvalidData, MediaFailureStage.ReceiveFrame);
        var lost = new DeviceLostException("lost", DeviceReset, decode);

        Assert.Equal(ErrorCategory.DeviceLost, MediaErrorClassification.Categorize(lost, ErrorCategory.System));
    }

    [Fact]
    public void DeviceLoss_WinsWhereverItIsInTheChain()
    {
        var lost = new DeviceLostException("lost", DeviceReset);
        var wrapped = new MediaException("decode", InvalidData, MediaFailureStage.ReceiveFrame, lost);

        Assert.Equal(ErrorCategory.DeviceLost, MediaErrorClassification.Categorize(wrapped, ErrorCategory.System));
    }

    [Theory]
    [InlineData(DeviceRemoved)]
    [InlineData(DriverInternalError)]
    public void AnExceptionCarryingALossHResult_IsDeviceLost(int hresult)
    {
        // As a D3D call on a removed device throws it, with no DeviceLostException around it.
        var com = new COMException("The GPU device instance has been suspended.", hresult);

        Assert.Equal(ErrorCategory.DeviceLost, MediaErrorClassification.Categorize(com, ErrorCategory.System));
        Assert.Equal(
            ErrorCategory.DeviceLost,
            MediaErrorClassification.Categorize(new InvalidOperationException("outer", com), ErrorCategory.System)
        );
    }

    [Fact]
    public void AnInvalidCallHResult_KeepsTheFallback() =>
        Assert.Equal(
            ErrorCategory.System,
            MediaErrorClassification.Categorize(new COMException("bad call", InvalidCall), ErrorCategory.System)
        );

    [Fact]
    public void APlaybackError_CarriesTheReason_BesideTheCategory()
    {
        var decode = new MediaException("decode", InvalidData, MediaFailureStage.ReceiveFrame);
        var lost = new DeviceLostException("lost", DeviceHung, decode);

        var error = new PlaybackError(
            MediaErrorClassification.Categorize(lost, ErrorCategory.System),
            "Playback failed",
            new InvalidOperationException("worker", lost)
        );

        Assert.Equal(ErrorCategory.DeviceLost, error.Category);
        Assert.Same(lost, error.DeviceLoss);
        Assert.Equal(DeviceHung, error.DeviceLoss!.RemovedReason);
        Assert.Same(decode, error.MediaFailure);
    }

    [Fact]
    public void APlaybackErrorWithNoLoss_HasNoDeviceLoss()
    {
        var error = new PlaybackError(
            ErrorCategory.Decode,
            "decode",
            new MediaException("decode", InvalidData, MediaFailureStage.ReceiveFrame)
        );

        Assert.Null(error.DeviceLoss);
    }
}
