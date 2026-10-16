using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The presenter treats every device-loss code as device loss, <c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c>
/// among them (#530), whether Vortice throws it or another exception carries it.
/// </summary>
public sealed class D3D11DeviceLossTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0006))] // DXGI_ERROR_DEVICE_HUNG
    [InlineData(unchecked((int)0x887A0007))] // DXGI_ERROR_DEVICE_RESET
    [InlineData(unchecked((int)0x887A0020))] // DXGI_ERROR_DRIVER_INTERNAL_ERROR
    [InlineData(unchecked((int)0x88760870))] // D3DDDIERR_DEVICEREMOVED
    public void ADeviceLossCode_IsDeviceLoss(int hresult)
    {
        Assert.True(D3D11DeviceLoss.IsDeviceLostCode(hresult));
        Assert.True(D3D11DeviceLoss.IsDeviceLost(new SharpGenException(new Result(hresult))));
        Assert.True(D3D11DeviceLoss.IsDeviceLost(new COMException("lost", hresult)));
    }

    [Theory]
    [InlineData(0)] // S_OK
    [InlineData(unchecked((int)0x887A0001))] // DXGI_ERROR_INVALID_CALL: the application's mistake
    [InlineData(unchecked((int)0x8007000E))] // E_OUTOFMEMORY
    public void AnotherCode_IsNotDeviceLoss(int hresult)
    {
        Assert.False(D3D11DeviceLoss.IsDeviceLostCode(hresult));
        Assert.False(D3D11DeviceLoss.IsDeviceLost(new COMException("other", hresult)));
    }
}
