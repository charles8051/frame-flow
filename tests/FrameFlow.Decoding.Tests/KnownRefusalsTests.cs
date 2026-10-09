using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The table of backends <c>Auto</c> does not bind for a codec (#574), and the rule for when it
/// governs. Pure: no FFmpeg, no device.
/// </summary>
public sealed class KnownRefusalsTests
{
    private const int H264CodecId = 27;

    [Fact]
    public void CudaIsRefusedForMjpeg()
    {
        var refusal = KnownRefusals.Find(
            KnownRefusals.Builtin,
            HardwareDecodeBackendKind.Cuda,
            KnownRefusals.MjpegCodecId
        );

        Assert.NotNull(refusal);
        Assert.Contains("#574", refusal.Value.Reason);
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, KnownRefusals.MjpegCodecId)]
    [InlineData(HardwareDecodeBackendKind.Vulkan, KnownRefusals.MjpegCodecId)]
    [InlineData(HardwareDecodeBackendKind.Cuda, H264CodecId)]
    public void NothingElseIsRefused(HardwareDecodeBackendKind backend, int codecId) =>
        Assert.Null(KnownRefusals.Find(KnownRefusals.Builtin, backend, codecId));

    [Fact]
    public void NoRowsRefuseNothing() =>
        Assert.Null(
            KnownRefusals.Find(
                [],
                HardwareDecodeBackendKind.Cuda,
                KnownRefusals.MjpegCodecId
            )
        );

    [Theory]
    [InlineData(HardwareDecodeMode.Auto, false, true)]
    [InlineData(HardwareDecodeMode.Auto, true, false)]
    [InlineData(HardwareDecodeMode.Required, false, false)]
    [InlineData(HardwareDecodeMode.Required, true, false)]
    [InlineData(HardwareDecodeMode.Disabled, false, false)]
    public void RefusalsGovernAutoWithoutABorrowedDevice(
        HardwareDecodeMode mode,
        bool borrowedDevice,
        bool expected
    ) => Assert.Equal(expected, KnownRefusals.Governs(mode, borrowedDevice));
}
